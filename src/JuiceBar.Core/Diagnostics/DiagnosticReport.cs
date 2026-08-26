using System.Globalization;
using System.Text;
using JuiceBar.Core.Power;

namespace JuiceBar.Core.Diagnostics;

/// <summary>진단 보고서를 만드는 데 필요한 값 한 벌.</summary>
public sealed record DiagnosticInput
{
    public required string AppVersion { get; init; }

    /// <summary>"Windows 11 Pro 24H2 (build 26200)" 같은 한 줄.</summary>
    public required string WindowsRelease { get; init; }

    public required bool HasEnergyMeter { get; init; }
    public required bool DriverInstalled { get; init; }
    public required bool IsElevated { get; init; }
    public required bool HasBattery { get; init; }

    public required IReadOnlyList<PowerChannel> Channels { get; init; }
    public required IReadOnlyCollection<string> SelectedChannelIds { get; init; }

    public required PowerQuality Quality { get; init; }
    public required double WallWatts { get; init; }
    public required double MeasuredWatts { get; init; }
    public required double BaselineWatts { get; init; }
    public required double Efficiency { get; init; }
    public required bool IsCalibrated { get; init; }

    /// <summary>화면에 쓰는 언어. 서식 문제를 재현할 때 필요하다.</summary>
    public required string Language { get; init; }

    /// <summary>요금제의 종류만. flat / tiered / tou.</summary>
    public required string RateKind { get; init; }
}

/// <summary>
/// GitHub 이슈에 그대로 붙여 넣을 수 있는 진단 보고서를 만든다.
///
/// 전력 측정이 되고 안 되고는 기기마다 갈린다 — 어떤 CPU 인지, Windows 몇인지,
/// 에너지 미터가 있는지, 드라이버가 깔렸는지. 그걸 대화로 하나씩 물어보는 것은
/// 서로에게 고문이라, 필요한 것을 한 번에 담아 내놓는다.
///
/// <b>공개된 곳에 붙여 넣을 글이다.</b> 그래서 담는 것을 좁게 잡았다 —
/// 측정 문제를 푸는 데 쓰이는 것만 넣고, 사람이나 기기를 가리키는 것은 뺀다.
/// 컴퓨터 이름, 장비 식별자, 파일 경로(사용자 이름이 들어 있다), 사용량, 요금 단가는
/// 모두 들어가지 않는다. 요금제는 종류만 적는다 — 단가는 사는 곳을 좁혀 주기 때문이다.
/// </summary>
public static class DiagnosticReport
{
    public static string Build(DiagnosticInput input)
    {
        var text = new StringBuilder();

        text.AppendLine("### JuiceBar diagnostics");
        text.AppendLine();
        text.AppendLine("| | |");
        text.AppendLine("|---|---|");

        Row(text, "JuiceBar", input.AppVersion);
        Row(text, "Windows", input.WindowsRelease);
        Row(text, "Energy meter", input.HasEnergyMeter ? "available" : "not present");
        Row(text, "PawnIO driver", input.DriverInstalled ? "installed" : "not installed");
        Row(text, "Elevated", YesNo(input.IsElevated));
        Row(text, "Battery", YesNo(input.HasBattery));
        Row(text, "Measurement", DescribeQuality(input.Quality));
        Row(text, "Calibration", input.IsCalibrated
            ? $"baseline {Watts(input.BaselineWatts)}, efficiency {Number(input.Efficiency, "0.00")}"
            : $"defaults (baseline {Watts(input.BaselineWatts)}, efficiency {Number(input.Efficiency, "0.00")})");
        Row(text, "Reading", $"{Watts(input.WallWatts)} at the wall, {Watts(input.MeasuredWatts)} from sensors");
        Row(text, "Rate kind", input.RateKind);
        Row(text, "Language", input.Language);

        text.AppendLine();
        text.AppendLine("Power channels — `*` marks the ones being summed:");
        text.AppendLine();
        text.AppendLine("```");

        if (input.Channels.Count == 0)
        {
            text.AppendLine("(no power sensors reported)");
        }
        else
        {
            foreach (string line in DescribeChannels(input)) text.AppendLine(line);
        }

        text.AppendLine("```");

        return text.ToString();
    }

    /// <summary>
    /// 채널 한 줄씩. 고른 것에 별표를 붙인다.
    ///
    /// 식별자는 싣지 않는다 — 길고 읽기 어려운 데다, 무엇이 잘못됐는지 보는 데는
    /// 이름·하드웨어·값 세 가지면 충분하다.
    /// </summary>
    internal static IEnumerable<string> DescribeChannels(DiagnosticInput input)
    {
        var selected = new HashSet<string>(input.SelectedChannelIds, StringComparer.Ordinal);

        // 고른 것을 위로 올려서, 합계에 무엇이 들어갔는지 먼저 보이게 한다.
        var ordered = input.Channels
            .OrderByDescending(c => selected.Contains(c.Id))
            .ThenByDescending(c => c.Watts)
            .ThenBy(c => c.Label, StringComparer.Ordinal);

        foreach (var channel in ordered)
        {
            string mark = selected.Contains(channel.Id) ? "*" : " ";
            yield return $"{mark} {Watts(channel.Watts),9}  {channel.Kind,-7}  {channel.Label,-18}  {channel.HardwareName}";
        }
    }

    private static string DescribeQuality(PowerQuality quality) => quality switch
    {
        PowerQuality.Measured => "measured directly (battery or meter)",
        PowerQuality.SensorCalibrated => "sensors, calibrated",
        PowerQuality.SensorUncalibrated => "sensors, not calibrated",
        _ => "estimated from CPU load",
    };

    private static void Row(StringBuilder text, string name, string value)
        => text.AppendLine($"| {name} | {value} |");

    private static string YesNo(bool value) => value ? "yes" : "no";

    /// <summary>
    /// 보고서는 어느 나라 사람이 붙여 넣든 같은 모양이어야 읽고 비교할 수 있다.
    /// 그래서 숫자는 화면 표기와 달리 항상 고정 문화권으로 쓴다.
    /// </summary>
    private static string Watts(double value) => $"{Number(value, "0.0")} W";

    private static string Number(double value, string format)
        => value.ToString(format, CultureInfo.InvariantCulture);
}
