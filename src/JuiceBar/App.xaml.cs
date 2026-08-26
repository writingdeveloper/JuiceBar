using System.Diagnostics;
using System.Windows;
using JuiceBar.Core.Localization;
using JuiceBar.Core.Storage;
using JuiceBar.Core.Update;
using JuiceBar.Ui;

namespace JuiceBar;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Global\\JuiceBar.SingleInstance";

    private Mutex? _instanceMutex;
    private TrayApp? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 언어를 가장 먼저 정한다. 아래 "이미 실행 중" 안내도 사용자의 언어로 나와야 한다.
        ApplySavedLanguage();

        // 업데이트 직후라면 방금 우리를 띄운 예전 실행 파일이 아직 살아 있다.
        // 그쪽이 물러날 때까지 기다리지 않으면 단일 인스턴스 검사에 걸려 새 버전이 그대로 죽는다.
        UpdateService.WaitForReplacedProcess(e.Args);

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

    /// <summary>
    /// 저장된 언어를 읽어 적용한다.
    ///
    /// TrayApp 도 같은 일을 하지만 그쪽은 창을 만들기 직전이라 늦다.
    /// 프로필을 두 번 읽는 비용은 파일 하나를 읽는 정도라 문제되지 않는다.
    /// </summary>
    private static void ApplySavedLanguage()
    {
        try
        {
            Loc.Use(new ProfileStore().Load().Language);
        }
        catch (Exception)
        {
            // 프로필을 못 읽어도 앱은 떠야 한다. 그때는 시스템 언어를 따른다.
            Loc.Use(Loc.AutoCode);
        }
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
