using System.Windows;
using JuiceBar.Ui;
using JuiceBar.Core.Localization;

namespace JuiceBar;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Global\\JuiceBar.SingleInstance";

    private Mutex? _instanceMutex;
    private TrayApp? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 두 번 실행되면 트레이 아이콘이 둘 생기고 같은 DB에 동시에 쓴다.
        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirst);

        if (!isFirst)
        {
            MessageBox.Show(
                Loc.T("tray.alreadyRunning"),
                "JuiceBar",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown();
            return;
        }

        ThemeManager.FollowSystem();
        SystemTheme.StartWatching();

        _tray = new TrayApp();
        _tray.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemTheme.StopWatching();

        _tray?.Dispose();

        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }
}
