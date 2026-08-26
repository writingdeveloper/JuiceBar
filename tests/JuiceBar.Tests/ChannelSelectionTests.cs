using JuiceBar.Core.Power;

namespace JuiceBar.Tests;

/// <summary>
/// 기본 채널 선택이 같은 전력을 두 번 세지 않는지 확인한다.
/// 표본은 Ryzen 9 7950X + RTX 4080 SUPER 에서 실제로 읽은 값이다.
/// </summary>
public class ChannelSelectionTests
{
    private static PowerChannel Channel(string id, string label, string hardware, ChannelKind kind, double watts)
        => new(id, label, hardware, kind, watts);

    private static List<PowerChannel> RyzenWithDiscreteGpu() =>
    [
        Channel("/amdcpu/0/power/0", "Core #1 (SMU)", "AMD Ryzen 9 7950X", ChannelKind.Cpu, 3.1),
        Channel("/amdcpu/0/power/1", "Core #2 (SMU)", "AMD Ryzen 9 7950X", ChannelKind.Cpu, 2.8),
        Channel("/amdcpu/0/power/16", "Package", "AMD Ryzen 9 7950X", ChannelKind.Cpu, 62.67),

        // 내장 그래픽. 이 값들은 위 Package 안에 이미 들어 있다.
        Channel("/gpu-amd/0/power/0", "GPU Core", "AMD Radeon(TM) Graphics", ChannelKind.Gpu, 35.0),
        Channel("/gpu-amd/0/power/1", "GPU SoC", "AMD Radeon(TM) Graphics", ChannelKind.Gpu, 11.0),

        Channel("/gpu-nvidia/0/power/0", "GPU Package", "NVIDIA GeForce RTX 4080 SUPER", ChannelKind.Gpu, 31.74),
    ];

    [Fact]
    public void Per_core_sensors_are_excluded_in_favour_of_the_package()
    {
        var selected = SensorReader.DefaultSelection(RyzenWithDiscreteGpu());

        Assert.Contains("/amdcpu/0/power/16", selected);
        Assert.DoesNotContain("/amdcpu/0/power/0", selected);
        Assert.DoesNotContain("/amdcpu/0/power/1", selected);
    }

    [Fact]
    public void Integrated_gpu_rails_are_excluded_when_the_cpu_package_covers_them()
    {
        var selected = SensorReader.DefaultSelection(RyzenWithDiscreteGpu());

        Assert.DoesNotContain("/gpu-amd/0/power/0", selected);
        Assert.DoesNotContain("/gpu-amd/0/power/1", selected);
    }

    [Fact]
    public void Discrete_gpu_is_included()
    {
        var selected = SensorReader.DefaultSelection(RyzenWithDiscreteGpu());

        Assert.Contains("/gpu-nvidia/0/power/0", selected);
    }

    [Fact]
    public void The_default_total_matches_cpu_package_plus_discrete_gpu()
    {
        var channels = RyzenWithDiscreteGpu();
        var selected = SensorReader.DefaultSelection(channels);

        double total = channels.Where(c => selected.Contains(c.Id)).Sum(c => c.Watts);

        // 62.67 + 31.74. iGPU 46W 가 섞이면 이 값이 크게 어긋난다.
        Assert.Equal(94.41, total, precision: 2);
    }

    [Fact]
    public void Integrated_gpu_is_kept_when_no_cpu_package_reading_exists()
    {
        // PawnIO 가 없으면 CPU Package 가 0으로 온다. 그럴 때는 iGPU 값이라도 쓰는 편이 낫다.
        var channels = RyzenWithDiscreteGpu()
            .Select(c => c.Label == "Package" ? c with { Watts = 0 } : c)
            .ToList();

        var selected = SensorReader.DefaultSelection(channels);

        Assert.Contains("/gpu-amd/0/power/0", selected);
        Assert.Contains("/gpu-amd/0/power/1", selected);
    }

    [Fact]
    public void Battery_channels_are_never_summed()
    {
        var channels = RyzenWithDiscreteGpu();
        channels.Add(Channel("/battery/0/power/0", "Discharge Rate", "Battery", ChannelKind.Battery, 18.0));

        var selected = SensorReader.DefaultSelection(channels);

        Assert.DoesNotContain("/battery/0/power/0", selected);
    }

    [Theory]
    [InlineData("AMD Radeon(TM) Graphics", true)]
    [InlineData("Intel(R) UHD Graphics 770", true)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", true)]
    [InlineData("NVIDIA GeForce RTX 4080 SUPER", false)]
    [InlineData("AMD Radeon RX 7900 XTX", false)]
    [InlineData("NVIDIA GeForce RTX 5090", false)]
    public void Integrated_graphics_are_recognised_by_name(string name, bool expected)
    {
        Assert.Equal(expected, SensorReader.IsLikelyIntegratedGpu(name));
    }
}
