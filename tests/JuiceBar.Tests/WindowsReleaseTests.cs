using JuiceBar.Core.Platform;

namespace JuiceBar.Tests;

/// <summary>
/// Windows 를 한 줄로 서술하는 부분.
///
/// 진단에서 가장 먼저 보는 값이다 — 에너지 미터가 있는지 없는지가 여기서 갈리므로,
/// 10 과 11 을 잘못 적으면 읽는 사람이 처음부터 엉뚱한 길로 간다.
/// </summary>
public class WindowsReleaseTests
{
    private static Func<string, string?> Registry(string? edition = null, string? display = null)
        => name => name switch
        {
            "EditionID" => edition,
            "DisplayVersion" => display,
            _ => null,
        };

    [Fact]
    public void A_modern_build_is_windows_11()
    {
        string text = WindowsRelease.Describe(26200, Registry("Professional", "24H2"));

        Assert.Equal("Windows 11 Pro 24H2 (build 26200)", text);
    }

    [Fact]
    public void An_older_build_is_windows_10()
    {
        string text = WindowsRelease.Describe(19045, Registry("Core", "22H2"));

        Assert.Equal("Windows 10 Home 22H2 (build 19045)", text);
    }

    [Fact]
    public void The_boundary_build_counts_as_eleven()
    {
        // 22000 이 Windows 11 의 첫 빌드다. 여기서 한 칸 어긋나면 진단이 통째로 뒤집힌다.
        Assert.StartsWith("Windows 11", WindowsRelease.Describe(22000, Registry()));
        Assert.StartsWith("Windows 10", WindowsRelease.Describe(21999, Registry()));
    }

    [Fact]
    public void The_registry_product_name_is_deliberately_not_used()
    {
        // ProductName 은 Windows 11 에서도 "Windows 10 ..." 이라고 답한다.
        // 그 값을 넘겨도 서술이 흔들리지 않아야 한다.
        string text = WindowsRelease.Describe(26200, name =>
            name == "ProductName" ? "Windows 10 Pro" : null);

        Assert.StartsWith("Windows 11", text);
    }

    [Fact]
    public void A_locked_down_registry_still_yields_something_useful()
    {
        // 정책으로 읽기가 막힌 기기가 있다. 빌드 번호만으로도 10 인지 11 인지는 안다.
        string text = WindowsRelease.Describe(26200, _ => null);

        Assert.Equal("Windows 11 (build 26200)", text);
    }

    [Fact]
    public void Blank_registry_values_are_treated_as_absent()
    {
        // 빈 문자열이 그대로 끼면 "Windows 11  (build ...)" 처럼 공백이 벌어진다.
        string text = WindowsRelease.Describe(26200, Registry(edition: "  ", display: ""));

        Assert.Equal("Windows 11 (build 26200)", text);
    }

    [Fact]
    public void An_edition_we_do_not_know_is_passed_through_rather_than_dropped()
    {
        string text = WindowsRelease.Describe(26200, Registry(edition: "IoTEnterprise"));

        Assert.Equal("Windows 11 IoTEnterprise (build 26200)", text);
    }
}
