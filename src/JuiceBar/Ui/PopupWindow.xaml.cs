using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using JuiceBar.Core;
using JuiceBar.Core.Platform;
using JuiceBar.Core.Power;
using JuiceBar.Core.Storage;
using JuiceBar.Core.Update;
using JuiceBar.Core.Localization;

namespace JuiceBar.Ui;

/// <summary>
/// 트레이 아이콘을 누르면 뜨는 상세 창.
/// 포커스를 잃으면 스스로 닫힌다 — 상주 앱의 팝업은 그래야 방해가 되지 않는다.
/// </summary>
public partial class PopupWindow : Window
{
    private readonly MeteringService _metering;
    private readonly DispatcherTimer _ticker;

    public event EventHandler? SettingsRequested;
    public event EventHandler? RateWizardRequested;
    public event EventHandler? ExitRequested;

    public PopupWindow(MeteringService metering)
    {
        _metering = metering;
        InitializeComponent();

        DeviceLabel.Text = metering.Profile.DisplayName;
        UpdateModeButton();

        SourceInitialized += (_, _) => WindowEffects.Apply(this, ThemeManager.Current);
        // 화면을 캡처하거나 UI 를 손볼 때는 팝업이 포커스를 잃어도 남아 있어야 한다.
        if (!PinnedForDevelopment)
            Deactivated += (_, _) => Hide();
        KeyDown += OnKeyDown;

        // 누진 경고 카드가 나타나고 사라지면서 높이가 변한다. 그때마다 아래에 다시 붙인다.
        SizeChanged += (_, _) => { if (IsVisible) AnchorToBottomRight(); };

        // 20fps 면 숫자가 매끄럽게 흐르면서도 그리는 비용이 거의 없다.
        _ticker = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _ticker.Tick += (_, _) => RenderTickingCost();

        IsVisibleChanged += (_, _) =>
        {
            // 창이 숨겨져 있는 동안 계속 돌릴 이유가 없다.
            if (IsVisible) _ticker.Start(); else _ticker.Stop();
        };
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Hide();
    }

    private const double ScreenMargin = 12;

    /// <summary>
    /// 개발·스크린샷용. JUICEBAR_PIN_POPUP=1 로 두면 팝업이 실행과 동시에 열리고
    /// 포커스를 잃어도 닫히지 않는다. 평소 동작에는 영향이 없다.
    /// </summary>
    public static bool PinnedForDevelopment { get; } =
        Environment.GetEnvironmentVariable("JUICEBAR_PIN_POPUP") == "1";

    /// <summary>
    /// 트레이 근처, 작업표시줄을 피해 띄운다.
    ///
    /// SizeToContent 를 쓰기 때문에 창을 실제로 보여 주기 전에는 높이를 알 수 없다.
    /// 그래서 투명한 채로 띄우고, 레이아웃이 끝난 뒤 위치를 잡고 나서 서서히 나타나게 한다.
    /// 그렇지 않으면 잘못된 자리에 한 프레임 깜빡이고 제자리를 찾아간다.
    /// </summary>
    public void ShowNearTray()
    {
        Opacity = 0;
        Show();

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            AnchorToBottomRight();
            FadeIn();
            Activate();
            Focus();
        });
    }

    private void AnchorToBottomRight()
    {
        var workArea = SystemParameters.WorkArea;

        Left = workArea.Right - ActualWidth - ScreenMargin;
        Top = workArea.Bottom - ActualHeight - ScreenMargin;
    }

    private void FadeIn()
    {
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        BeginAnimation(OpacityProperty, animation);
    }

    public void Update(MeteringSnapshot snapshot)
    {
        var tariff = snapshot.Tariff;

        UpdateGauge(snapshot);
        UpdateBreakdown(snapshot.Power);
        UpdateTotals(snapshot, tariff);
        UpdateWarning(snapshot, tariff);
        UpdateQualityBadge(snapshot.Power);
    }

    private void UpdateGauge(MeteringSnapshot snapshot)
    {
        Gauge.AnimateTo(snapshot.GaugeFraction);

        if (snapshot.GaugeMode == GaugeMode.Instant || snapshot.Tariff.MonthlyBudget <= 0)
        {
            PrimaryValue.Text = CurrencyFormatter.FormatWatts(snapshot.Power.WallWatts);
            PrimaryUnit.Text = "W";

            GaugeCaption.Text = snapshot.Tariff.MonthlyBudget <= 0 && snapshot.GaugeMode == GaugeMode.Budget
                ? Loc.T("popup.setBudgetHint")
                : Loc.T("popup.maxWatts", CurrencyFormatter.FormatWatts(_metering.Profile.ObservedPeakWatts));
        }
        else
        {
            PrimaryValue.Text = CurrencyFormatter.FormatWatts(snapshot.Power.WallWatts);
            PrimaryUnit.Text = "W";

            double percent = snapshot.GaugeFraction * 100;
            string budget = CurrencyFormatter.Format(
                snapshot.Tariff.MonthlyBudget, snapshot.Tariff.Currency, snapshot.Tariff.Symbol);

            GaugeCaption.Text = Loc.T("popup.budgetUsed", percent.ToString("N0"), budget);
        }
    }

    /// <summary>
    /// 총합만으로는 지금 무엇이 전기를 먹는지 알 수 없다. CPU·GPU·나머지를 한 줄로 갈라 준다.
    ///
    /// "나머지"는 잴 수 없어서 모델로 메우는 몫이다 — 메인보드·RAM·SSD·팬과 파워서플라이 손실.
    /// 값이 0 인 항목은 빼서 노트북처럼 외장 GPU 가 없는 기기에서 줄이 지저분해지지 않게 한다.
    /// </summary>
    private void UpdateBreakdown(PowerReading power)
    {
        var parts = new List<string>(3);

        if (power.CpuWatts >= 0.5)
            parts.Add(Loc.T("popup.part.cpu", CurrencyFormatter.FormatWatts(power.CpuWatts)));

        if (power.GpuWatts >= 0.5)
            parts.Add(Loc.T("popup.part.gpu", CurrencyFormatter.FormatWatts(power.GpuWatts)));

        if (power.OtherWatts >= 0.5)
            parts.Add(Loc.T("popup.part.other", CurrencyFormatter.FormatWatts(power.OtherWatts)));

        BreakdownText.Text = string.Join("   ·   ", parts);
    }

    private void UpdateTotals(MeteringSnapshot snapshot, Core.Tariff.TariffConfig tariff)
    {
        TodayKwh.Text = CurrencyFormatter.FormatKwh(snapshot.TodayKwh);
        TodayCost.Text = CurrencyFormatter.Format(snapshot.TodayCost, tariff.Currency, tariff.Symbol);

        CycleKwh.Text = CurrencyFormatter.FormatKwh(snapshot.CycleKwh);

        ProjectedCost.Text = CurrencyFormatter.Format(
            snapshot.ProjectedCycleCost, tariff.Currency, tariff.Symbol);

        AnchorTickingCost(snapshot);

        var history = _metering.RecentHistory(60);
        var watts = history.Select(s => s.AverageWatts).ToList();
        Trend.Values = watts;

        PeakLabel.Text = watts.Count > 0
            ? Loc.T("popup.peak", CurrencyFormatter.FormatWatts(watts.Max()))
            : string.Empty;
    }

    // ─────────────── 흘러가는 요금 ───────────────
    //
    // 계측은 1초에 한 번이지만, 그 값을 그대로 찍으면 숫자가 멈춰 있는 것처럼 보인다.
    // 원화로 342W 를 쓸 때 1초에 오르는 금액이 0.024원밖에 안 되기 때문이다.
    // 그래서 마지막 표본을 기준점으로 삼고, 시간당 요금으로 그 사이를 채워 그린다.

    private double _costAnchor;
    private double _costPerSecond;
    private long _costAnchorTimestamp;
    private bool _costConfigured;

    private void AnchorTickingCost(MeteringSnapshot snapshot)
    {
        _costAnchor = snapshot.CycleCost.Total;
        _costPerSecond = snapshot.CostPerHour / 3600.0;
        _costAnchorTimestamp = Stopwatch.GetTimestamp();
        _costConfigured = snapshot.IsTariffConfigured;

        CostRate.Text = _costConfigured
            ? Loc.T("popup.ratePerHour",
                CurrencyFormatter.Format(snapshot.CostPerHour, snapshot.Tariff.Currency, snapshot.Tariff.Symbol))
            : Loc.T("popup.noRateYet");

        RateButton.Content = Loc.T(_costConfigured ? "popup.rateButton" : "popup.rateButtonSetup");

        RenderTickingCost();
    }

    private void RenderTickingCost()
    {
        var tariff = _metering.Profile.Tariff;

        if (!_costConfigured)
        {
            CostPrefix.Text = string.Empty;
            CostMain.Text = "—";
            CostFraction.Text = string.Empty;
            CostSuffix.Text = string.Empty;
            return;
        }

        double elapsed = Stopwatch.GetElapsedTime(_costAnchorTimestamp).TotalSeconds;
        double amount = _costAnchor + (_costPerSecond * elapsed);

        var (prefix, main, fraction, suffix) =
            CurrencyFormatter.SplitTicking(amount, tariff.Currency, tariff.Symbol);

        CostPrefix.Text = prefix;
        CostMain.Text = main;
        CostFraction.Text = fraction;
        CostSuffix.Text = suffix;
    }

    private void UpdateWarning(MeteringSnapshot snapshot, Core.Tariff.TariffConfig tariff)
    {
        var warning = snapshot.TierWarning;

        // 다음 구간이 멀면 굳이 겁줄 필요가 없다. 20kWh 안쪽일 때만 띄운다.
        if (warning is null || warning.KwhUntilNextTier > 20)
        {
            WarningCard.Visibility = Visibility.Collapsed;
            return;
        }

        string current = CurrencyFormatter.Format(
            warning.CurrentPricePerKwh, tariff.Currency, tariff.Symbol);
        string next = CurrencyFormatter.Format(
            warning.NextPricePerKwh, tariff.Currency, tariff.Symbol);

        // 금액 뒤에 조사를 붙이면 통화 기호에 따라 "215원이" / "$0.42가" 처럼
        // 받침 여부가 달라져 틀린 문장이 된다. 조사가 필요 없는 형태로 쓴다.
        WarningText.Text = Loc.T(
            "popup.tierWarning", warning.KwhUntilNextTier.ToString("N1"), current, next);

        WarningCard.Visibility = Visibility.Visible;
    }

    private void UpdateQualityBadge(PowerReading power)
    {
        var (text, color) = power.Quality switch
        {
            PowerQuality.Measured => (Loc.T("popup.quality.measured"), Color.FromRgb(0x2E, 0xD5, 0x73)),
            PowerQuality.SensorCalibrated => (Loc.T("popup.quality.calibrated"), Color.FromRgb(0x2E, 0xD5, 0x73)),
            PowerQuality.SensorUncalibrated => (Loc.T("popup.quality.uncalibrated"), Color.FromRgb(0xF5, 0xC2, 0x2C)),
            _ => (Loc.T("popup.quality.estimated", EstimationReason()), Color.FromRgb(0xF5, 0x8A, 0x2C)),
        };

        QualityText.Text = power.OnBattery ? Loc.T("popup.quality.onBattery", text) : text;
        QualityDot.Fill = new SolidColorBrush(color);
    }

    /// <summary>
    /// CPU 센서를 왜 못 읽는지. 드라이버가 없는 것과 권한이 없는 것은 조치가 다르므로
    /// 뭉뚱그리면 사용자가 이미 설치한 드라이버를 다시 설치하러 간다.
    /// </summary>
    private static string EstimationReason()
    {
        if (!PawnIoDetector.IsInstalled()) return Loc.T("popup.reason.noPawnIo");

        return Loc.T(Elevation.IsElevated
            ? "popup.reason.noResponse"
            : "popup.reason.noElevation");
    }

    /// <summary>지금 무엇을 기준으로 그리고 있는지 보여 준다. 누르면 반대쪽으로 바뀐다.</summary>
    private void UpdateModeButton()
        => ModeButton.Content = Loc.T(_metering.Profile.GaugeMode == GaugeMode.Budget
            ? "popup.mode.budget"
            : "popup.mode.instant");

    private void OnToggleMode(object sender, RoutedEventArgs e)
    {
        var next = _metering.Profile.GaugeMode == GaugeMode.Budget
            ? GaugeMode.Instant
            : GaugeMode.Budget;

        _metering.UpdateProfile(_metering.Profile with { GaugeMode = next });
        UpdateModeButton();

        if (_metering.Latest is { } snapshot) Update(snapshot);
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        Hide();
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenRateWizard(object sender, RoutedEventArgs e)
    {
        Hide();
        RateWizardRequested?.Invoke(this, EventArgs.Empty);
    }

    // ─────────────── 업데이트 ───────────────

    private ReleaseInfo? _pendingUpdate;

    /// <summary>새 버전을 찾았을 때 TrayApp 이 알려 준다.</summary>
    public void ShowUpdate(ReleaseInfo release)
    {
        _pendingUpdate = release;

        UpdateTitle.Text = Loc.T("popup.update.title", release.Tag);
        UpdateDetail.Text = Loc.T("popup.update.detail", UpdateService.CurrentVersion);
        UpdateButton.IsEnabled = true;
        UpdateButton.Content = Loc.T("popup.update.button");

        UpdateCard.Visibility = Visibility.Visible;
    }

    private async void OnApplyUpdate(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is not ReleaseInfo release) return;

        UpdateButton.IsEnabled = false;

        var progress = new Progress<double>(fraction =>
            UpdateButton.Content = $"{fraction:P0}");

        try
        {
            string downloaded = await new UpdateService().DownloadAsync(release, progress);

            // 여기서부터는 실행 파일이 바뀐다. 새 프로세스가 뜨면 이쪽은 물러난다.
            UpdateService.ApplyAndRestart(downloaded);
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            UpdateTitle.Text = Loc.T("popup.update.failed");
            UpdateDetail.Text = Loc.T("popup.update.failedDetail", ex.Message);

            UpdateButton.Content = Loc.T("popup.update.openRelease");
            UpdateButton.IsEnabled = true;

            // 다음 클릭은 내려받기를 다시 시도하는 대신 브라우저를 연다.
            _pendingUpdate = null;
            UpdateButton.Click -= OnApplyUpdate;
            UpdateButton.Click += (_, _) => OpenInBrowser(release.PageUrl);
        }
    }

    private static void OpenInBrowser(string url)
        => Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
}
