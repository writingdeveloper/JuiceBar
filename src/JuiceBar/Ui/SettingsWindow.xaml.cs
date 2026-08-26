using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using JuiceBar.Core;
using JuiceBar.Core.Platform;
using JuiceBar.Core.Power;
using JuiceBar.Core.Storage;
using JuiceBar.Core.Update;
using JuiceBar.Services;

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
            : "아직 설정하지 않았습니다. 요금을 설정해야 비용이 계산됩니다.";

        BudgetBox.Text = tariff.MonthlyBudget > 0 ? Number(tariff.MonthlyBudget) : string.Empty;
        BudgetUnit.Text = tariff.Symbol;

        BudgetModeRadio.IsChecked = profile.GaugeMode == GaugeMode.Budget;
        InstantModeRadio.IsChecked = profile.GaugeMode == GaugeMode.Instant;

        AutoStartCheck.IsChecked = AutoStart.IsEnabled();
        AutoUpdateCheck.IsChecked = profile.CheckForUpdates;

        VersionText.Text = UpdateService.CanReplaceItself
            ? $"현재 버전 {UpdateService.CurrentVersion} · GitHub 릴리스에서 하루 한 번 확인합니다."
            : $"현재 버전 {UpdateService.CurrentVersion} · 개발 빌드에서는 자동 교체를 하지 않습니다.";

        CpuIdleBox.Text = Number(profile.Estimation.CpuIdleWatts);
        CpuMaxBox.Text = Number(profile.Estimation.CpuMaxWatts);
        PawnIoStatus.Text = DescribeSensorAccess();

        ProfilePathText.Text = $"설정 위치: {ProfileDirectory()}";

        LoadChannels();
    }

    /// <summary>CPU 전력을 실측하고 있는지, 못 한다면 왜 못 하는지.</summary>
    private static string DescribeSensorAccess()
    {
        if (!PawnIoDetector.IsInstalled())
            return "PawnIO 드라이버가 없어 CPU 전력을 추정하고 있습니다. 설치하면 실측으로 바뀝니다.";

        return Elevation.IsElevated
            ? "PawnIO 설치됨 · 관리자 권한으로 실행 중 — CPU 전력을 실측합니다. 아래 값은 쓰이지 않습니다."
            : "PawnIO 는 설치돼 있지만 관리자 권한이 없어 CPU 전력을 추정하고 있습니다.";
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
            ValidationText.Text = "예산이 숫자가 아닙니다.";
            return;
        }

        if (!TryParse(CpuIdleBox.Text, out double cpuIdle) || !TryParse(CpuMaxBox.Text, out double cpuMax))
        {
            ValidationText.Text = "CPU 추정 값이 숫자가 아닙니다.";
            return;
        }

        if (cpuMax <= cpuIdle)
        {
            ValidationText.Text = "CPU 최대 W는 유휴 W보다 커야 합니다.";
            return;
        }

        var selectedChannels = _channels.Where(c => c.IsSelected).Select(c => c.Id).ToList();

        // 채널을 전부 끄면 측정값이 0이 되어 앱이 아무것도 못 한다.
        if (_channels.Count > 0 && selectedChannels.Count == 0)
        {
            ValidationText.Text = "전력 채널을 최소 하나는 선택해야 합니다.";
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
        };

        if (selectedChannels.Count > 0)
            profile = profile with { SelectedChannelIds = selectedChannels };

        _metering.UpdateProfile(profile);

        if (!ApplyAutoStart())
        {
            ValidationText.Text = "자동 시작 설정에 실패했습니다. 나머지 설정은 저장되었습니다.";
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

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
