using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace JuiceBar.Core.Localization;

/// <summary>고를 수 있는 언어 하나.</summary>
public sealed record Language(string Code, string NativeName);

/// <summary>
/// 화면에 보이는 모든 글월을 담당한다.
///
/// 위성 어셈블리(.resx) 대신 어셈블리에 박아 넣은 JSON 을 쓴다.
/// 단일 파일로 배포할 때 동작이 더 예측 가능하고, 언어를 하나 보태고 싶은 사람이
/// JSON 파일 하나만 얹으면 되어 기여 문턱이 낮다.
/// </summary>
public static class Loc
{
    /// <summary>번역이 빠졌을 때 기대는 언어. 여기에는 모든 열쇠가 다 들어 있다.</summary>
    public const string FallbackCode = "en";

    public const string AutoCode = "auto";

    /// <summary>
    /// 지원 언어. 이름은 그 언어를 쓰는 사람이 읽을 수 있게 각자의 표기로 적는다 —
    /// 지금 화면이 무슨 언어든 자기 언어는 알아볼 수 있어야 하기 때문이다.
    /// </summary>
    public static IReadOnlyList<Language> Available { get; } =
    [
        new("en", "English"),
        new("ko", "한국어"),
        new("ja", "日本語"),
        new("zh-Hans", "简体中文"),
        new("es", "Español"),
        new("de", "Deutsch"),
    ];

    private static readonly Dictionary<string, Dictionary<string, string>> _tables = LoadAll();

    private static Dictionary<string, string> _current = _tables[FallbackCode];
    private static Dictionary<string, string> _fallback = _tables[FallbackCode];

    /// <summary>지금 쓰이는 언어 코드. "auto" 는 여기 담기지 않고 실제로 고른 코드가 담긴다.</summary>
    public static string CurrentCode { get; private set; } = FallbackCode;

    /// <summary>언어가 바뀌면 발생한다. 열려 있는 창이 글월을 다시 읽어야 한다.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// 언어를 고른다. <see cref="AutoCode"/> 를 주면 Windows 표시 언어를 따라간다.
    /// </summary>
    public static void Use(string? code)
    {
        string resolved = Resolve(code);
        if (resolved == CurrentCode) return;

        _current = _tables[resolved];
        CurrentCode = resolved;

        // 날짜와 숫자 서식도 함께 맞춘다. 글자만 바뀌고 1,234.5 표기가 그대로면 어색하다.
        ApplyNumberFormatting(resolved);

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static string Resolve(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code == AutoCode)
            return MatchSystemLanguage();

        return _tables.ContainsKey(code) ? code : FallbackCode;
    }

    /// <summary>
    /// Windows 표시 언어에 가장 가까운 것을 고른다.
    /// zh-CN 처럼 지역까지 붙은 이름은 zh-Hans 같은 큰 갈래로 접어 준다.
    /// </summary>
    private static string MatchSystemLanguage()
    {
        var culture = CultureInfo.CurrentUICulture;

        while (culture is not null && culture != CultureInfo.InvariantCulture)
        {
            if (_tables.ContainsKey(culture.Name)) return culture.Name;

            // zh-CN / zh-SG 는 간체, zh-TW / zh-HK 는 번체다.
            // 번체 표가 아직 없으므로 간체로 보내는 대신 영어로 둔다.
            if (culture.Name is "zh-CN" or "zh-SG" or "zh-Hans-CN") return "zh-Hans";

            culture = culture.Parent;
        }

        return FallbackCode;
    }

    private static void ApplyNumberFormatting(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);

            CultureInfo.CurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // 알 수 없는 이름이면 시스템 서식을 그대로 둔다. 글자만 바뀌어도 쓸 수는 있다.
        }
    }

    /// <summary>열쇠에 해당하는 글월. 없으면 영어, 그것도 없으면 열쇠 자체를 돌려준다.</summary>
    public static string T(string key)
    {
        if (_current.TryGetValue(key, out string? text)) return text;
        if (_fallback.TryGetValue(key, out text)) return text;

        // 열쇠를 그대로 보여 주면 번역이 빠진 자리가 눈에 띈다. 조용히 빈칸이 되는 것보다 낫다.
        return key;
    }

    /// <summary>자리표시자가 있는 글월. <c>{0}</c> 형태를 쓴다.</summary>
    public static string T(string key, params object?[] args)
    {
        string format = T(key);

        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            // 번역문에서 자리표시자를 잘못 적었을 때 앱이 죽지 않도록 한다.
            return format;
        }
    }

    private static Dictionary<string, Dictionary<string, string>> LoadAll()
    {
        var assembly = typeof(Loc).Assembly;
        var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (string resource in assembly.GetManifestResourceNames())
        {
            if (!resource.Contains(".Localization.strings.", StringComparison.Ordinal)) continue;
            if (!resource.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

            string code = resource[..^".json".Length];
            code = code[(code.LastIndexOf('.') + 1)..];

            // 파일 이름에는 점을 쓸 수 없어 zh-Hans 처럼 붙임표로 적는다.
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null) continue;

            try
            {
                var table = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
                if (table is not null) tables[code] = table;
            }
            catch (JsonException)
            {
                // 깨진 번역 파일 하나 때문에 앱이 못 뜨면 안 된다. 그 언어만 건너뛴다.
            }
        }

        // 영어는 반드시 있어야 한다. 없으면 빈 표라도 두어 T() 가 열쇠를 돌려주게 한다.
        tables.TryAdd(FallbackCode, []);

        return tables;
    }
}
