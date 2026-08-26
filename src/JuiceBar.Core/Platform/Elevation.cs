using System.Diagnostics;
using System.Security.Principal;
using JuiceBar.Core.Update;

namespace JuiceBar.Core.Platform;

/// <summary>
/// 현재 프로세스가 승격되어 있는지.
///
/// PawnIO 가 설치되어 있어도 승격 없이는 드라이버와 통신할 수 없어 CPU 센서가 0으로 온다.
/// 두 상황을 구분해서 알려 줘야 사용자가 엉뚱한 조치를 하지 않는다 —
/// 이미 설치한 드라이버를 다시 설치하러 가는 일 같은 것.
/// </summary>
public static class Elevation
{
    private static readonly Lazy<bool> _isElevated = new(Check);

    public static bool IsElevated => _isElevated.Value;

    private static bool Check()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 승격된 채로 스스로를 다시 띄운다. 성공하면 부른 쪽이 곧바로 종료해야 한다.
    ///
    /// 프로세스 안에서는 권한을 올릴 수 없다. 새로 띄우는 수밖에 없고, 그러면 UAC 창이 뜬다.
    /// 사용자가 거기서 취소하면 예외가 나므로 조용히 false 를 돌려준다 —
    /// 거절은 잘못이 아니라 선택이고, 앱은 그대로 계속 돌아야 한다.
    ///
    /// 새 프로세스에 우리 번호를 넘겨서, 우리가 물러날 때까지 기다리게 한다.
    /// 그러지 않으면 단일 인스턴스 검사에 걸려 새 쪽이 그대로 죽는다.
    /// </summary>
    public static bool TryRelaunchElevated()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = $"{UpdateService.RestartArgument} {Environment.ProcessId}",
                UseShellExecute = true,
                Verb = "runas",
            });

            return true;
        }
        catch (Exception)
        {
            // UAC 를 거절했거나 정책이 막았다. 어느 쪽이든 하던 대로 계속한다.
            return false;
        }
    }
}
