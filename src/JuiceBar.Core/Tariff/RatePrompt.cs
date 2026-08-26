using System.Text;
using System.Text.Json;
using JuiceBar.Core.Localization;

namespace JuiceBar.Core.Tariff;

public sealed record TariffParseResult(bool Success, TariffConfig? Tariff, string? Error)
{
    public static TariffParseResult Succeeded(TariffConfig tariff) => new(true, tariff, null);
    public static TariffParseResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// 요금을 손으로 채우는 대신 AI 에게 물어보게 하는 경로.
///
/// 사용자가 자기 지역의 전기요금 구조(누진 구간, 기본요금, 세금)를 정확히 아는 경우는
/// 드물다. 그래서 그대로 복사해 붙여 넣을 프롬프트를 만들어 주고, 돌아온 JSON 을
/// 다시 붙여 넣으면 설정이 채워지도록 했다. 입력할 칸이 열 개가 넘는 화면을
/// 마주하는 것보다 훨씬 단순하다.
/// </summary>
public static class RatePrompt
{
    /// <summary>
    /// 스키마와 보기는 번역하지 않는다. 기계가 읽을 부분이라 어느 언어로 물어보든
    /// 똑같은 모양으로 돌아와야 하고, 번역하면 오히려 답이 흔들린다.
    /// </summary>
    private const string Schema = """
        {
          "Currency": "USD",
          "Symbol": "$",
          "BillingCycleStartDay": 1,
          "FixedChargePerMonth": 0,
          "Taxes": [{ "Name": "Sales tax", "Rate": 0.1 }],
          "Rate": { "kind": "flat", "PricePerKwh": 0.17 },
          "MonthlyBudget": 0
        }
        """;

    private const string FlatExample =
        """{ "kind": "flat", "PricePerKwh": 0.17 }""";

    private const string TieredExample = """
        { "kind": "tiered", "Tiers": [
            { "UpToKwh": 350,  "PricePerKwh": 0.22 },
            { "UpToKwh": 1050, "PricePerKwh": 0.28 },
            { "UpToKwh": null, "PricePerKwh": 0.35 } ] }
        """;

    private const string TouExample = """
        { "kind": "tou",
          "Periods": [
            { "Name": "peak", "Days": "mon-fri", "From": "16:00", "To": "21:00",
              "PricePerKwh": 0.42 } ],
          "DefaultPeriodName": "offpeak",
          "DefaultPricePerKwh": 0.14 }
        """;

    /// <summary>
    /// AI 에게 던질 프롬프트. 설명은 사용자의 언어로, 스키마는 그대로 둔다.
    /// </summary>
    /// <param name="region">사는 곳. 비우면 일반적인 표현으로 대체한다.</param>
    /// <param name="currency">
    /// 원하는 통화. 비워도 되지만, 지역명만으로는 AI 가 다른 통화를 고르는 일이 있다 —
    /// 해외 거주자가 현지 통화 대신 본국 통화를 원하는 경우도 있어서 직접 지정할 수 있게 열어 둔다.
    /// </param>
    public static string Build(string region, string currency = "")
    {
        string place = string.IsNullOrWhiteSpace(region)
            ? Loc.T("prompt.defaultRegion")
            : region.Trim();

        string currencyLine = string.IsNullOrWhiteSpace(currency)
            ? Loc.T("prompt.currencyAuto")
            : Loc.T("prompt.currencyPinned", currency.Trim());

        var text = new StringBuilder();

        text.AppendLine(Loc.T("prompt.intro", place, currencyLine));
        text.AppendLine();
        text.AppendLine(Loc.T("prompt.onlyJson"));
        text.AppendLine();
        text.AppendLine(Schema);
        text.AppendLine();
        text.AppendLine(Loc.T("prompt.rulesHeading"));
        text.AppendLine();
        text.AppendLine(Loc.T("prompt.rule.currency"));
        text.AppendLine(Loc.T("prompt.rule.billingDay"));
        text.AppendLine(Loc.T("prompt.rule.fixedCharge"));
        text.AppendLine(Loc.T("prompt.rule.taxes"));
        text.AppendLine(Loc.T("prompt.rule.rate"));
        text.AppendLine();
        text.AppendLine($"   {Loc.T("prompt.rate.flat")}");
        text.AppendLine(Indent(FlatExample));
        text.AppendLine();
        text.AppendLine($"   {Loc.T("prompt.rate.tiered")}");
        text.AppendLine(Indent(TieredExample));
        text.AppendLine($"   {Loc.T("prompt.rate.tieredNote")}");
        text.AppendLine();
        text.AppendLine($"   {Loc.T("prompt.rate.tou")}");
        text.AppendLine(Indent(TouExample));
        text.AppendLine($"   {Loc.T("prompt.rate.touNote")}");
        text.AppendLine();
        text.AppendLine(Loc.T("prompt.rule.budget"));
        text.AppendLine(Loc.T("prompt.rule.uncertain"));

        return text.ToString().TrimEnd();
    }

    private static string Indent(string block)
        => string.Join(
            Environment.NewLine,
            block.Split('\n').Select(line => "   " + line.TrimEnd('\r')));

    /// <summary>
    /// AI 답변에서 요금 설정을 뽑아낸다.
    ///
    /// 답변에는 대개 코드 펜스나 앞뒤 설명이 붙어 오므로 중괄호 사이만 잘라 쓴다.
    /// 사용자에게 "JSON 만 남기고 지우세요" 라고 요구하지 않으려는 것이다.
    /// </summary>
    public static TariffParseResult TryParse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return TariffParseResult.Failed(Loc.T("rate.error.empty"));

        int start = response.IndexOf('{');
        int end = response.LastIndexOf('}');

        if (start < 0 || end <= start)
            return TariffParseResult.Failed(Loc.T("rate.error.noJson"));

        string json = response[start..(end + 1)];

        TariffConfig? tariff;

        try
        {
            tariff = JsonSerializer.Deserialize<TariffConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            return TariffParseResult.Failed(Loc.T("rate.error.badJson", ex.Message));
        }

        if (tariff is null)
            return TariffParseResult.Failed(Loc.T("rate.error.emptyJson"));

        return Validate(tariff);
    }

    /// <summary>
    /// AI 가 만든 값이라 그대로 믿을 수 없다. 앱이 이상하게 동작할 값은 여기서 걸러 낸다.
    /// </summary>
    private static TariffParseResult Validate(TariffConfig tariff)
    {
        if (string.IsNullOrWhiteSpace(tariff.Currency))
            return TariffParseResult.Failed(Loc.T("rate.error.currency"));

        if (tariff.BillingCycleStartDay is < 1 or > 31)
            return TariffParseResult.Failed(Loc.T("rate.error.billingDay", tariff.BillingCycleStartDay));

        if (tariff.FixedChargePerMonth < 0)
            return TariffParseResult.Failed(Loc.T("rate.error.fixedCharge"));

        foreach (var tax in tariff.Taxes)
        {
            // 0.1 로 써야 할 10% 를 10 으로 쓰는 실수가 잦다. 그대로 두면 요금이 열 배가 된다.
            if (tax.Rate is < 0 or > 1)
                return TariffParseResult.Failed(
                    Loc.T("rate.error.taxRate", tax.Name, tax.Rate));
        }

        return tariff.Rate switch
        {
            FlatRate flat when flat.PricePerKwh <= 0
                => TariffParseResult.Failed(Loc.T("rate.error.price")),

            TieredRate tiered => ValidateTiers(tariff, tiered),

            TouRate tou when tou.DefaultPricePerKwh <= 0 && tou.Periods.Count == 0
                => TariffParseResult.Failed(Loc.T("rate.error.tou")),

            _ => TariffParseResult.Succeeded(tariff),
        };
    }

    private static TariffParseResult ValidateTiers(TariffConfig tariff, TieredRate tiered)
    {
        if (tiered.Tiers.Count == 0)
            return TariffParseResult.Failed(Loc.T("rate.error.tiersEmpty"));

        double previous = 0;

        for (int i = 0; i < tiered.Tiers.Count; i++)
        {
            var tier = tiered.Tiers[i];

            if (tier.PricePerKwh <= 0)
                return TariffParseResult.Failed(Loc.T("rate.error.tierPrice", i + 1));

            bool isLast = i == tiered.Tiers.Count - 1;

            if (isLast)
            {
                // 마지막 구간에 상한이 있으면 그 위 사용량의 요금이 정의되지 않는다.
                if (tier.UpToKwh is not null)
                    return TariffParseResult.Failed(Loc.T("rate.error.lastTierBounded"));

                continue;
            }

            if (tier.UpToKwh is not double limit)
                return TariffParseResult.Failed(Loc.T("rate.error.tierMissing", i + 1));

            if (limit <= previous)
                return TariffParseResult.Failed(
                    Loc.T("rate.error.tierOrder", limit, previous));

            previous = limit;
        }

        return TariffParseResult.Succeeded(tariff);
    }
}
