using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using JuiceBar.Core.Localization;

namespace JuiceBar.Core.Update;

/// <summary>찾아낸 새 버전.</summary>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string DownloadUrl,
    long SizeBytes,
    string PageUrl,
    string Notes);

/// <summary>
/// GitHub 릴리스를 확인하고 실행 파일을 스스로 갈아 끼운다.
///
/// 설치 관리자 없이 exe 하나로 배포하므로, 업데이트도 그 파일을 바꾸는 일이 전부다.
/// Windows 는 실행 중인 exe 를 덮어쓸 수는 없지만 이름을 바꾸는 것은 허용한다.
/// 그 성질을 이용해 현재 파일을 옆으로 밀어 두고 새 파일을 그 자리에 놓는다.
/// </summary>
public sealed class UpdateService
{
    private const string Owner = "writingdeveloper";
    private const string Repository = "JuiceBar";

    private const string LatestReleaseUrl =
        $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest";

    /// <summary>밀어 둔 예전 실행 파일에 붙는 꼬리표.</summary>
    private const string BackupSuffix = ".old";

    // 정적 초기화는 선언 순서대로 일어난다.
    // CreateClient 가 CurrentVersion 을 쓰므로 반드시 이쪽이 먼저 와야 한다.
    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // GitHub API 는 User-Agent 가 없으면 403 을 돌려준다.
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(Repository, CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        return client;
    }

    private static Version ReadCurrentVersion()
    {
        // 진입 어셈블리가 아니라 이 코드가 들어 있는 어셈블리를 본다.
        // 테스트에서는 진입점이 테스트 호스트라 엉뚱한 버전을 읽게 되고,
        // 어차피 릴리스 태그와 비교할 대상은 JuiceBar 자신의 버전이다.
        var assembly = typeof(UpdateService).Assembly;

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return ParseVersion(informational) ?? assembly.GetName().Version ?? new Version(0, 0, 0);
    }

    /// <summary>
    /// "v1.2.3", "1.2.3+abc123" 같은 표기에서 숫자 부분만 뽑는다.
    /// 빌드 메타데이터(+)와 프리릴리스(-)는 비교에 쓰지 않는다.
    /// </summary>
    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        text = text.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        int cut = text.IndexOfAny(['+', '-']);
        if (cut >= 0) text = text[..cut];

        return Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>
    /// 실행 파일을 직접 교체할 수 있는 상태인지.
    ///
    /// 개발 중에는 dotnet 호스트가 dll 을 실행하므로 바꿔치기할 exe 가 없다.
    /// 그럴 때는 확인만 하고 내려받기는 하지 않는다.
    /// </summary>
    public static bool CanReplaceItself
    {
        get
        {
            string? path = Environment.ProcessPath;

            return path is not null
                && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>새 버전이 있으면 돌려주고, 없거나 확인에 실패하면 null.</summary>
    public async Task<ReleaseInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await _http.GetFromJsonAsync<GitHubRelease>(
                LatestReleaseUrl, cancellationToken).ConfigureAwait(false);

            if (release is null || release.Draft || release.Prerelease) return null;
            if (ParseVersion(release.TagName) is not Version latest) return null;
            if (latest <= CurrentVersion) return null;

            var asset = release.Assets?.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            if (asset is null || !IsTrustedDownload(asset.DownloadUrl)) return null;

            return new ReleaseInfo(
                latest,
                release.TagName ?? latest.ToString(),
                asset.DownloadUrl,
                asset.Size,
                release.HtmlUrl ?? $"https://github.com/{Owner}/{Repository}/releases",
                release.Body ?? string.Empty);
        }
        catch (Exception)
        {
            // 네트워크가 없거나 GitHub 이 잠깐 응답하지 않는 것은 흔한 일이다.
            // 업데이트 확인 실패로 앱이 시끄러워질 이유는 없다.
            return null;
        }
    }

    /// <summary>
    /// 내려받을 주소가 GitHub 것인지 확인한다.
    /// 응답이 조작되더라도 엉뚱한 서버에서 실행 파일을 받아오지 않도록 하는 최소한의 방어다.
    /// </summary>
    internal static bool IsTrustedDownload(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        string host = uri.Host;

        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>새 실행 파일을 임시 폴더에 내려받고 그 경로를 돌려준다.</summary>
    public async Task<string> DownloadAsync(
        ReleaseInfo release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsTrustedDownload(release.DownloadUrl))
            throw new InvalidOperationException(Loc.T("update.error.untrustedUrl"));

        string target = Path.Combine(
            Path.GetTempPath(), $"JuiceBar-{release.Tag}-{Guid.NewGuid():N}.exe");

        using (var response = await _http.GetAsync(
            release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? release.SizeBytes;

            await using var source = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = File.Create(target);

            var buffer = new byte[81920];
            long copied = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                copied += read;
                if (total > 0) progress?.Report((double)copied / total);
            }
        }

        await VerifyAsync(target, release, cancellationToken).ConfigureAwait(false);
        return target;
    }

    /// <summary>
    /// 받은 파일이 실제로 Windows 실행 파일인지, 크기가 맞는지 본다.
    /// 여기서 걸러내지 못하면 멀쩡한 설치본을 깨진 파일로 덮어쓰게 된다.
    /// </summary>
    private static async Task VerifyAsync(string path, ReleaseInfo release, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);

        if (release.SizeBytes > 0 && file.Length != release.SizeBytes)
        {
            File.Delete(path);
            throw new InvalidOperationException(
                $"내려받은 파일 크기가 다릅니다 ({file.Length:N0} / {release.SizeBytes:N0} 바이트).");
        }

        var header = new byte[2];

        await using (var stream = File.OpenRead(path))
        {
            if (await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false) != 2
                || header[0] != (byte)'M' || header[1] != (byte)'Z')
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                File.Delete(path);
                throw new InvalidOperationException("내려받은 파일이 실행 파일이 아닙니다.");
            }
        }
    }

    /// <summary>
    /// 새 파일을 제자리에 놓고 다시 실행한다. 성공하면 이 프로세스는 곧 종료되어야 한다.
    ///
    /// 실행 중인 파일은 덮어쓸 수 없지만 이름은 바꿀 수 있다. 그래서
    /// 지금 파일을 .old 로 밀어 두고 새 파일을 그 자리에 옮긴다.
    /// 도중에 실패하면 밀어 둔 파일을 되돌려 원래 상태로 남긴다.
    /// </summary>
    public static void ApplyAndRestart(string downloadedPath)
    {
        string current = Environment.ProcessPath
            ?? throw new InvalidOperationException("현재 실행 파일 경로를 알 수 없습니다.");

        SwapExecutable(current, downloadedPath);

        // 새 프로세스에게 우리 번호를 넘겨 준다.
        // 우리가 아직 단일 인스턴스 뮤텍스를 쥐고 있어서, 그쪽이 곧바로 뜨면
        // "이미 실행 중" 이라며 스스로 종료해 버린다. 그러면 업데이트 후 아무것도 남지 않는다.
        Process.Start(new ProcessStartInfo
        {
            FileName = current,
            Arguments = $"{WaitForArgument} {Environment.ProcessId}",
            UseShellExecute = true,
        });
    }

    /// <summary>업데이트로 교체된 예전 프로세스를 기다리라는 표시.</summary>
    public const string WaitForArgument = "--updated-from";

    /// <summary>
    /// 승격을 위해 스스로를 다시 띄울 때 쓰는 표시.
    ///
    /// 기다려야 하는 이유는 업데이트와 똑같다 — 예전 프로세스가 단일 인스턴스 뮤텍스를
    /// 놓기 전에 새 프로세스가 뜨면 새 쪽이 "이미 실행 중"이라며 스스로 종료한다.
    /// 인자 이름을 나눠 둔 건 작업 관리자에서 왜 이 프로세스가 떴는지 보이게 하기 위해서다.
    /// </summary>
    public const string RestartArgument = "--restarted-from";

    /// <summary>예전 프로세스가 물러나기를 이만큼 기다린다. 그 뒤에는 그냥 진행한다.</summary>
    private static readonly TimeSpan ReplacedProcessTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 명령줄에 <see cref="WaitForArgument"/> 나 <see cref="RestartArgument"/> 가 있으면
    /// 그 번호의 프로세스가 끝날 때까지 기다린다. 업데이트 직후나 승격 재시작에서만 쓰인다.
    /// </summary>
    public static void WaitForReplacedProcess(IReadOnlyList<string>? args)
    {
        if (ParseReplacedProcessId(args) is not int processId) return;

        try
        {
            using var previous = Process.GetProcessById(processId);
            previous.WaitForExit((int)ReplacedProcessTimeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            // 이미 끝났다. 기다릴 것이 없다.
        }
        catch (Exception)
        {
            // 접근이 막혀도 계속 진행한다. 최악의 경우 단일 인스턴스 안내가 뜰 뿐이다.
        }
    }

    internal static int? ParseReplacedProcessId(IReadOnlyList<string>? args)
    {
        if (args is null) return null;

        for (int i = 0; i < args.Count - 1; i++)
        {
            bool handoff = string.Equals(args[i], WaitForArgument, StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[i], RestartArgument, StringComparison.OrdinalIgnoreCase);

            if (!handoff) continue;

            return int.TryParse(args[i + 1], out int processId) && processId > 0
                ? processId
                : null;
        }

        return null;
    }

    /// <summary>
    /// <paramref name="target"/> 자리에 <paramref name="replacement"/> 를 놓는다.
    /// 밀어 둔 예전 파일의 경로를 돌려준다.
    ///
    /// 실패하면 원래 파일을 제자리에 되돌린다 — 업데이트가 깨지는 것보다
    /// 예전 버전이라도 돌아가는 편이 낫다.
    /// </summary>
    internal static string SwapExecutable(string target, string replacement)
    {
        string backup = target + BackupSuffix;

        if (File.Exists(backup)) TryDelete(backup);

        File.Move(target, backup);

        try
        {
            File.Move(replacement, target);
        }
        catch (Exception)
        {
            File.Move(backup, target);
            throw;
        }

        return backup;
    }

    /// <summary>
    /// 지난 업데이트가 남긴 예전 실행 파일을 지운다.
    /// 시작할 때 한 번 부르면 된다 — 그때는 이미 잠겨 있지 않다.
    /// </summary>
    public static void CleanupPreviousVersion()
    {
        if (Environment.ProcessPath is not string current) return;

        TryDelete(current + BackupSuffix);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 아직 잠겨 있으면 다음 실행 때 다시 시도한다. 지우지 못해도 동작에는 지장이 없다.
        }
    }

    // GitHub 릴리스 API 응답에서 필요한 것만 받는다.

    private sealed record GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; init; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
        [JsonPropertyName("body")] public string? Body { get; init; }
        [JsonPropertyName("draft")] public bool Draft { get; init; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; init; }
    }

    private sealed record GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; init; } = string.Empty;
        [JsonPropertyName("size")] public long Size { get; init; }
    }
}
