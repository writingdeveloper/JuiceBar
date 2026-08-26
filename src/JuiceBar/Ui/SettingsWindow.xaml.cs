using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using JuiceBar.Core;
using JuiceBar.Core.Platform;
using JuiceBar.Core.Power;
using JuiceBar.Core.Storage;
using JuiceBar.Core.Update;
using JuiceBar.Services;
using JuiceBar.Core.Localization;

namespace JuiceBar.Ui;

/// <summary>
/// 요금을 뺀 나머지 설정.
///
/// 요금 자체는 마법사가 통째로 처리한다 — 통화·기호·검침일·세금·구간까지 AI 답변 하나로
/// 채워지므로, 여기에 입력란을 늘어놓으면 같은 일을 두 번 하게 만드는 셈이다.
/// 이 화면에는 지역과 무관하게 각자 정해야 하는 것만 둔다.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly MeteringService _metering;
    private readonly ObservableCollection<ChannelRow> _channels = [];

    public event EventHandler? WizardRequested;

    public SettingsWindow(MeteringService metering)
    {
        _metering = metering;
        InitializeComponent();

        ChannelList.ItemsSource = _channels;

        SourceInitialized += (_, _) => WindowEffects.Apply(this, ThemeManager.Current);

        LoadFromProfile();
    }

    private void LoadFromProfile()
    {
        var profile = _metering.Profile;
        var tariff = profile.Tariff;

        TariffSummaryText.Text = profile.IsTariffConfigured
            ? TariffSummary.Describe(tariff)
            : Loc.T("settings.tariffNotSet");

        BudgetBox.Text = tariff.MonthlyBudget > 0 ? Number(tariff.MonthlyBudget) : string.Empty;
        BudgetUnit.Text = tariff.Symbol;

        BudgetModeRadio.IsChecked = profile.GaugeMode == GaugeMode.Budget;
        InstantModeRadio.IsChecked = profile.GaugeMode == GaugeMode.Instant;

        AutoStartCheck.IsChecked = AutoStart.IsEnabled();
        AutoUpdateCheck.IsChecked = profile.CheckForUpdates;

        VersionText.Text = Loc.T(
            UpdateService.CanReplaceItself ? "settings.version" : "settings.versionDev",
            UpdateService.CurrentVersion);

        CpuIdleBox.Text = Number(profile.Estimation.CpuIdleWatts);
        CpuMaxBox.Text = Number(profile.Estimation.CpuMaxWatts);
        PawnIoStatus.Text = DescribeSensorAccess();
        ShowElevateButtonIfUseful();

        ProfilePathText.Text = Loc.T("settings.profilePath", ProfileDirectory());

        ShowCycleUsage();
        LoadLanguages(profile.Language);
        LoadChannels();
    }

    // ─────────────────────────── 언어 ───────────────────────────

    /// <summary>목록에 넣을 언어 하나. 이름은 그 언어 표기 그대로 보여 준다.</summary>
    private sealed record LanguageChoice(string Code, string Label);

    private bool _languageLoaded;

    private void LoadLanguages(string current)
    {
        var choices = new List<LanguageChoice>
        {
            new(Loc.AutoCode, Loc.T("settings.languageAuto")),
        };

        foreach (var language in Loc.Available)
            choices.Add(new LanguageChoice(language.Code, language.NativeName));

        LanguageBox.ItemsSource = choices;
        LanguageBox.SelectedItem = choices.FirstOrDefault(c => c.Code == current) ?? choices[0];

        _languageLoaded = true;
    }

    /// <summary>
    /// 고르는 즉시 화면 전체를 그 언어로 바꾼다.
    /// 저장을 눌러야 바뀌면 어떤 언어인지 확인하고 되돌릴 방법이 없다.
    /// </summary>
    private void OnLanguageChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_languageLoaded) return;
        if (LanguageBox.SelectedItem is not LanguageChoice choice) return;

        Loc.Use(choice.Code);

        // 번역이 아니라 코드에서 만든 글도 있어 다시 그린다.
        TariffSummaryText.Text = _metering.Profile.IsTariffConfigured
            ? TariffSummary.Describe(_metering.Profile.Tariff)
            : Loc.T("settings.tariffNotSet");

        VersionText.Text = Loc.T(
            UpdateService.CanReplaceItself ? "settings.version" : "settings.versionDev",
            UpdateService.CurrentVersion);

        PawnIoStatus.Text = DescribeSensorAccess();
        ProfilePathText.Text = Loc.T("settings.profilePath", ProfileDirectory());
        ShowCycleUsage();

        // "Windows 설정 따르기" 항목의 이름도 새 언어로 바뀌어야 한다.
        LoadLanguages(choice.Code);
    }

    /// <summary>CPU 전력을 실측하고 있는지, 못 한다면 왜 못 하는지.</summary>
    private string DescribeSensorAccess()
    {
        // 에너지 미터가 답이면 나머지는 물어볼 필요가 없다.
        // 이미 재고 있는 사람에게 드라이버 설치 안내를 남겨 두면 혼란만 준다.
        if (_metering.HasEnergyMeter)
            return Loc.T("settings.sensor.energyMeter");

        return SensorAccess.Advise(false, PawnIoDetector.IsInstalled(), Elevation.IsElevated) switch
        {
            SensorAdvice.InstallDriver => Loc.T("settings.sensor.noPawnIo"),
            SensorAdvice.RunElevated => Loc.T("settings.sensor.notElevated"),
            _ => Loc.T("settings.sensor.elevated"),
        };
    }

    /// <summary>승격만 하면 되는 상태일 때에만 다시 실행 버튼을 보여 준다.</summary>
    private void ShowElevateButtonIfUseful()
    {
        var advice = SensorAccess.Advise(
            _metering.HasEnergyMeter, PawnIoDetector.IsInstalled(), Elevation.IsElevated);

        ElevateButton.Visibility = advice == SensorAdvice.RunElevated
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnRelaunchElevated(object sender, RoutedEventArgs e)
    {
        if (!Elevation.TryRelaunchElevated()) return;

        // 새 프로세스가 우리 번호를 받아 우리가 물러날 때까지 기다린다.
        Application.Current.Shutdown();
    }

    // ─────────────────────── 이번 주기 누적 ───────────────────────

    private void ShowCycleUsage()
    {
        if (_metering.Latest is not { } latest)
        {
            CycleUsageText.Text = Loc.T("settings.cycleUsageUnknown");
            return;
        }

        var tariff = latest.Tariff;

        CycleUsageText.Text = Loc.T(
            "settings.cycleUsage",
            CurrencyFormatter.FormatKwh(latest.CycleKwh),
            CurrencyFormatter.Format(latest.CycleCost.Total, tariff.Currency, tariff.Symbol));
    }

    /// <summary>
    /// 이번 청구 주기의 누적을 지운다.
    ///
    /// 저장 버튼과 달리 즉시 반영된다. 지운 값을 되살릴 방법이 없으므로 한 번 더 묻는다.
    /// </summary>
    private void OnResetCycle(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            Loc.T("settings.resetConfirm"),
            Loc.T("settings.resetCycle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        double removedKwh = _metering.ResetCurrentCycle();

        ShowCycleUsage();
        ValidationText.Text = Loc.T("settings.resetDone", CurrencyFormatter.FormatKwh(removedKwh));
    }

    private void LoadChannels()
    {
        if (_metering.Latest is not { } latest) return;

        var selected = _metering.Profile.SelectedChannelIds.ToHashSet(StringComparer.Ordinal);

        foreach (var channel in latest.Power.Channels)
        {
            // 배터리 채널은 합산 대상이 아니라 검증용이라 목록에서 뺀다.
            if (channel.Kind == ChannelKind.Battery) continue;

            var row = new ChannelRow
            {
                Id = channel.Id,
                Label = channel.Label,
                HardwareName = channel.HardwareName,
                Watts = channel.Watts,
                IsSelected = selected.Contains(channel.Id),
            };

            row.PropertyChanged += (_, _) => UpdateChannelTotal();
            _channels.Add(row);
        }

        UpdateChannelTotal();
    }

    /// <summary>
    /// 선택한 채널의 합.
    /// 이 값이 실제 소비보다 크면 같은 전력이 두 번 세어지고 있다는 뜻이다.
    /// </summary>
    private void UpdateChannelTotal()
    {
        double total = _channels.Where(c => c.IsSelected).Sum(c => c.Watts);
        ChannelTotal.Text = $"{total:N1} W";
    }

    private static string ProfileDirectory()
        => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JuiceBar", "devices", DeviceIdentity.Current);

    // ─────────────────────────── 저장 ───────────────────────────

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryParse(BudgetBox.Text, out double budget))
        {
            ValidationText.Text = Loc.T("settings.error.budget");
            return;
        }

        if (!TryParse(CpuIdleBox.Text, out double cpuIdle) || !TryParse(CpuMaxBox.Text, out double cpuMax))
        {
            ValidationText.Text = Loc.T("settings.error.cpu");
            return;
        }

        if (cpuMax <= cpuIdle)
        {
            ValidationText.Text = Loc.T("settings.error.cpuOrder");
            return;
        }

        var selectedChannels = _channels.Where(c => c.IsSelected).Select(c => c.Id).ToList();

        // 채널을 전부 끄면 측정값이 0이 되어 앱이 아무것도 못 한다.
        if (_channels.Count > 0 && selectedChannels.Count == 0)
        {
            ValidationText.Text = Loc.T("settings.error.noChannel");
            return;
        }

        var profile = _metering.Profile with
        {
            Tariff = _metering.Profile.Tariff with { MonthlyBudget = budget },
            GaugeMode = InstantModeRadio.IsChecked == true ? GaugeMode.Instant : GaugeMode.Budget,
            Estimation = _metering.Profile.Estimation with
            {
                CpuIdleWatts = cpuIdle,
                CpuMaxWatts = cpuMax,
            },
            StartWithWindows = AutoStartCheck.IsChecked == true,
            CheckForUpdates = AutoUpdateCheck.IsChecked == true,
            Language = (LanguageBox.SelectedItem as LanguageChoice)?.Code ?? Loc.AutoCode,
        };

        if (selectedChannels.Count > 0)
            profile = profile with { SelectedChannelIds = selectedChannels };

        _metering.UpdateProfile(profile);

        if (!ApplyAutoStart())
        {
            // 끄지 못한 경우는 대개 예전 버전이 만든 예약 작업이 남아 있고 권한이 없어서다.
            // "실패했다"고만 하면 무엇을 해야 할지 알 수 없으므로 이유를 밝혀 준다.
            bool blockedByTask = AutoStartCheck.IsChecked != true
                && !Elevation.IsElevated
                && AutoStart.IsEnabled();

            ValidationText.Text = Loc.T(blockedByTask
                ? "settings.error.autoStartAdmin"
                : "settings.error.autoStart");
            return;
        }

        Close();
    }

    private bool ApplyAutoStart()
    {
        bool wanted = AutoStartCheck.IsChecked == true;
        if (wanted == AutoStart.IsEnabled()) return true;

        if (!wanted) return AutoStart.Disable();

        string? path = Environment.ProcessPath;
        return path is not null && AutoStart.Enable(path);
    }

    /// <summary>현재 문화권과 소수점 표기가 달라도 받아 준다. 빈 칸은 0으로 본다.</summary>
    private static bool TryParse(string? text, out double value)
    {
        text = text?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            value = 0;
            return true;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string Number(double value)
        => value == 0 ? "0" : value.ToString("0.####", CultureInfo.CurrentCulture);

    private void OnOpenWizard(object sender, RoutedEventArgs e)
    {
        Close();
        WizardRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        // 언어는 고르는 즉시 반영되므로, 저장하지 않고 닫으면 원래대로 되돌려야 한다.
        Loc.Use(_metering.Profile.Language);
        Close();
    }
}
