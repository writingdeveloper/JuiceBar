using JuiceBar.Core.Update;

namespace JuiceBar.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("V1.0.0", "1.0.0")]
    // 빌드 메타데이터와 프리릴리스 꼬리표는 버전 비교에 쓰지 않는다.
    [InlineData("v1.2.3+abc1234", "1.2.3")]
    [InlineData("v2.0.0-beta.1", "2.0.0")]
    [InlineData("v1.2.3.4", "1.2.3.4")]
    public void Release_tags_are_parsed_into_versions(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateService.ParseVersion(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("v")]
    public void Unparseable_tags_return_null(string? tag)
    {
        Assert.Null(UpdateService.ParseVersion(tag));
    }

    [Theory]
    [InlineData("https://github.com/owner/repo/releases/download/v1/JuiceBar.exe", true)]
    [InlineData("https://objects.githubusercontent.com/some/path/JuiceBar.exe", true)]
    [InlineData("https://api.github.com/repos/owner/repo/releases/assets/1", true)]
    public void Github_download_urls_are_trusted(string url, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsTrustedDownload(url));
    }

    [Theory]
    // 다른 호스트에서 실행 파일을 받아오면 안 된다.
    [InlineData("https://evil.example.com/JuiceBar.exe")]
    // github.com 을 접두사로만 흉내 낸 도메인도 막아야 한다.
    [InlineData("https://github.com.evil.example/JuiceBar.exe")]
    // 평문 HTTP 는 중간에서 바꿔치기할 수 있다.
    [InlineData("http://github.com/owner/repo/releases/download/v1/JuiceBar.exe")]
    // 로컬 파일 경로도 마찬가지다.
    [InlineData("file:///C:/temp/JuiceBar.exe")]
    [InlineData("")]
    [InlineData("not a url")]
    public void Non_github_download_urls_are_rejected(string url)
    {
        Assert.False(UpdateService.IsTrustedDownload(url));
    }

    [Fact]
    public void Current_version_is_read_from_the_assembly()
    {
        // Directory.Build.props 에서 버전을 지정하므로 0.0.0 이 나오면 설정이 빠진 것이다.
        Assert.True(UpdateService.CurrentVersion > new Version(0, 0, 0));
    }

    [Fact]
    public void A_newer_tag_compares_greater_than_the_current_version()
    {
        var current = UpdateService.CurrentVersion;
        var newer = new Version(current.Major + 1, 0, 0);

        Assert.True(newer > current);
    }
}
