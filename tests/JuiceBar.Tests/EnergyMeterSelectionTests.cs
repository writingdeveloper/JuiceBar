using JuiceBar.Core.Power;

namespace JuiceBar.Tests;

/// <summary>
/// 에너지 미터가 합류하면서 "무엇을 합산할지"가 다시 어려워졌다.
/// 같은 CPU 전력을 두 곳에서 읽을 수 있게 됐기 때문이다.
///
/// 표본은 Ryzen 9 7950X + RTX 4080 SUPER 에서 실제로 읽은 값이다. 승격 여부에 따라
/// LibreHardwareMonitor 의 CPU 값이 62.67W 였다가 0W 가 되는 것까지 그대로 옮겼다.
/// </summary>
public class EnergyMeterSelectionTests
{
    private const string MeterName = "Energy Meter (Microsoft PPM)";

    private static PowerChannel Channel(string id, string label, string hardware, ChannelKind kind, double watts)
        => new(id, label, hardware, kind, watts);

    private static PowerChannel MeterPackage(double watts)
        => Channel("emi:\\\\?\\acpi#cpu#0#{guid}#0", "CPU Package", MeterName, ChannelKind.Cpu, watts);

    private static PowerChannel MeterDram(double watts)
        => Channel("emi:\\\\?\\acpi#cpu#0#{guid}#1", "DRAM", MeterName, ChannelKind.Other, watts);

    /// <summary>
    /// PawnIO 없이(또는 승격 없이) 실행한 상태.
    /// LibreHardwareMonitor 는 CPU 를 0W 로 보고하고, 내장 GPU 만 값이 나온다.
    /// </summary>
    private static List<PowerChannel> WithoutDriver() =>
    [
        Channel("/amdcpu/0/power/16", "Package", "AMD Ryzen 9 7950X", ChannelKind.Cpu, 0),
        Channel("/amdcpu/0/power/0", "Core #1 (SMU)", "AMD Ryzen 9 7950X", ChannelKind.Cpu, 0),

        Channel("/gpu-amd/0/power/0", "GPU Core", "AMD Radeon(TM) Graphics", ChannelKind.Gpu, 22.0),
        Channel("/gpu-amd/0/power/1", "GPU SoC", "AMD Radeon(TM) Graphics", ChannelKind.Gpu, 10.0),

        Channel("/gpu-nvidia/0/power/0", "GPU Package", "NVIDIA GeForce RTX 4080 SUPER", ChannelKind.Gpu, 8.95),
    ];

    /// <summary>PawnIO 가 깔려 있고 승격되어 CPU 도 읽히는 상태.</summary>
    private static List<PowerChannel> WithDriver() =>
    [
        Channel("/amdcpu/0/power/16", "Package", "AMD Ryzen 9 7950X", ChannelKind.Cpu, 62.67),
        Channel("/amdcpu/0/power/0", "Core #1 (SMU)", "AMD Ryzen 9 7950X", ChannelKind.Cpu, 3.1),

        Channel("/gpu-amd/0/power/0", "GPU Core", "AMD Radeon(TM) Graphics", ChannelKind.Gpu, 35.0),
        Channel("/gpu-amd/0/power/1", "GPU SoC", "AMD Radeon(TM) Graphics", ChannelKind.Gpu, 11.0),

        Channel("/gpu-nvidia/0/power/0", "GPU Package", "NVIDIA GeForce RTX 4080 SUPER", ChannelKind.Gpu, 31.74),
    ];

    private static double TotalOf(List<PowerChannel> channels)
    {
        var selected = SensorReader.DefaultSelection(channels);
        return channels.Where(c => selected.Contains(c.Id)).Sum(c => c.Watts);
    }

    // ─────────────── 드라이버 없이 ───────────────

    [Fact]
    public void The_energy_meter_supplies_cpu_power_when_the_driver_cannot()
    {
        // 이게 이 기능의 핵심이다. 드라이버 없이도 CPU 가 총합에 들어가야 한다.
        var channels = WithoutDriver();
        channels.Add(MeterPackage(66.61));

        var selected = SensorReader.DefaultSelection(channels);

        Assert.Contains(MeterPackage(0).Id, selected);
    }

    [Fact]
    public void The_integrated_gpu_stops_being_double_counted_once_the_meter_reports()
    {
        // 실제로 있던 문제다. LibreHardwareMonitor 의 CPU 가 0W 라 "패키지 값이 없다"고
        // 판단해 내장 GPU 를 더했는데, 에너지 미터가 주는 패키지에는 그 32W 가 이미 들어 있다.
        var channels = WithoutDriver();
        channels.Add(MeterPackage(66.61));

        var selected = SensorReader.DefaultSelection(channels);

        Assert.DoesNotContain("/gpu-amd/0/power/0", selected);
        Assert.DoesNotContain("/gpu-amd/0/power/1", selected);
    }

    [Fact]
    public void Without_the_meter_the_total_is_only_the_graphics_cards()
    {
        // 비교 기준. 에너지 미터가 없으면 CPU 몫이 통째로 빠지고,
        // 내장 GPU 가 잘못 섞여 들어온다.
        Assert.Equal(22.0 + 10.0 + 8.95, TotalOf(WithoutDriver()), precision: 2);
    }

    [Fact]
    public void With_the_meter_the_total_is_the_cpu_package_plus_the_discrete_card()
    {
        var channels = WithoutDriver();
        channels.Add(MeterPackage(66.61));

        Assert.Equal(66.61 + 8.95, TotalOf(channels), precision: 2);
    }

    // ─────────────── 드라이버가 있을 때 ───────────────

    [Fact]
    public void The_cpu_is_never_counted_from_both_sources_at_once()
    {
        // 둘 다 살아 있는 경우가 가장 위험하다. 같은 RAPL 카운터를 두 곳에서 읽으므로
        // 그냥 더하면 CPU 전력이 정확히 두 배가 된다.
        var channels = WithDriver();
        channels.Add(MeterPackage(62.71));

        var selected = SensorReader.DefaultSelection(channels);

        Assert.Contains(MeterPackage(0).Id, selected);
        Assert.DoesNotContain("/amdcpu/0/power/16", selected);
    }

    [Fact]
    public void With_both_sources_the_total_still_counts_the_cpu_once()
    {
        var channels = WithDriver();
        channels.Add(MeterPackage(62.71));

        Assert.Equal(62.71 + 31.74, TotalOf(channels), precision: 2);
    }

    [Fact]
    public void A_meter_that_reports_nothing_does_not_push_out_a_working_sensor()
    {
        // 미터 채널이 있어도 0W 만 낸다면 재고 있는 게 아니다.
        // 그 상태로 LibreHardwareMonitor 값을 밀어내면 CPU 를 통째로 잃는다.
        var channels = WithDriver();
        channels.Add(MeterPackage(0));

        var selected = SensorReader.DefaultSelection(channels);

        Assert.Contains("/amdcpu/0/power/16", selected);
        Assert.DoesNotContain(MeterPackage(0).Id, selected);
        Assert.Equal(62.67 + 31.74, TotalOf(channels), precision: 2);
    }

    // ─────────────── 그 밖의 채널 ───────────────

    [Fact]
    public void A_dram_channel_is_added_because_it_sits_outside_the_package()
    {
        var channels = WithoutDriver();
        channels.Add(MeterPackage(66.61));
        channels.Add(MeterDram(4.2));

        var selected = SensorReader.DefaultSelection(channels);

        Assert.Contains(MeterDram(0).Id, selected);
        Assert.Equal(66.61 + 4.2 + 8.95, TotalOf(channels), precision: 2);
    }

    [Fact]
    public void A_meter_channel_is_recognised_by_its_identifier()
    {
        Assert.True(SensorReader.IsEnergyMeterChannel("emi:\\\\?\\acpi#cpu#0#0"));
        Assert.False(SensorReader.IsEnergyMeterChannel("/amdcpu/0/power/16"));
        Assert.False(SensorReader.IsEnergyMeterChannel("/gpu-nvidia/0/power/0"));
    }

    [Fact]
    public void A_machine_with_no_meter_behaves_exactly_as_before()
    {
        // 에너지 미터를 내보내지 않는 하드웨어도 많다. 그런 PC 에서 동작이 달라지면 안 된다.
        var channels = WithDriver();
        var selected = SensorReader.DefaultSelection(channels);

        Assert.Contains("/amdcpu/0/power/16", selected);
        Assert.DoesNotContain("/gpu-amd/0/power/0", selected);
        Assert.Equal(62.67 + 31.74, TotalOf(channels), precision: 2);
    }
}
