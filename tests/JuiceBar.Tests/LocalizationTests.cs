using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using JuiceBar.Core.Localization;

namespace JuiceBar.Tests;

/// <summary>
/// 번역은 손으로 관리하는 표라서 조용히 어긋나기 쉽다.
/// 열쇠가 빠지거나 자리표시자가 달라지면 화면에 열쇠 이름이 그대로 나오거나
/// 서식이 깨지므로, 그런 일이 커밋되기 전에 여기서 걸러 낸다.
/// </summary>
[Collection(LocalizationCollection.Name)]
public class LocalizationTests
{
    private static readonly Dictionary<string, Dictionary<string, string>> Tables = LoadTables();

    private static Dictionary<string, Dictionary<string, string>> LoadTables()
    {
        var assembly = typeof(Loc).Assembly;
        var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        foreach (string resource in assembly.GetManifestResourceNames())
        {
            if (!resource.Contains(".Localization.strings.", StringComparison.Ordinal)) continue;

            string code = resource[..^".json".Length];
            code = code[(code.LastIndexOf('.') + 1)..];

            using var stream = assembly.GetManifestResourceStream(resource)!;
            tables[code] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        }

        return tables;
    }

    public static TheoryData<string> AllLanguages()
    {
        var data = new TheoryData<string>();
        foreach (var language in Loc.Available) data.Add(language.Code);
        return data;
    }

    [Fact]
    public void Every_declared_language_ships_a_string_table()
    {
        foreach (var language in Loc.Available)
            Assert.True(Tables.ContainsKey(language.Code), $"{language.Code}.json 이 없습니다.");
    }

    [Fact]
    public void English_is_present_because_everything_falls_back_to_it()
    {
        Assert.True(Tables.ContainsKey(Loc.FallbackCode));
        Assert.NotEmpty(Tables[Loc.FallbackCode]);
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void No_language_is_missing_a_key(string code)
    {
        var reference = Tables[Loc.FallbackCode];
        var table = Tables[code];

        var missing = reference.Keys.Where(key => !table.ContainsKey(key)).OrderBy(k => k).ToList();

        Assert.True(missing.Count == 0, $"{code}.json 에 빠진 열쇠: {string.Join(", ", missing)}");
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void No_language_has_a_key_that_english_does_not(string code)
    {
        var reference = Tables[Loc.FallbackCode];
        var table = Tables[code];

        // 영어에 없는 열쇠는 이름이 바뀐 뒤 남은 찌꺼기이거나 오타다. 어느 쪽이든 쓰이지 않는다.
        var extra = table.Keys.Where(key => !reference.ContainsKey(key)).OrderBy(k => k).ToList();

        Assert.True(extra.Count == 0, $"{code}.json 에만 있는 열쇠: {string.Join(", ", extra)}");
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Placeholders_match_the_english_original(string code)
    {
        var reference = Tables[Loc.FallbackCode];
        var table = Tables[code];
        var problems = new List<string>();

        foreach (var (key, english) in reference)
        {
            if (!table.TryGetValue(key, out string? translated)) continue;

            var expected = Placeholders(english);
            var actual = Placeholders(translated);

            // 순서는 언어마다 달라도 되지만, 쓰이는 번호는 같아야 한다.
            // {1} 이 빠지면 값이 사라지고, 없는 {2} 를 쓰면 서식이 터진다.
            if (!expected.SetEquals(actual))
                problems.Add($"{key} (영어 {Show(expected)} / {code} {Show(actual)})");
        }

        Assert.True(problems.Count == 0,
            $"{code}.json 의 자리표시자가 맞지 않습니다:\n  {string.Join("\n  ", problems)}");
    }

    private static HashSet<string> Placeholders(string text)
        => [.. Regex.Matches(text, @"\{(\d+)\}").Select(m => m.Groups[1].Value)];

    private static string Show(HashSet<string> values)
        => values.Count == 0 ? "없음" : "{" + string.Join("},{", values.OrderBy(v => v)) + "}";

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void No_translation_is_left_blank(string code)
    {
        var blank = Tables[code]
            .Where(pair => string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => pair.Key)
            .ToList();

        Assert.True(blank.Count == 0, $"{code}.json 의 빈 값: {string.Join(", ", blank)}");
    }

    [Fact]
    public void Switching_language_changes_what_lookups_return()
    {
        Loc.Use("en");
        string english = Loc.T("popup.today");

        Loc.Use("ko");
        string korean = Loc.T("popup.today");

        Assert.Equal("Today", english);
        Assert.Equal("오늘", korean);

        Loc.Use("en");
    }

    [Fact]
    public void An_unknown_language_falls_back_to_english()
    {
        Loc.Use("xx-XX");

        Assert.Equal(Loc.FallbackCode, Loc.CurrentCode);
        Loc.Use("en");
    }

    [Fact]
    public void An_unknown_key_returns_the_key_so_the_gap_is_visible()
    {
        Assert.Equal("no.such.key", Loc.T("no.such.key"));
    }

    [Fact]
    public void Formatting_survives_a_translation_with_a_bad_placeholder()
    {
        // 서식이 깨져도 예외로 앱이 죽어서는 안 된다.
        Loc.Use("en");

        string result = Loc.T("popup.today", "unused");

        Assert.False(string.IsNullOrEmpty(result));
    }
}
