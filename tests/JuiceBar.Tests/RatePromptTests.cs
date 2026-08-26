using JuiceBar.Core.Tariff;

namespace JuiceBar.Tests;

public class RatePromptTests
{
    [Fact]
    public void Prompt_mentions_the_region_the_user_typed()
    {
        string prompt = RatePrompt.Build("서울");

        Assert.StartsWith("서울의", prompt);
    }

    [Fact]
    public void Prompt_falls_back_to_a_neutral_phrase_when_no_region_is_given()
    {
        string prompt = RatePrompt.Build("   ");

        Assert.Contains("내가 사는 지역", prompt);
    }

    [Fact]
    public void Prompt_pins_the_currency_when_one_is_given()
    {
        string prompt = RatePrompt.Build("Berlin", "EUR");

        Assert.Contains("EUR 단위로 표기해줘", prompt);
    }

    [Fact]
    public void Prompt_lets_the_assistant_choose_when_no_currency_is_given()
    {
        string prompt = RatePrompt.Build("Berlin");

        Assert.Contains("실제로 쓰는 통화로", prompt);
        Assert.DoesNotContain("단위로 표기해줘", prompt);
    }

    [Fact]
    public void Prompt_documents_all_three_rate_shapes()
    {
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

    // ── 걸러내기 ──────────────────────────────────────────────

    [Fact]
    public void Empty_input_is_rejected()
    {
        Assert.False(RatePrompt.TryParse("   ").Success);
    }

    [Fact]
    public void Input_without_json_is_rejected_with_a_useful_message()
    {
        var result = RatePrompt.TryParse("죄송하지만 그 지역의 요금은 알 수 없습니다.");

        Assert.False(result.Success);
        Assert.Contains("JSON", result.Error);
    }

    [Fact]
    public void A_tax_rate_written_as_a_percentage_is_rejected()
    {
        // "10%" 를 0.1 이 아니라 10 으로 쓰면 요금이 열 배가 된다. 반드시 잡아야 한다.
        string response = """
            { "Currency": "KRW", "Symbol": "원", "BillingCycleStartDay": 1,
              "FixedChargePerMonth": 0, "Taxes": [{ "Name": "VAT", "Rate": 10 }],
              "Rate": { "kind": "flat", "PricePerKwh": 250 }, "MonthlyBudget": 0 }
            """;

        var result = RatePrompt.TryParse(response);

        Assert.False(result.Success);
        Assert.Contains("비율", result.Error);
    }

    [Fact]
    public void A_bounded_final_tier_is_rejected()
    {
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
        Assert.Contains("순서", result.Error);
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
