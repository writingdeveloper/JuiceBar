using System.Text;
using JuiceBar.Core.Tariff;
using JuiceBar.Core.Localization;

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

        text.Append(Loc.T("summary.header", tariff.Currency, tariff.Symbol, tariff.BillingCycleStartDay));

        if (tariff.FixedChargePerMonth > 0)
            text.Append(Loc.T("summary.fixedCharge", Money(tariff.FixedChargePerMonth, tariff)));

        text.AppendLine();
        AppendRate(text, tariff);

        if (tariff.Taxes.Count > 0)
        {
            var names = tariff.Taxes.Select(t => $"{t.Name} {t.Rate:P1}");
            text.Append(Loc.T("summary.taxes", string.Join(", ", names)));
        }

        return text.ToString().TrimEnd();
    }

    private static void AppendRate(StringBuilder text, TariffConfig tariff)
    {
        switch (tariff.Rate)
        {
            case FlatRate flat:
                text.AppendLine(Loc.T("summary.flat", Money(flat.PricePerKwh, tariff)));
                break;

            case TieredRate tiered:
                text.AppendLine(Loc.T("summary.tiered", tiered.Tiers.Count));

                double previous = 0;
                foreach (var tier in tiered.Tiers)
                {
                    string price = Money(tier.PricePerKwh, tariff);

                    text.AppendLine(tier.UpToKwh is double limit
                        ? Loc.T("summary.tierRange", previous.ToString("N0"), limit.ToString("N0"), price)
                        : Loc.T("summary.tierAbove", previous.ToString("N0"), price));
                    previous = tier.UpToKwh ?? previous;
                }
                break;

            case TouRate tou:
                text.AppendLine(Loc.T("summary.tou", tou.Periods.Count));

                foreach (var period in tou.Periods)
                {
                    text.AppendLine(Loc.T("summary.touPeriod",
                        period.Name, period.Days, period.From, period.To, Money(period.PricePerKwh, tariff)));
                }

                text.AppendLine(Loc.T("summary.touDefault", Money(tou.DefaultPricePerKwh, tariff)));
                break;

            default:
                text.AppendLine(Loc.T("summary.notSet"));
                break;
        }
    }

    private static string Money(double amount, TariffConfig tariff)
        => CurrencyFormatter.Format(amount, tariff.Currency, tariff.Symbol);
}
