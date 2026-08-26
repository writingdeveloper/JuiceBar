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

    // ── 업데이트 후 재시작 ────────────────────────────────────
    //
    // 1.0.0 에서 실제로 겪은 문제: 새 프로세스가 예전 프로세스보다 먼저 떠서
    // 단일 인스턴스 검사에 걸려 스스로 종료했고, 곧이어 예전 쪽도 끝나
    // 업데이트 후 아무것도 남지 않았다. 그래서 넘겨받은 번호를 반드시 읽어야 한다.

    [Fact]
    public void The_previous_process_id_is_read_from_the_command_line()
    {
        var args = new[] { UpdateService.WaitForArgument, "4321" };

        Assert.Equal(4321, UpdateService.ParseReplacedProcessId(args));
    }

    [Fact]
    public void Other_arguments_around_it_do_not_matter()
    {
        var args = new[] { "--something", UpdateService.WaitForArgument, "77", "--else" };

        Assert.Equal(77, UpdateService.ParseReplacedProcessId(args));
    }

    public static TheoryData<string[]> ArgumentsWithoutAProcessId() =>
    [
        // 평소 실행에는 이 인자가 없다.
        [],
        ["--other"],

        // 번호가 빠졌거나 숫자가 아니면 기다릴 대상이 없다.
        [UpdateService.WaitForArgument],
        [UpdateService.WaitForArgument, "not-a-number"],
        [UpdateService.WaitForArgument, "0"],
        [UpdateService.WaitForArgument, "-5"],
    ];

    [Theory]
    [MemberData(nameof(ArgumentsWithoutAProcessId))]
    public void Without_a_usable_process_id_nothing_is_waited_for(string[] args)
    {
        Assert.Null(UpdateService.ParseReplacedProcessId(args));
    }

    [Fact]
    public void Waiting_actually_blocks_until_the_named_process_exits()
    {
        // 진짜 프로세스로 확인한다. 이 대기가 동작하지 않으면
        // 업데이트한 새 버전이 예전 인스턴스에 밀려 그대로 죽는다.
        // timeout 은 콘솔이 없으면 곧바로 실패한다. ping 은 창 없이도 제 시간을 지킨다.
        using var previous = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 3 127.0.0.1",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        UpdateService.WaitForReplacedProcess([UpdateService.WaitForArgument, previous.Id.ToString()]);
        watch.Stop();

        Assert.True(previous.HasExited, "기다렸는데도 예전 프로세스가 아직 살아 있습니다.");
        Assert.True(watch.Elapsed > TimeSpan.FromSeconds(1),
            $"기다리지 않고 {watch.Elapsed} 만에 돌아왔습니다.");
    }

    [Fact]
    public void Waiting_returns_immediately_when_the_process_is_already_gone()
    {
        // 이미 사라진 번호를 줘도 예외 없이 곧바로 돌아와야 한다.
        var args = new[] { UpdateService.WaitForArgument, "999999" };

        var watch = System.Diagnostics.Stopwatch.StartNew();
        UpdateService.WaitForReplacedProcess(args);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"{watch.Elapsed} 만큼 기다렸습니다.");
    }

    // ── 실행 파일 교체 ────────────────────────────────────────
    //
    // 여기가 잘못되면 사용자의 설치본이 사라진다. 실제 파일로 확인한다.

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("juicebar-test").FullName;

        public string File(string name, string content)
        {
            string full = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllText(full, content);
            return full;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Swapping_puts_the_new_file_in_place_and_keeps_the_old_one_aside()
    {
        using var temp = new TempDirectory();

        string target = temp.File("JuiceBar.exe", "old version");
        string replacement = temp.File("downloaded.exe", "new version");

        string backup = UpdateService.SwapExecutable(target, replacement);

        Assert.Equal("new version", File.ReadAllText(target));
        Assert.Equal("old version", File.ReadAllText(backup));
        Assert.False(File.Exists(replacement));
    }

    [Fact]
    public void Swapping_overwrites_a_backup_left_by_an_earlier_update()
    {
        using var temp = new TempDirectory();

        string target = temp.File("JuiceBar.exe", "version 2");
        temp.File("JuiceBar.exe.old", "version 1");
        string replacement = temp.File("downloaded.exe", "version 3");

        string backup = UpdateService.SwapExecutable(target, replacement);

        Assert.Equal("version 3", File.ReadAllText(target));
        Assert.Equal("version 2", File.ReadAllText(backup));
    }

    [Fact]
    public void A_failed_swap_leaves_the_original_executable_in_place()
    {
        using var temp = new TempDirectory();

        string target = temp.File("JuiceBar.exe", "old version");
        string missing = Path.Combine(temp.Path, "never-downloaded.exe");

        Assert.ThrowsAny<IOException>(() => UpdateService.SwapExecutable(target, missing));

        // 이것이 핵심이다 — 업데이트가 실패해도 쓰던 버전은 그대로 남아야 한다.
        Assert.True(File.Exists(target));
        Assert.Equal("old version", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".old"));
    }
}
