using System.Text.Json;

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
    /// AI 에게 던질 프롬프트. 스키마를 예시와 함께 못 박아 두어야
    /// 그대로 붙여 넣을 수 있는 형태로 답이 온다.
    /// </summary>
    /// <param name="region">사는 곳. 비우면 AI 가 알아서 묻거나 일반적인 값을 쓴다.</param>
    /// <param name="currency">
    /// 원하는 통화. 비워도 되지만, 지역명만으로는 AI 가 다른 통화를 고르는 일이 있다 —
    /// 해외 거주자가 현지 통화 대신 본국 통화를 원하는 경우도 있어서 직접 지정할 수 있게 열어 둔다.
    /// </param>
    public static string Build(string region, string currency = "")
    {
        string place = string.IsNullOrWhiteSpace(region) ? "내가 사는 지역" : region.Trim();

        string currencyLine = string.IsNullOrWhiteSpace(currency)
            ? "그 지역에서 실제로 쓰는 통화로 표기해줘."
            : $"금액은 {currency.Trim()} 단위로 표기해줘.";

        return $$"""
            {{place}}의 가정용 전기요금을 알려줘. {{currencyLine}}

            설명은 빼고 아래 형식의 JSON 만 출력해줘.

            {
              "Currency": "KRW",
              "Symbol": "원",
              "BillingCycleStartDay": 1,
              "FixedChargePerMonth": 0,
              "Taxes": [{ "Name": "부가가치세", "Rate": 0.1 }],
              "Rate": { "kind": "flat", "PricePerKwh": 250 },
              "MonthlyBudget": 0
            }

            규칙:

            1. Currency 는 ISO 4217 코드, Symbol 은 그 지역에서 실제로 쓰는 통화 기호.
            2. BillingCycleStartDay 는 요금이 새로 시작되는 날(보통 1).
            3. FixedChargePerMonth 는 사용량과 무관하게 매달 붙는 기본요금. 없으면 0.
            4. Taxes 의 Rate 는 비율로. 10% 는 0.1. 세금이 없거나 단가에 이미 포함돼
               있으면 빈 배열로 두고, 그 사실을 JSON 밖에 한 줄로 덧붙여줘.
            5. Rate 는 그 지역 요금제에 맞는 형태 하나를 골라서:

               단일 단가:
               { "kind": "flat", "PricePerKwh": 250 }

               구간 누진 (쓸수록 단가가 오르는 방식):
               { "kind": "tiered", "Tiers": [
                   { "UpToKwh": 200,  "PricePerKwh": 120 },
                   { "UpToKwh": 400,  "PricePerKwh": 214 },
                   { "UpToKwh": null, "PricePerKwh": 307 } ] }
               마지막 구간의 UpToKwh 는 반드시 null.

               시간대별 (시간에 따라 단가가 다른 방식):
               { "kind": "tou",
                 "Periods": [
                   { "Name": "peak", "Days": "mon-fri", "From": "16:00", "To": "21:00",
                     "PricePerKwh": 0.42 } ],
                 "DefaultPeriodName": "offpeak",
                 "DefaultPricePerKwh": 0.14 }
               Days 는 all / mon-fri / sat,sun 형태, 시각은 24시간제 HH:MM.

            6. MonthlyBudget 은 0 으로 둬. 내가 직접 정할 거야.
            7. 값이 확실하지 않으면 가장 일반적인 가정용 요금제를 기준으로 하고,
               언제 기준 요금인지 JSON 밖에 한 줄로 적어줘.
            """;
    }

    /// <summary>
    /// AI 답변에서 요금 설정을 뽑아낸다.
    ///
    /// 답변에는 대개 코드 펜스나 앞뒤 설명이 붙어 오므로 중괄호 사이만 잘라 쓴다.
    /// 사용자에게 "JSON 만 남기고 지우세요" 라고 요구하지 않으려는 것이다.
    /// </summary>
    public static TariffParseResult TryParse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return TariffParseResult.Failed("붙여 넣은 내용이 비어 있습니다.");

        int start = response.IndexOf('{');
        int end = response.LastIndexOf('}');

        if (start < 0 || end <= start)
            return TariffParseResult.Failed("JSON 을 찾지 못했습니다. AI 답변 전체를 그대로 붙여 넣어 보세요.");

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
            return TariffParseResult.Failed($"JSON 형식이 올바르지 않습니다: {ex.Message}");
        }

        if (tariff is null)
            return TariffParseResult.Failed("JSON 을 읽었지만 내용이 비어 있습니다.");

        return Validate(tariff);
    }

    /// <summary>
    /// AI 가 만든 값이라 그대로 믿을 수 없다. 앱이 이상하게 동작할 값은 여기서 걸러 낸다.
    /// </summary>
    private static TariffParseResult Validate(TariffConfig tariff)
    {
        if (string.IsNullOrWhiteSpace(tariff.Currency))
            return TariffParseResult.Failed("Currency 가 비어 있습니다.");

        if (tariff.BillingCycleStartDay is < 1 or > 31)
            return TariffParseResult.Failed($"BillingCycleStartDay 가 {tariff.BillingCycleStartDay} 입니다. 1~31 이어야 합니다.");

        if (tariff.FixedChargePerMonth < 0)
            return TariffParseResult.Failed("FixedChargePerMonth 가 음수입니다.");

        foreach (var tax in tariff.Taxes)
        {
            // 0.1 로 써야 할 10% 를 10 으로 쓰는 실수가 잦다. 그대로 두면 요금이 열 배가 된다.
            if (tax.Rate is < 0 or > 1)
                return TariffParseResult.Failed(
                    $"세금 '{tax.Name}' 의 Rate 가 {tax.Rate} 입니다. 비율이어야 합니다 (10% = 0.1).");
        }

        return tariff.Rate switch
        {
            FlatRate flat when flat.PricePerKwh <= 0
                => TariffParseResult.Failed("PricePerKwh 가 0 이하입니다."),

            TieredRate tiered => ValidateTiers(tariff, tiered),

            TouRate tou when tou.DefaultPricePerKwh <= 0 && tou.Periods.Count == 0
                => TariffParseResult.Failed("시간대별 요금에 구간도 기본 단가도 없습니다."),

            _ => TariffParseResult.Succeeded(tariff),
        };
    }

    private static TariffParseResult ValidateTiers(TariffConfig tariff, TieredRate tiered)
    {
        if (tiered.Tiers.Count == 0)
            return TariffParseResult.Failed("누진 구간이 비어 있습니다.");

        double previous = 0;

        for (int i = 0; i < tiered.Tiers.Count; i++)
        {
            var tier = tiered.Tiers[i];

            if (tier.PricePerKwh <= 0)
                return TariffParseResult.Failed($"{i + 1}번째 구간의 단가가 0 이하입니다.");

            bool isLast = i == tiered.Tiers.Count - 1;

            if (isLast)
            {
                // 마지막 구간에 상한이 있으면 그 위 사용량의 요금이 정의되지 않는다.
                if (tier.UpToKwh is not null)
                    return TariffParseResult.Failed("마지막 구간의 UpToKwh 는 null 이어야 합니다.");

                continue;
            }

            if (tier.UpToKwh is not double limit)
                return TariffParseResult.Failed($"{i + 1}번째 구간에 UpToKwh 가 없습니다.");

            if (limit <= previous)
                return TariffParseResult.Failed(
                    $"구간 상한이 커지는 순서가 아닙니다 ({limit} ≤ {previous}).");

            previous = limit;
        }

        return TariffParseResult.Succeeded(tariff);
    }
}
