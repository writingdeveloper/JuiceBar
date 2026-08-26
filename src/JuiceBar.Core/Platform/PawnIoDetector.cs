using Microsoft.Win32;

namespace JuiceBar.Core.Platform;

/// <summary>
/// PawnIO 드라이버 설치 여부를 확인한다.
///
/// LibreHardwareMonitor 0.9.5부터 CPU 전력·온도 같은 ring0 센서는 PawnIO를 통해 읽는다.
/// (그 전에 쓰던 WinRing0은 Microsoft 취약 드라이버 차단 목록에 올라 퇴출됐다.)
/// PawnIO가 없으면 CPU 센서가 0으로 나오므로, 첫 실행 때 이를 감지해 안내해야 한다.
/// </summary>
public static class PawnIoDetector
{
    public const string DownloadUrl = "https://pawnio.eu/";
    public const string WingetPackageId = "namazso.PawnIO";

    public static bool IsInstalled()
    {
        // 서비스 등록 여부가 가장 확실하다. 드라이버가 설치되면 서비스 키가 생긴다.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\PawnIO", writable: false);

            if (key is not null) return true;
        }
        catch (Exception)
        {
            // 접근이 막히면 아래 경로 확인으로 넘어간다.
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Directory.Exists(Path.Combine(programFiles, "PawnIO"));
    }
}
