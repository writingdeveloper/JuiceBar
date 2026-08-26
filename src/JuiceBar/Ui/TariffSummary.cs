using System.Text;
using JuiceBar.Core.Tariff;

namespace JuiceBar.Ui;

/// <summary>
/// 요금 설정을 사람이 읽을 수 있는 문장으로 푼다.
///
/// 설정 화면에서 입력란을 다 없앤 대신, 지금 무엇이 적용돼 있는지는
/// 한눈에 보여 줘야 한다. 마법사에서 "이렇게 인식했습니다" 를 보여 줄 때도 같은 글을 쓴다.
/// </summary>
public static class TariffSummary
{
    public static string Describe(TariffConfig tariff)
    {
        var text = new StringBuilder();

        text.Append($"{tariff.Currency} ({tariff.Symbol}) · 검침 시작 {tariff.BillingCycleStartDay}일");

        if (tariff.FixedChargePerMonth > 0)
            text.Append($" · 기본요금 {Money(tariff.FixedChargePerMonth, tariff)}");

        text.AppendLine();
        AppendRate(text, tariff);

        if (tariff.Taxes.Count > 0)
        {
            var names = tariff.Taxes.Select(t => $"{t.Name} {t.Rate:P1}");
            text.Append($"세금 — {string.Join(", ", names)}");
        }

        return text.ToString().TrimEnd();
    }

    private static void AppendRate(StringBuilder text, TariffConfig tariff)
    {
        switch (tariff.Rate)
        {
            case FlatRate flat:
                text.AppendLine($"단일 단가 — kWh당 {Money(flat.PricePerKwh, tariff)}");
                break;

            case TieredRate tiered:
                text.AppendLine($"{tiered.Tiers.Count}단계 누진");

                double previous = 0;
                foreach (var tier in tiered.Tiers)
                {
                    string range = tier.UpToKwh is double limit
                        ? $"{previous:N0}~{limit:N0} kWh"
                        : $"{previous:N0} kWh 초과";

                    text.AppendLine($"    {range} — kWh당 {Money(tier.PricePerKwh, tariff)}");
                    previous = tier.UpToKwh ?? previous;
                }
                break;

            case TouRate tou:
                text.AppendLine($"시간대별 — 구간 {tou.Periods.Count}개");

                foreach (var period in tou.Periods)
                {
                    text.AppendLine(
                        $"    {period.Name} ({period.Days} {period.From}~{period.To}) — kWh당 {Money(period.PricePerKwh, tariff)}");
                }

                text.AppendLine($"    그 외 시간 — kWh당 {Money(tou.DefaultPricePerKwh, tariff)}");
                break;

            default:
                text.AppendLine("요금이 설정되지 않았습니다.");
                break;
        }
    }

    private static string Money(double amount, TariffConfig tariff)
        => CurrencyFormatter.Format(amount, tariff.Currency, tariff.Symbol);
}
