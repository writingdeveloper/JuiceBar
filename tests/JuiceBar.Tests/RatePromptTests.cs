using JuiceBar.Core.Localization;
using JuiceBar.Core.Tariff;

namespace JuiceBar.Tests;

[Collection(LocalizationCollection.Name)]
public class RatePromptTests
{
    /// <summary>테스트가 도는 기계의 시스템 언어에 좌우되지 않게 못 박는다.</summary>
    private static void UseEnglish() => Loc.Use("en");

    [Fact]
    public void Prompt_mentions_the_region_the_user_typed()
    {
        UseEnglish();

        Assert.Contains("Seoul", RatePrompt.Build("Seoul"));
    }

    [Fact]
    public void Prompt_is_written_in_the_selected_language()
    {
        Loc.Use("ko");
        string korean = RatePrompt.Build("서울");

        Loc.Use("ja");
        string japanese = RatePrompt.Build("東京");

        UseEnglish();

        Assert.Contains("가정용 전기요금", korean);
        Assert.Contains("家庭用電気料金", japanese);

        // 스키마는 어느 언어에서도 그대로여야 한다. 기계가 읽는 부분이기 때문이다.
        Assert.Contains("\"BillingCycleStartDay\"", korean);
        Assert.Contains("\"BillingCycleStartDay\"", japanese);
    }

    [Fact]
    public void Prompt_falls_back_to_a_neutral_phrase_when_no_region_is_given()
    {
        UseEnglish();

        Assert.Contains("where I live", RatePrompt.Build("   "));
    }

    [Fact]
    public void Prompt_pins_the_currency_when_one_is_given()
    {
        UseEnglish();

        Assert.Contains("in EUR", RatePrompt.Build("Berlin", "EUR"));
    }

    [Fact]
    public void Prompt_lets_the_assistant_choose_when_no_currency_is_given()
    {
        UseEnglish();
        string prompt = RatePrompt.Build("Berlin");

        Assert.Contains("currency actually used there", prompt);
        Assert.DoesNotContain("Give the amounts in", prompt);
    }

    [Fact]
    public void Prompt_documents_all_three_rate_shapes()
    {
        UseEnglish();
        string prompt = RatePrompt.Build("Berlin");

        Assert.Contains("\"flat\"", prompt);
        Assert.Contains("\"tiered\"", prompt);
        Assert.Contains("\"tou\"", prompt);
    }

    // ── 잘라내기 ──────────────────────────────────────────────

    [Fact]
    public void Parsing_ignores_prose_around_the_json()
    {
        string response = """
            서울의 가정용 전기요금은 누진제로 운영됩니다. 아래가 요청하신 JSON 입니다.

            {
              "Currency": "KRW",
              "Symbol": "원",
              "BillingCycleStartDay": 1,
              "FixedChargePerMonth": 1600,
              "Taxes": [{ "Name": "부가가치세", "Rate": 0.1 }],
              "Rate": { "kind": "flat", "PricePerKwh": 250 },
              "MonthlyBudget": 0
            }

            2026년 기준입니다.
            """;

        var result = RatePrompt.TryParse(response);

        Assert.True(result.Success, result.Error);
        Assert.Equal("KRW", result.Tariff!.Currency);
        Assert.Equal(1600, result.Tariff.FixedChargePerMonth);
    }

    [Fact]
    public void Parsing_handles_a_markdown_code_fence()
    {
        string response = """
            ```json
            {
              "Currency": "USD",
              "Symbol": "$",
              "BillingCycleStartDay": 15,
              "FixedChargePerMonth": 10,
              "Taxes": [],
              "Rate": { "kind": "flat", "PricePerKwh": 0.17 },
              "MonthlyBudget": 0
            }
            ```
            """;

        var result = RatePrompt.TryParse(response);

        Assert.True(result.Success, result.Error);
        Assert.Equal(15, result.Tariff!.BillingCycleStartDay);
    }

    [Fact]
    public void Parsing_accepts_tiered_rates()
    {
        string response = """
            {
              "Currency": "KRW", "Symbol": "원",
              "BillingCycleStartDay": 1, "FixedChargePerMonth": 1600, "Taxes": [],
              "Rate": { "kind": "tiered", "Tiers": [
                  { "UpToKwh": 200,  "PricePerKwh": 120 },
                  { "UpToKwh": 400,  "PricePerKwh": 214 },
                  { "UpToKwh": null, "PricePerKwh": 307 } ] },
              "MonthlyBudget": 0
            }
            """;

        var result = RatePrompt.TryParse(response);

        Assert.True(result.Success, result.Error);
        var tiered = Assert.IsType<TieredRate>(result.Tariff!.Rate);
        Assert.Equal(3, tiered.Tiers.Count);
        Assert.Null(tiered.Tiers[^1].UpToKwh);
    }

    [Fact]
    public void Parsing_accepts_time_of_use_rates()
    {
        string response = """
            {
              "Currency": "USD", "Symbol": "$",
              "BillingCycleStartDay": 1, "FixedChargePerMonth": 0, "Taxes": [],
              "Rate": { "kind": "tou",
                "Periods": [ { "Name": "peak", "Days": "mon-fri",
                               "From": "16:00", "To": "21:00", "PricePerKwh": 0.42 } ],
                "DefaultPeriodName": "offpeak", "DefaultPricePerKwh": 0.14 },
              "MonthlyBudget": 0
            }
            """;

        var result = RatePrompt.TryParse(response);

        Assert.True(result.Success, result.Error);
        var tou = Assert.IsType<TouRate>(result.Tariff!.Rate);
        Assert.Single(tou.Periods);
        Assert.Equal(0.14, tou.DefaultPricePerKwh, precision: 6);
    }

    /// <summary>
    /// 실제로 AI 가 돌려준 답변. 미국 캘리포니아식 3단계 누진에 사용료세가 붙은 형태다.
    /// 사용자가 이 값을 붙여 넣었을 때 저장이 되지 않는다는 보고가 있어 회귀 테스트로 남긴다.
    /// </summary>
    [Fact]
    public void A_real_assistant_reply_with_tiers_and_tax_is_accepted()
    {
        string response = """
            {
              "Currency": "USD",
              "Symbol": "$",
              "BillingCycleStartDay": 1,
              "FixedChargePerMonth": 0,
              "Taxes": [
                { "Name": "Utility User Tax", "Rate": 0.1 }
              ],
              "Rate": {
                "kind": "tiered",
                "Tiers": [
                  { "UpToKwh": 350, "PricePerKwh": 0.22 },
                  { "UpToKwh": 1050, "PricePerKwh": 0.28 },
                  { "UpToKwh": null, "PricePerKwh": 0.35 }
                ]
              },
              "MonthlyBudget": 0
            }
            """;

        var result = RatePrompt.TryParse(response);

        Assert.True(result.Success, result.Error);

        var tariff = result.Tariff!;
        Assert.Equal("USD", tariff.Currency);
        Assert.Equal("$", tariff.Symbol);
        Assert.Single(tariff.Taxes);
        Assert.Equal(0.1, tariff.Taxes[0].Rate, precision: 6);

        var tiered = Assert.IsType<TieredRate>(tariff.Rate);
        Assert.Equal(3, tiered.Tiers.Count);
        Assert.Equal(350, tiered.Tiers[0].UpToKwh);
        Assert.Null(tiered.Tiers[^1].UpToKwh);

        // 400 kWh 면 350×0.22 + 50×0.28 = 91.0, 세금 10% 를 더해 100.1
        var cost = TariffCalculator.Calculate(tariff, new CycleUsage(400));
        Assert.Equal(91.0, cost.EnergyCharge, precision: 6);
        Assert.Equal(100.1, cost.Total, precision: 6);
    }

    // ── 걸러내기 ──────────────────────────────────────────────

    [Fact]
    public void Empty_input_is_rejected()
    {
        Assert.False(RatePrompt.TryParse("   ").Success);
    }

    [Fact]
    public void Input_without_json_is_rejected_with_a_useful_message()
    {
        UseEnglish();
        var result = RatePrompt.TryParse("죄송하지만 그 지역의 요금은 알 수 없습니다.");

        Assert.False(result.Success);
        Assert.Contains("JSON", result.Error);
    }

    [Fact]
    public void A_tax_rate_written_as_a_percentage_is_rejected()
    {
        UseEnglish();
        // "10%" 를 0.1 이 아니라 10 으로 쓰면 요금이 열 배가 된다. 반드시 잡아야 한다.
        string response = """
            { "Currency": "KRW", "Symbol": "원", "BillingCycleStartDay": 1,
              "FixedChargePerMonth": 0, "Taxes": [{ "Name": "VAT", "Rate": 10 }],
              "Rate": { "kind": "flat", "PricePerKwh": 250 }, "MonthlyBudget": 0 }
            """;

        var result = RatePrompt.TryParse(response);

        Assert.False(result.Success);
        Assert.Contains("fraction", result.Error);
    }

    [Fact]
    public void A_bounded_final_tier_is_rejected()
    {
        UseEnglish();
        string response = """
            { "Currency": "KRW", "Symbol": "원", "BillingCycleStartDay": 1,
              "FixedChargePerMonth": 0, "Taxes": [],
              "Rate": { "kind": "tiered", "Tiers": [
                  { "UpToKwh": 200, "PricePerKwh": 120 },
                  { "UpToKwh": 400, "PricePerKwh": 214 } ] },
              "MonthlyBudget": 0 }
            """;

        var result = RatePrompt.TryParse(response);

        Assert.False(result.Success);
        Assert.Contains("null", result.Error);
    }

    [Fact]
    public void Tiers_out_of_order_are_rejected()
    {
        UseEnglish();
        string response = """
            { "Currency": "KRW", "Symbol": "원", "BillingCycleStartDay": 1,
              "FixedChargePerMonth": 0, "Taxes": [],
              "Rate": { "kind": "tiered", "Tiers": [
                  { "UpToKwh": 400, "PricePerKwh": 120 },
                  { "UpToKwh": 200, "PricePerKwh": 214 },
                  { "UpToKwh": null, "PricePerKwh": 307 } ] },
              "MonthlyBudget": 0 }
            """;

        var result = RatePrompt.TryParse(response);

        Assert.False(result.Success);
        Assert.Contains("not increasing", result.Error);
    }

    [Fact]
    public void An_out_of_range_billing_day_is_rejected()
    {
        string response = """
            { "Currency": "KRW", "Symbol": "원", "BillingCycleStartDay": 45,
              "FixedChargePerMonth": 0, "Taxes": [],
              "Rate": { "kind": "flat", "PricePerKwh": 250 }, "MonthlyBudget": 0 }
            """;

        Assert.False(RatePrompt.TryParse(response).Success);
    }

    [Fact]
    public void A_zero_price_is_rejected()
    {
        string response = """
            { "Currency": "KRW", "Symbol": "원", "BillingCycleStartDay": 1,
              "FixedChargePerMonth": 0, "Taxes": [],
              "Rate": { "kind": "flat", "PricePerKwh": 0 }, "MonthlyBudget": 0 }
            """;

        Assert.False(RatePrompt.TryParse(response).Success);
    }
}
