using JuiceBar.Core.Tariff;

namespace JuiceBar.Tests;

public class TariffCalculatorTests
{
    [Fact]
    public void Flat_rate_multiplies_usage_by_price()
    {
        var config = new TariffConfig { Rate = new FlatRate { PricePerKwh = 250 } };

        var result = TariffCalculator.Calculate(config, new CycleUsage(10));

        Assert.Equal(2500, result.EnergyCharge, precision: 6);
        Assert.Equal(2500, result.Total, precision: 6);
    }

    [Fact]
    public void Fixed_charge_and_taxes_apply_to_the_subtotal()
    {
        var config = new TariffConfig
        {
            Rate = new FlatRate { PricePerKwh = 100 },
            FixedChargePerMonth = 1000,
            Taxes = [new TaxItem("VAT", 0.10), new TaxItem("Fund", 0.037)],
        };

        var result = TariffCalculator.Calculate(config, new CycleUsage(10));

        // 소계 = 1000 + 1000 = 2000, 세금 = 200 + 74
        Assert.Equal(1000, result.EnergyCharge, precision: 6);
        Assert.Equal(2, result.Taxes.Count);
        Assert.Equal(200, result.Taxes[0].Amount, precision: 6);
        Assert.Equal(74, result.Taxes[1].Amount, precision: 6);
        Assert.Equal(2274, result.Total, precision: 6);
    }

    private static TariffConfig ThreeTierConfig() => new()
    {
        Rate = new TieredRate
        {
            Tiers =
            [
                new Tier(200, 100),
                new Tier(400, 200),
                new Tier(null, 300),
            ],
        },
    };

    [Theory]
    // 첫 구간 안
    [InlineData(150, 15000)]
    // 첫 구간 경계 — 아직 두 번째 단가가 붙지 않는다
    [InlineData(200, 20000)]
    // 두 구간에 걸침: 200×100 + 100×200
    [InlineData(300, 40000)]
    // 세 구간에 걸침: 200×100 + 200×200 + 100×300
    [InlineData(500, 90000)]
    public void Tiered_rate_charges_each_band_at_its_own_price(double kwh, double expected)
    {
        var result = TariffCalculator.Calculate(ThreeTierConfig(), new CycleUsage(kwh));

        Assert.Equal(expected, result.EnergyCharge, precision: 6);
    }

    [Fact]
    public void Tier_warning_reports_distance_to_the_next_band()
    {
        var warning = TariffCalculator.GetTierWarning(ThreeTierConfig(), totalKwh: 188);

        Assert.NotNull(warning);
        Assert.Equal(12, warning.KwhUntilNextTier, precision: 6);
        Assert.Equal(100, warning.CurrentPricePerKwh, precision: 6);
        Assert.Equal(200, warning.NextPricePerKwh, precision: 6);
    }

    [Fact]
    public void Tier_warning_is_absent_in_the_final_unbounded_band()
    {
        Assert.Null(TariffCalculator.GetTierWarning(ThreeTierConfig(), totalKwh: 450));
    }

    [Fact]
    public void Tier_warning_is_absent_for_flat_rates()
    {
        var config = new TariffConfig { Rate = new FlatRate { PricePerKwh = 250 } };

        Assert.Null(TariffCalculator.GetTierWarning(config, totalKwh: 100));
    }

    [Fact]
    public void Marginal_price_follows_the_active_tier()
    {
        var config = ThreeTierConfig();
        var now = DateTimeOffset.Now;

        Assert.Equal(100, TariffCalculator.MarginalPricePerKwh(config, 50, now), precision: 6);
        Assert.Equal(200, TariffCalculator.MarginalPricePerKwh(config, 250, now), precision: 6);
        Assert.Equal(300, TariffCalculator.MarginalPricePerKwh(config, 900, now), precision: 6);
    }

    private static TouRate PeakOffPeak() => new()
    {
        Periods = [new TouPeriod("peak", "mon-fri", "09:00", "22:00", 0.32)],
        DefaultPeriodName = "offpeak",
        DefaultPricePerKwh = 0.12,
    };

    [Fact]
    public void Tou_rate_prices_each_period_separately()
    {
        var config = new TariffConfig { Rate = PeakOffPeak() };

        var usage = new CycleUsage(
            TotalKwh: 100,
            KwhByPeriod: new Dictionary<string, double> { ["peak"] = 40, ["offpeak"] = 60 });

        var result = TariffCalculator.Calculate(config, usage);

        Assert.Equal((40 * 0.32) + (60 * 0.12), result.EnergyCharge, precision: 6);
    }

    [Fact]
    public void Tou_rate_falls_back_to_the_default_price_without_period_data()
    {
        var config = new TariffConfig { Rate = PeakOffPeak() };

        var result = TariffCalculator.Calculate(config, new CycleUsage(100));

        Assert.Equal(12, result.EnergyCharge, precision: 6);
    }

    [Fact]
    public void Tou_resolves_a_weekday_afternoon_as_peak()
    {
        // 2026-08-26 은 수요일
        var wednesdayNoon = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

        var (name, price) = TariffCalculator.ResolvePeriod(PeakOffPeak(), wednesdayNoon);

        Assert.Equal("peak", name);
        Assert.Equal(0.32, price, precision: 6);
    }

    [Fact]
    public void Tou_resolves_a_weekend_as_the_default_period()
    {
        // 2026-08-29 는 토요일
        var saturdayNoon = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

        var (name, _) = TariffCalculator.ResolvePeriod(PeakOffPeak(), saturdayNoon);

        Assert.Equal("offpeak", name);
    }

    [Fact]
    public void Tou_resolves_a_weekday_night_as_the_default_period()
    {
        var wednesdayNight = new DateTimeOffset(2026, 8, 26, 23, 30, 0, TimeSpan.Zero);

        var (name, _) = TariffCalculator.ResolvePeriod(PeakOffPeak(), wednesdayNight);

        Assert.Equal("offpeak", name);
    }

    [Fact]
    public void Tou_period_can_span_midnight()
    {
        var rate = new TouRate
        {
            Periods = [new TouPeriod("night", "all", "22:00", "06:00", 0.08)],
            DefaultPeriodName = "day",
            DefaultPricePerKwh = 0.25,
        };

        Assert.Equal("night", TariffCalculator.ResolvePeriod(rate, At(23, 0)).Name);
        Assert.Equal("night", TariffCalculator.ResolvePeriod(rate, At(3, 0)).Name);
        Assert.Equal("day", TariffCalculator.ResolvePeriod(rate, At(12, 0)).Name);

        static DateTimeOffset At(int hour, int minute)
            => new(2026, 8, 26, hour, minute, 0, TimeSpan.Zero);
    }

    [Theory]
    [InlineData("all", DayOfWeek.Sunday, true)]
    [InlineData("mon-fri", DayOfWeek.Wednesday, true)]
    [InlineData("mon-fri", DayOfWeek.Saturday, false)]
    [InlineData("sat,sun", DayOfWeek.Sunday, true)]
    [InlineData("sat,sun", DayOfWeek.Monday, false)]
    // 주말을 감싸는 범위
    [InlineData("fri-mon", DayOfWeek.Sunday, true)]
    [InlineData("fri-mon", DayOfWeek.Wednesday, false)]
    public void Day_specifications_are_parsed(string spec, DayOfWeek day, bool expected)
    {
        Assert.Equal(expected, TariffCalculator.MatchesDay(spec, day));
    }
}
