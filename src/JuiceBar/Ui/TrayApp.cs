using System.Windows;
using System.Windows.Forms;
using JuiceBar.Core;
using JuiceBar.Core.Platform;
using JuiceBar.Core.Storage;
using JuiceBar.Core.Update;

// 트레이 아이콘은 WinForms 의 NotifyIcon 을 쓴다 — WPF 에는 대응물이 없다.
// 그래서 이 파일에서만 두 세계의 이름이 겹친다.
using Application = System.Windows.Application;
using MouseEventArgs = System.Windows.Forms.MouseEventArgs;
using MessageBox = System.Windows.MessageBox;
using JuiceBar.Core.Localization;

namespace JuiceBar.Ui;

/// <summary>
/// 트레이 아이콘과 창들을 붙들고 있는 조정자.
///
/// 계측은 백그라운드 스레드에서 돌고 UI는 디스패처 스레드에서만 만질 수 있으므로,
/// 스냅샷이 올 때마다 여기서 마셜링한다.
/// </summary>
public sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly TrayIconRenderer _iconRenderer = new();
    private readonly ProfileStore _profileStore = new();
    private readonly MeteringService _metering;

    private PopupWindow? _popup;
    private SettingsWindow? _settings;
    private RateWizardWindow? _rateWizard;

    private double _lastDrawnFraction = -1;
    private ThemeVariant _lastTaskbarTheme;
    private bool _disposed;

    public TrayApp()
    {
        _metering = new MeteringService(_profileStore);

        // 트레이 메뉴를 만들기 전에 언어를 정해야 메뉴 글자가 그 언어로 나온다.
        Loc.Use(_metering.Profile.Language);

        _lastTaskbarTheme = SystemTheme.Taskbar;

        _notifyIcon = new NotifyIcon
        {
            Text = "JuiceBar",
            Visible = true,
            ContextMenuStrip = BuildContextMenu(),
        };

        _notifyIcon.MouseClick += OnTrayClick;
        _metering.Updated += OnMeteringUpdated;
        SystemTheme.Changed += OnSystemThemeChanged;
    }

    public void Start()
    {
        RedrawIcon(0);
        _metering.Start();

        _ = WatchForUpdatesAsync();

        if (PopupWindow.PinnedForDevelopment)
        {
            Application.Current.Dispatcher.BeginInvoke(ShowPopup);
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(RunFirstStart);
    }

    /// <summary>
    /// 첫 실행에서 처리할 것들.
    ///
    /// 요금 설정이 먼저다 — 그게 없으면 이 앱이 보여 줄 것이 절반뿐이다.
    /// PawnIO 안내는 정확도를 높이는 선택 사항이라 그 뒤에 붙인다.
    /// 두 개를 한꺼번에 띄우면 실행하자마자 창 두 개가 겹쳐서 성가시다.
    /// </summary>
    private void RunFirstStart()
    {
        if (!_metering.Profile.IsTariffConfigured)
        {
            ShowRateWizard(onClosed: PromptForPawnIoIfMissing);
            return;
        }

        PromptForPawnIoIfMissing();
    }

    private void PromptForPawnIoIfMissing()
    {
        if (PawnIoDetector.IsInstalled()) return;

        PromptForPawnIo();
    }

    // ─────────────── 업데이트 확인 ───────────────

    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    /// <summary>실행하자마자 네트워크를 두드리면 시작이 느려 보인다. 조금 뒤로 미룬다.</summary>
    private static readonly TimeSpan FirstUpdateCheckDelay = TimeSpan.FromSeconds(20);

    private readonly UpdateService _updates = new();
    private readonly CancellationTokenSource _shutdown = new();

    private ReleaseInfo? _availableUpdate;

    private async Task WatchForUpdatesAsync()
    {
        // 지난 업데이트가 남긴 예전 실행 파일을 치운다.
        UpdateService.CleanupPreviousVersion();

        try
        {
            await Task.Delay(FirstUpdateCheckDelay, _shutdown.Token);

            while (!_shutdown.IsCancellationRequested)
            {
                if (_metering.Profile.CheckForUpdates && UpdateService.CanReplaceItself)
                    await CheckOnceAsync();

                await Task.Delay(UpdateCheckInterval, _shutdown.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 종료 중이다. 조용히 빠져나간다.
        }
    }

    private async Task CheckOnceAsync()
    {
        var release = await _updates.CheckAsync(_shutdown.Token);
        if (release is null) return;

        // 같은 버전을 두 번 알리지 않는다.
        if (_availableUpdate?.Version == release.Version) return;
        _availableUpdate = release;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;

        await dispatcher.InvokeAsync(() =>
        {
            _popup?.ShowUpdate(release);

            _notifyIcon.BalloonTipTitle = Loc.T("tray.update.title", release.Tag);
            _notifyIcon.BalloonTipText = Loc.T("tray.update.text");
            _notifyIcon.ShowBalloonTip(8000);
        });
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(Loc.T("tray.open"), null, (_, _) => ShowPopup());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.T("tray.rate"), null, (_, _) => ShowRateWizard());
        menu.Items.Add(Loc.T("tray.calibrate"), null, (_, _) => ShowCalibration());
        menu.Items.Add(Loc.T("tray.settings"), null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.T("tray.exit"), null, (_, _) => Shutdown());

        return menu;
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (_popup is { IsVisible: true })
            _popup.Hide();
        else
            ShowPopup();
    }

    private void OnMeteringUpdated(object? sender, MeteringSnapshot snapshot)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;

        dispatcher.BeginInvoke(() =>
        {
            UpdateTooltip(snapshot);
            RedrawIcon(snapshot.GaugeFraction);

            if (_popup is { IsVisible: true })
                _popup.Update(snapshot);
        });
    }

    /// <summary>
    /// 눈에 띄게 달라졌을 때만 다시 그린다.
    /// 매초 아이콘을 새로 만들면 GDI 부담이 크고, 1% 미만의 변화는 어차피 보이지 않는다.
    /// </summary>
    private void RedrawIcon(double fraction)
    {
        var taskbarTheme = SystemTheme.Taskbar;

        bool changed = Math.Abs(fraction - _lastDrawnFraction) >= 0.01
            || taskbarTheme != _lastTaskbarTheme;

        if (!changed) return;

        _lastDrawnFraction = fraction;
        _lastTaskbarTheme = taskbarTheme;

        _notifyIcon.Icon = _iconRenderer.Render(
            fraction, TrayIconRenderer.RecommendedSize(), taskbarTheme);
    }

    private void UpdateTooltip(MeteringSnapshot snapshot)
    {
        string watts = $"{CurrencyFormatter.FormatWatts(snapshot.Power.WallWatts)} W";

        // 트레이 툴팁은 63자를 넘으면 잘린다. 짧게 유지한다.
        if (!snapshot.IsTariffConfigured)
        {
            _notifyIcon.Text = Loc.T("tray.tooltipNoRate", watts);
            return;
        }

        var tariff = snapshot.Tariff;
        string cost = CurrencyFormatter.Format(
            snapshot.CycleCost.Total, tariff.Currency, tariff.Symbol);

        _notifyIcon.Text = Loc.T("tray.tooltip", watts, cost);
    }

    private void OnSystemThemeChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            ThemeManager.FollowSystem();

            // 강제로 다시 그리게 해서 작업표시줄 테마 변화를 즉시 반영한다.
            _lastDrawnFraction = -1;
            RedrawIcon(_metering.Latest?.GaugeFraction ?? 0);

            if (_popup is not null) WindowEffects.Apply(_popup, ThemeManager.Current);
            if (_settings is not null) WindowEffects.Apply(_settings, ThemeManager.Current);
        });
    }

    private void ShowPopup()
    {
        if (_popup is null)
        {
            _popup = new PopupWindow(_metering);
            _popup.SettingsRequested += (_, _) => ShowSettings();
            _popup.CalibrationRequested += (_, _) => ShowCalibration();
            _popup.RateWizardRequested += (_, _) => ShowRateWizard();
            _popup.ExitRequested += (_, _) => Shutdown();
        }

        if (_metering.Latest is { } snapshot) _popup.Update(snapshot);
        if (_availableUpdate is { } release) _popup.ShowUpdate(release);

        _popup.ShowNearTray();
    }

    private void ShowSettings()
    {
        if (_settings is { IsVisible: true })
        {
            _settings.Activate();
            return;
        }

        _settings = new SettingsWindow(_metering);
        _settings.WizardRequested += (_, _) => ShowRateWizard();
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    private void ShowCalibration()
    {
        var window = new CalibrationWindow(_metering);
        window.Show();
        window.Activate();
    }

    private void ShowRateWizard(Action? onClosed = null)
    {
        if (_rateWizard is { IsVisible: true })
        {
            _rateWizard.Activate();
            return;
        }

        _rateWizard = new RateWizardWindow(_metering);
        _rateWizard.AdvancedRequested += (_, _) => ShowSettings();
        _rateWizard.Closed += (_, _) =>
        {
            _rateWizard = null;
            onClosed?.Invoke();
        };

        _rateWizard.Show();
        _rateWizard.Activate();
    }

    private void PromptForPawnIo()
    {
        var result = System.Windows.MessageBox.Show(
            Loc.T("tray.pawnio.body"),
            Loc.T("tray.pawnio.title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (result != MessageBoxResult.Yes) return;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = PawnIoDetector.DownloadUrl,
            UseShellExecute = true,
        });
    }

    private void Shutdown()
    {
        _notifyIcon.Visible = false;
        Application.Current?.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SystemTheme.Changed -= OnSystemThemeChanged;
        _metering.Updated -= OnMeteringUpdated;

        _shutdown.Cancel();
        _shutdown.Dispose();

        _metering.Dispose();

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _iconRenderer.Dispose();
    }
}
