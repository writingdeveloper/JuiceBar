using System.Globalization;

namespace JuiceBar.Core.Tariff;

/// <summary>한 청구 주기 동안의 사용량. 시간대별 요금제를 위해 구간별로도 나눠 담는다.</summary>
public sealed record CycleUsage(
    double TotalKwh,
    IReadOnlyDictionary<string, double>? KwhByPeriod = null);

public sealed record CostBreakdown(
    double EnergyCharge,
    double FixedCharge,
    IReadOnlyList<TaxCharge> Taxes,
    double Total);

public sealed record TaxCharge(string Name, double Amount);

/// <summary>다음 누진 구간까지 얼마나 남았는지. 사용자에게 경고를 띄우는 데 쓴다.</summary>
public sealed record TierWarning(
    double KwhUntilNextTier,
    double CurrentPricePerKwh,
    double NextPricePerKwh);

public static class TariffCalculator
{
    public static CostBreakdown Calculate(TariffConfig config, CycleUsage usage)
    {
        double energyCharge = config.Rate switch
        {
            FlatRate flat => usage.TotalKwh * flat.PricePerKwh,
            TieredRate tiered => CalculateTiered(tiered, usage.TotalKwh),
            TouRate tou => CalculateTou(tou, usage),
            _ => 0,
        };

        double subtotal = energyCharge + config.FixedChargePerMonth;

        // 세금은 소계에 각각 곱한다. 세금 위에 세금을 얹는 구조는 지원하지 않는다 —
        // 나라마다 규칙이 다르고, 이 앱의 정확도 목표를 넘어서는 복잡도다.
        var taxes = new List<TaxCharge>(config.Taxes.Count);
        double taxTotal = 0;

        foreach (var tax in config.Taxes)
        {
            double amount = subtotal * tax.Rate;
            taxes.Add(new TaxCharge(tax.Name, amount));
            taxTotal += amount;
        }

        return new CostBreakdown(
            energyCharge,
            config.FixedChargePerMonth,
            taxes,
            subtotal + taxTotal);
    }

    /// <summary>구간을 차례로 채워 나가며 각 구간의 단가를 적용한다.</summary>
    private static double CalculateTiered(TieredRate rate, double totalKwh)
    {
        if (rate.Tiers.Count == 0 || totalKwh <= 0) return 0;

        double cost = 0;
        double remaining = totalKwh;
        double previousLimit = 0;

        foreach (var tier in rate.Tiers)
        {
            double limit = tier.UpToKwh ?? double.PositiveInfinity;
            double width = limit - previousLimit;
            double consumed = Math.Min(remaining, width);

            cost += consumed * tier.PricePerKwh;
            remaining -= consumed;
            previousLimit = limit;

            if (remaining <= 0) break;
        }

        // 마지막 구간에 상한이 있는데도 사용량이 넘쳤다면, 그 단가를 그대로 이어 적용한다.
        if (remaining > 0)
            cost += remaining * rate.Tiers[^1].PricePerKwh;

        return cost;
    }

    private static double CalculateTou(TouRate rate, CycleUsage usage)
    {
        if (usage.KwhByPeriod is null || usage.KwhByPeriod.Count == 0)
            return usage.TotalKwh * rate.DefaultPricePerKwh;

        double cost = 0;

        foreach (var (periodName, kwh) in usage.KwhByPeriod)
        {
            var period = rate.Periods.FirstOrDefault(p =>
                p.Name.Equals(periodName, StringComparison.OrdinalIgnoreCase));

            cost += kwh * (period?.PricePerKwh ?? rate.DefaultPricePerKwh);
        }

        return cost;
    }

    /// <summary>지금 1kWh를 더 쓰면 붙는 단가. 세금은 반영하지 않은 순수 전력량 단가다.</summary>
    public static double MarginalPricePerKwh(TariffConfig config, double totalKwh, DateTimeOffset now)
        => config.Rate switch
        {
            FlatRate flat => flat.PricePerKwh,
            TieredRate tiered => TierAt(tiered, totalKwh).PricePerKwh,
            TouRate tou => ResolvePeriod(tou, now).PricePerKwh,
            _ => 0,
        };

    /// <summary>
    /// 지금 이 전력을 계속 쓸 때 시간당 붙는 요금. 세금까지 반영한 값이다.
    ///
    /// 누적 요금이 매초 얼마나 오르는지 보여 주는 데 쓴다 — 총액만 보면
    /// 숫자가 멈춰 있는 것처럼 보여서 전기를 쓰고 있다는 감각이 오지 않는다.
    /// </summary>
    public static double CostPerHour(TariffConfig config, double totalKwh, double watts, DateTimeOffset now)
    {
        double marginal = MarginalPricePerKwh(config, totalKwh, now);

        double taxMultiplier = 1.0;
        foreach (var tax in config.Taxes) taxMultiplier += tax.Rate;

        return marginal * (watts / 1000.0) * taxMultiplier;
    }

    /// <summary>누진 구간이 임박했는지 알려준다. 누진제가 아니거나 마지막 구간이면 null.</summary>
    public static TierWarning? GetTierWarning(TariffConfig config, double totalKwh)
    {
        if (config.Rate is not TieredRate tiered || tiered.Tiers.Count == 0)
            return null;

        double previousLimit = 0;

        for (int i = 0; i < tiered.Tiers.Count; i++)
        {
            var tier = tiered.Tiers[i];
            if (tier.UpToKwh is not double limit) return null;

            if (totalKwh < limit)
            {
                if (i + 1 >= tiered.Tiers.Count) return null;

                return new TierWarning(
                    KwhUntilNextTier: limit - totalKwh,
                    CurrentPricePerKwh: tier.PricePerKwh,
                    NextPricePerKwh: tiered.Tiers[i + 1].PricePerKwh);
            }

            previousLimit = limit;
        }

        _ = previousLimit;
        return null;
    }

    private static Tier TierAt(TieredRate rate, double totalKwh)
    {
        double previousLimit = 0;

        foreach (var tier in rate.Tiers)
        {
            double limit = tier.UpToKwh ?? double.PositiveInfinity;
            if (totalKwh < limit) return tier;
            previousLimit = limit;
        }

        _ = previousLimit;
        return rate.Tiers.Count > 0 ? rate.Tiers[^1] : new Tier(null, 0);
    }

    /// <summary>지금 시각이 어느 시간대 구간에 속하는지 판정한다.</summary>
    public static (string Name, double PricePerKwh) ResolvePeriod(TouRate rate, DateTimeOffset now)
    {
        foreach (var period in rate.Periods)
        {
            if (MatchesDay(period.Days, now.DayOfWeek) && MatchesTime(period.From, period.To, now.TimeOfDay))
                return (period.Name, period.PricePerKwh);
        }

        return (rate.DefaultPeriodName, rate.DefaultPricePerKwh);
    }

    /// <summary>"all", "mon-fri", "sat,sun", "mon,wed,fri" 형태를 받는다.</summary>
    internal static bool MatchesDay(string spec, DayOfWeek day)
    {
        if (string.IsNullOrWhiteSpace(spec)) return true;

        spec = spec.Trim().ToLowerInvariant();
        if (spec is "all" or "*") return true;

        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dash = part.IndexOf('-');

            if (dash < 0)
            {
                if (ParseDay(part) == day) return true;
                continue;
            }

            if (ParseDay(part[..dash]) is not DayOfWeek start) continue;
            if (ParseDay(part[(dash + 1)..]) is not DayOfWeek end) continue;

            // 요일 범위는 일요일을 넘어 감길 수 있다 (예: fri-mon).
            for (int d = (int)start; ; d = (d + 1) % 7)
            {
                if ((DayOfWeek)d == day) return true;
                if ((DayOfWeek)d == end) break;
            }
        }

        return false;
    }

    private static DayOfWeek? ParseDay(string token) => token.Trim() switch
    {
        "sun" or "sunday" => DayOfWeek.Sunday,
        "mon" or "monday" => DayOfWeek.Monday,
        "tue" or "tuesday" => DayOfWeek.Tuesday,
        "wed" or "wednesday" => DayOfWeek.Wednesday,
        "thu" or "thursday" => DayOfWeek.Thursday,
        "fri" or "friday" => DayOfWeek.Friday,
        "sat" or "saturday" => DayOfWeek.Saturday,
        _ => null,
    };

    internal static bool MatchesTime(string from, string to, TimeSpan now)
    {
        if (!TryParseTime(from, out var start) || !TryParseTime(to, out var end))
            return false;

        // 자정을 넘는 구간(예: 22:00~06:00)은 두 조각으로 나뉜다.
        return start <= end
            ? now >= start && now < end
            : now >= start || now < end;
    }

    private static bool TryParseTime(string value, out TimeSpan result)
        => TimeSpan.TryParseExact(value?.Trim() ?? string.Empty, @"hh\:mm", CultureInfo.InvariantCulture, out result);
}
