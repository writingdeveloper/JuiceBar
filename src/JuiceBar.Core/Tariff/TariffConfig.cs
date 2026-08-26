using System.Text.Json.Serialization;

namespace JuiceBar.Core.Tariff;

/// <summary>
/// 요금제 정의. 특정 국가에 묶이지 않는다.
///
/// 사용자는 자기 지역 요금을 알아내(고지서를 보거나 LLM에 물어보거나) 이 필드를 채운다.
/// 정확한 청구서 재현이 목표가 아니라, 비용 감각을 주는 것이 목표다.
/// </summary>
public sealed record TariffConfig
{
    /// <summary>ISO 4217 통화 코드. 숫자 서식에 쓴다.</summary>
    public string Currency { get; init; } = "KRW";

    /// <summary>UI에 붙일 통화 기호.</summary>
    public string Symbol { get; init; } = "₩";

    /// <summary>청구 주기가 시작되는 날. 대부분 1일이지만 검침일 기준인 곳도 있다.</summary>
    public int BillingCycleStartDay { get; init; } = 1;

    /// <summary>사용량과 무관하게 매달 붙는 기본요금.</summary>
    public double FixedChargePerMonth { get; init; }

    /// <summary>소계에 곱해지는 세금·부담금. 복리로 겹쳐 매기지 않는다.</summary>
    public IReadOnlyList<TaxItem> Taxes { get; init; } = [];

    public RateSpec Rate { get; init; } = new FlatRate { PricePerKwh = 250 };

    /// <summary>이 주기에 쓰기로 한 예산. 게이지의 만재 기준이 된다. 0이면 미설정.</summary>
    public double MonthlyBudget { get; init; }
}

public sealed record TaxItem(string Name, double Rate);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(FlatRate), "flat")]
[JsonDerivedType(typeof(TieredRate), "tiered")]
[JsonDerivedType(typeof(TouRate), "tou")]
public abstract record RateSpec;

/// <summary>단일 단가. 기본값이자 대부분의 사용자에게 충분한 형태.</summary>
public sealed record FlatRate : RateSpec
{
    public double PricePerKwh { get; init; }
}

/// <summary>
/// 구간 누진. 청구 주기 누적 사용량이 구간을 넘을 때마다 단가가 올라간다.
/// 한국·일본·미국 일부 주에서 쓰인다.
/// </summary>
public sealed record TieredRate : RateSpec
{
    public IReadOnlyList<Tier> Tiers { get; init; } = [];
}

/// <summary><see cref="UpToKwh"/>가 null이면 상한 없는 마지막 구간이다.</summary>
public sealed record Tier(double? UpToKwh, double PricePerKwh);

/// <summary>
/// 시간대별 요금. 유럽·북미·호주에 흔하다.
/// 어느 구간에도 안 걸리는 시간은 <see cref="DefaultPeriodName"/>으로 떨어진다.
/// </summary>
public sealed record TouRate : RateSpec
{
    public IReadOnlyList<TouPeriod> Periods { get; init; } = [];

    public string DefaultPeriodName { get; init; } = "offpeak";

    public double DefaultPricePerKwh { get; init; }
}

/// <summary>
/// <paramref name="Days"/>는 "mon-fri", "sat,sun", "all" 형태.
/// <paramref name="From"/>/<paramref name="To"/>는 "HH:mm". From &gt; To 이면 자정을 넘는 구간이다.
/// </summary>
public sealed record TouPeriod(
    string Name,
    string Days,
    string From,
    string To,
    double PricePerKwh);
