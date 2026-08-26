using Microsoft.Win32;

namespace JuiceBar.Core.Platform;

/// <summary>
/// 지금 돌고 있는 Windows 를 한 줄로 서술한다.
///
/// 전력 측정이 되는지 안 되는지가 여기서 갈리기 때문에 진단에 반드시 필요하다 —
/// Windows 11 은 CPU 의 RAPL 값을 에너지 미터로 내보내지만 Windows 10 은
/// 실제 계측 하드웨어가 달린 기기에만 있다.
/// </summary>
public static class WindowsRelease
{
    /// <summary>Windows 11 이 시작되는 빌드 번호. 그 아래는 전부 10 계열이다.</summary>
    private const int FirstWindows11Build = 22000;

    /// <summary>"Windows 11 Pro 24H2 (build 26200)" 같은 한 줄.</summary>
    public static string Describe() => Describe(Environment.OSVersion.Version.Build, ReadRegistry);

    /// <summary>
    /// 레지스트리를 직접 읽지 않는 형태. 값이 없거나 못 읽는 경우를 확인할 수 있게 열어 둔다.
    /// </summary>
    internal static string Describe(int build, Func<string, string?> readValue)
    {
        // ProductName 은 Windows 11 에서도 "Windows 10 ..." 이라고 답한다. 유명한 함정이라
        // 이름은 빌드 번호로 정하고, 레지스트리에서는 버전 표기만 빌려 온다.
        string family = build >= FirstWindows11Build ? "Windows 11" : "Windows 10";

        string? edition = Friendly(readValue("EditionID"));
        string? display = Trimmed(readValue("DisplayVersion"));

        var parts = new List<string>(4) { family };

        if (edition is not null) parts.Add(edition);
        if (display is not null) parts.Add(display);

        parts.Add($"(build {build})");

        return string.Join(' ', parts);
    }

    /// <summary>EditionID 는 "Professional", "Core" 처럼 온다. 사람이 부르는 이름으로 바꾼다.</summary>
    private static string? Friendly(string? editionId)
    {
        string? id = Trimmed(editionId);
        if (id is null) return null;

        return id switch
        {
            "Core" => "Home",
            "CoreSingleLanguage" => "Home Single Language",
            "Professional" => "Pro",
            "ProfessionalWorkstation" => "Pro for Workstations",
            "Enterprise" => "Enterprise",
            "Education" => "Education",
            _ => id,
        };
    }

    private static string? Trimmed(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? ReadRegistry(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);

            return key?.GetValue(name) as string;
        }
        catch (Exception)
        {
            // 정책으로 막혀 있을 수 있다. 빌드 번호만으로도 쓸 만한 서술이 나온다.
            return null;
        }
    }
}
