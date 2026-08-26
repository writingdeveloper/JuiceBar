using JuiceBar.Core.Tariff;

namespace JuiceBar.Tests;

public class BillingCycleTests
{
    private static DateTimeOffset Local(int year, int month, int day, int hour = 12)
        => new(new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Local));

    [Fact]
    public void A_first_of_month_cycle_spans_the_calendar_month()
    {
        var (start, end) = BillingCycle.Current(1, Local(2026, 8, 15));

        Assert.Equal(new DateTime(2026, 8, 1), start.LocalDateTime);
        Assert.Equal(new DateTime(2026, 9, 1), end.LocalDateTime);
    }

    [Fact]
    public void Before_the_anchor_day_the_cycle_started_last_month()
    {
        var (start, end) = BillingCycle.Current(15, Local(2026, 8, 3));

        Assert.Equal(new DateTime(2026, 7, 15), start.LocalDateTime);
        Assert.Equal(new DateTime(2026, 8, 15), end.LocalDateTime);
    }

    [Fact]
    public void On_the_anchor_day_the_new_cycle_has_already_begun()
    {
        var (start, _) = BillingCycle.Current(15, Local(2026, 8, 15));

        Assert.Equal(new DateTime(2026, 8, 15), start.LocalDateTime);
    }

    [Fact]
    public void An_anchor_day_past_the_end_of_a_short_month_is_pulled_back()
    {
        // 31일 시작인데 2월은 28일까지밖에 없다.
        var (start, end) = BillingCycle.Current(31, Local(2026, 2, 10));

        Assert.Equal(new DateTime(2026, 1, 31), start.LocalDateTime);
        Assert.Equal(new DateTime(2026, 2, 28), end.LocalDateTime);
    }

    [Fact]
    public void Cycles_are_contiguous_across_a_short_month()
    {
        var (_, februaryEnd) = BillingCycle.Current(31, Local(2026, 2, 10));
        var (marchStart, _) = BillingCycle.Current(31, Local(2026, 3, 10));

        Assert.Equal(februaryEnd.LocalDateTime, marchStart.LocalDateTime);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(99)]
    public void Out_of_range_anchor_days_are_clamped_rather_than_throwing(int day)
    {
        var (start, end) = BillingCycle.Current(day, Local(2026, 8, 15));

        Assert.True(end > start);
    }

    [Fact]
    public void Elapsed_fraction_is_zero_at_the_start_of_a_cycle()
    {
        var atStart = new DateTimeOffset(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Local));

        Assert.Equal(0, BillingCycle.ElapsedFraction(1, atStart), precision: 6);
    }

    [Fact]
    public void Elapsed_fraction_reaches_about_half_at_mid_cycle()
    {
        // 8월은 31일. 16일 12시면 대략 절반이다.
        var midpoint = new DateTimeOffset(new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Local));

        double fraction = BillingCycle.ElapsedFraction(1, midpoint);

        Assert.InRange(fraction, 0.49, 0.51);
    }

    [Fact]
    public void Elapsed_fraction_stays_within_bounds()
    {
        var late = new DateTimeOffset(new DateTime(2026, 8, 31, 23, 59, 0, DateTimeKind.Local));

        Assert.InRange(BillingCycle.ElapsedFraction(1, late), 0, 1);
    }
}
