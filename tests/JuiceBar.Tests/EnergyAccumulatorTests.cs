using JuiceBar.Core.Energy;

namespace JuiceBar.Tests;

public class EnergyAccumulatorTests
{
    [Fact]
    public void First_sample_only_establishes_a_baseline()
    {
        var accumulator = new EnergyAccumulator();

        double delta = accumulator.Add(100, TimeSpan.FromSeconds(1));

        Assert.Equal(0, delta);
        Assert.Equal(0, accumulator.TotalWattHours);
    }

    [Fact]
    public void Constant_power_integrates_to_the_expected_energy()
    {
        var accumulator = new EnergyAccumulator();

        // 100W 를 한 시간 유지하면 100Wh. 1초 간격 3600회로 재현한다.
        accumulator.Add(100, TimeSpan.FromSeconds(1));
        for (int i = 0; i < 3600; i++)
            accumulator.Add(100, TimeSpan.FromSeconds(1));

        Assert.Equal(100, accumulator.TotalWattHours, precision: 6);
    }

    /// <summary>한 시간짜리 구간을 한 번에 넣는 테스트용. 기본 상한(30초)에 걸리지 않게 한다.</summary>
    private static EnergyAccumulator LongInterval() => new(TimeSpan.FromDays(1));

    [Fact]
    public void Linear_ramp_uses_the_trapezoid_midpoint()
    {
        var accumulator = LongInterval();

        accumulator.Add(0, TimeSpan.FromSeconds(1));
        accumulator.Add(200, TimeSpan.FromHours(1));

        // 0W 에서 200W 로 선형 증가하며 한 시간 → 평균 100W → 100Wh
        Assert.Equal(100, accumulator.TotalWattHours, precision: 6);
    }

    [Fact]
    public void Oversized_gaps_are_discarded_rather_than_integrated()
    {
        var accumulator = new EnergyAccumulator();

        accumulator.Add(100, TimeSpan.FromSeconds(1));

        // 절전 모드에서 8시간 자고 돌아온 상황. 그 시간을 100W 로 쳐서는 안 된다.
        double delta = accumulator.Add(100, TimeSpan.FromHours(8));

        Assert.Equal(0, delta);
        Assert.Equal(0, accumulator.TotalWattHours);
        Assert.Equal(1, accumulator.SkippedGaps);
    }

    [Fact]
    public void Accumulation_resumes_normally_after_a_discarded_gap()
    {
        var accumulator = new EnergyAccumulator();

        accumulator.Add(100, TimeSpan.FromSeconds(1));
        accumulator.Add(100, TimeSpan.FromHours(8));

        // 절전에서 깨어난 뒤 정상 간격으로 한 시간을 채운다.
        for (int i = 0; i < 3600; i++)
            accumulator.Add(100, TimeSpan.FromSeconds(1));

        Assert.Equal(100, accumulator.TotalWattHours, precision: 6);
        Assert.Equal(1, accumulator.SkippedGaps);
    }

    [Fact]
    public void Backwards_clock_movement_contributes_nothing()
    {
        var accumulator = new EnergyAccumulator();

        accumulator.Add(100, TimeSpan.FromSeconds(1));
        double delta = accumulator.Add(100, TimeSpan.FromSeconds(-5));

        Assert.Equal(0, delta);
        Assert.Equal(0, accumulator.TotalWattHours);
    }

    [Fact]
    public void Reset_clears_the_running_total()
    {
        var accumulator = LongInterval();

        accumulator.Add(100, TimeSpan.FromSeconds(1));
        accumulator.Add(100, TimeSpan.FromHours(1));
        accumulator.Reset();

        Assert.Equal(0, accumulator.TotalWattHours);
    }

    [Fact]
    public void Kilowatt_hours_are_watt_hours_over_a_thousand()
    {
        var accumulator = LongInterval();

        accumulator.Add(2000, TimeSpan.FromSeconds(1));
        accumulator.Add(2000, TimeSpan.FromHours(1));

        Assert.Equal(2, accumulator.TotalKilowattHours, precision: 6);
    }
}
