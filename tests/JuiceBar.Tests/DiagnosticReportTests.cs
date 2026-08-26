using System.Globalization;
using JuiceBar.Core.Diagnostics;
using JuiceBar.Core.Power;

namespace JuiceBar.Tests;

/// <summary>
/// 진단 보고서.
///
/// 두 가지가 중요하다. 문제를 푸는 데 필요한 것이 <b>들어 있어야</b> 하고,
/// 공개된 곳에 붙여 넣는 글이므로 사람이나 기기를 가리키는 것이 <b>없어야</b> 한다.
/// 뒤쪽이 특히 그렇다 — 한 번 새어 나가면 되돌릴 수 없다.
/// </summary>
public class DiagnosticReportTests
{
    private static PowerChannel Channel(string id, string label, string hardware, ChannelKind kind, double watts)
        => new(id, label, hardware, kind, watts);

    private static DiagnosticInput Sample(
        IReadOnlyList<PowerChannel>? channels = null,
        IReadOnlyCollection<string>? selected = null) => new()
    {
        AppVersion = "1.4.0",
        WindowsRelease = "Windows 11 Pro 24H2 (build 26200)",
        HasEnergyMeter = true,
        DriverInstalled = false,
        IsElevated = false,
        HasBattery = false,
        Channels = channels ??
        [
            Channel("emi:x#0", "CPU Package", "Energy Meter (Microsoft PPM)", ChannelKind.Cpu, 99.5),
            Channel("/gpu-nvidia/0/power/0", "GPU Package", "NVIDIA GeForce RTX 4080 SUPER", ChannelKind.Gpu, 31.9),
            Channel("/gpu-amd/0/power/0", "GPU Core", "AMD Radeon(TM) Graphics", ChannelKind.Gpu, 57.0),
            Channel("/amdcpu/0/power/16", "Package", "AMD Ryzen 9 7950X", ChannelKind.Cpu, 0),
        ],
        SelectedChannelIds = selected ?? ["emi:x#0", "/gpu-nvidia/0/power/0"],
        Quality = PowerQuality.SensorUncalibrated,
        WallWatts = 153.4,
        MeasuredWatts = 131.4,
        BaselineWatts = 35,
        Efficiency = 0.88,
        IsCalibrated = false,
        Language = "en",
        RateKind = "tiered",
    };

    // ─────────────── 담겨야 하는 것 ───────────────

    [Fact]
    public void The_report_says_which_windows_and_which_juicebar()
    {
        string report = DiagnosticReport.Build(Sample());

        Assert.Contains("Windows 11 Pro 24H2 (build 26200)", report);
        Assert.Contains("1.4.0", report);
    }

    [Fact]
    public void The_three_things_that_decide_whether_the_cpu_can_be_read_are_all_there()
    {
        // 에너지 미터·드라이버·권한. 이 셋을 모르면 CPU 가 왜 0W 인지 알 길이 없다.
        string report = DiagnosticReport.Build(Sample());

        Assert.Contains("Energy meter", report);
        Assert.Contains("PawnIO driver", report);
        Assert.Contains("Elevated", report);
    }

    [Fact]
    public void Every_channel_is_listed_whether_or_not_it_is_summed()
    {
        // 고르지 않은 채널이 곧 문제인 경우가 많다 — 빠뜨렸거나 두 번 셌거나.
        string report = DiagnosticReport.Build(Sample());

        Assert.Contains("CPU Package", report);
        Assert.Contains("GPU Package", report);
        Assert.Contains("GPU Core", report);
        Assert.Contains("AMD Ryzen 9 7950X", report);
    }

    [Fact]
    public void The_summed_channels_are_marked()
    {
        var lines = DiagnosticReport.DescribeChannels(Sample()).ToList();

        Assert.Equal(2, lines.Count(l => l.StartsWith('*')));
        Assert.Contains(lines, l => l.StartsWith('*') && l.Contains("CPU Package"));
        Assert.Contains(lines, l => !l.StartsWith('*') && l.Contains("GPU Core"));
    }

    [Fact]
    public void The_summed_channels_come_first()
    {
        // 합계에 무엇이 들어갔는지가 먼저 보여야 읽는 사람이 빨리 짚는다.
        var lines = DiagnosticReport.DescribeChannels(Sample()).ToList();

        Assert.StartsWith("*", lines[0]);
        Assert.StartsWith("*", lines[1]);
        Assert.DoesNotContain('*', lines[2][..1]);
    }

    [Fact]
    public void A_machine_with_no_power_sensors_at_all_still_produces_a_report()
    {
        // 이게 바로 신고할 만한 상태다. 여기서 빈 보고서가 나오면 아무 쓸모가 없다.
        string report = DiagnosticReport.Build(Sample(channels: [], selected: []));

        Assert.Contains("no power sensors reported", report);
        Assert.Contains("Windows 11", report);
    }

    // ─────────────── 담기면 안 되는 것 ───────────────

    [Fact]
    public void Channel_identifiers_are_left_out()
    {
        // 식별자는 길고 읽기 어렵기만 하다. 이름·하드웨어·값이면 진단에 충분하다.
        string report = DiagnosticReport.Build(Sample());

        Assert.DoesNotContain("/amdcpu/0/power/16", report);
        Assert.DoesNotContain("emi:x#0", report);
    }

    [Fact]
    public void Prices_never_appear_only_the_kind_of_tariff()
    {
        // 단가는 사는 곳을 좁혀 준다. 측정 문제를 푸는 데는 요금제 종류면 된다.
        var input = Sample() with { RateKind = "tiered" };
        string report = DiagnosticReport.Build(input);

        Assert.Contains("tiered", report);
        Assert.DoesNotContain("0.2641", report);
        Assert.DoesNotContain("KRW", report);
    }

    [Fact]
    public void Numbers_read_the_same_no_matter_where_the_reporter_lives()
    {
        // 독일에서 붙여 넣은 "99,5 W" 와 미국에서 붙여 넣은 "99.5 W" 가 섞이면
        // 비교도 검색도 안 된다. 보고서는 늘 같은 표기여야 한다.
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string german = DiagnosticReport.Build(Sample());

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            string invariant = DiagnosticReport.Build(Sample());

            Assert.Equal(invariant, german);
            Assert.Contains("99.5 W", german);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ─────────────── 상태 서술 ───────────────

    [Theory]
    [InlineData(PowerQuality.Measured, "measured directly")]
    [InlineData(PowerQuality.SensorCalibrated, "calibrated")]
    [InlineData(PowerQuality.SensorUncalibrated, "not calibrated")]
    [InlineData(PowerQuality.Estimated, "estimated")]
    public void The_measurement_quality_is_spelled_out(PowerQuality quality, string expected)
    {
        string report = DiagnosticReport.Build(Sample() with { Quality = quality });

        Assert.Contains(expected, report);
    }

    [Fact]
    public void The_report_is_a_markdown_block_ready_to_paste()
    {
        string report = DiagnosticReport.Build(Sample());

        Assert.StartsWith("### JuiceBar diagnostics", report);

        // 채널 목록은 코드 블록 안에 있어야 정렬이 유지된다.
        Assert.Equal(2, report.Split("```").Length - 1);
    }
}
