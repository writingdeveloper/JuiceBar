using System.Security.Principal;

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
}
