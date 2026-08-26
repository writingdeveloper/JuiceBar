using System.Globalization;

namespace JuiceBar.Ui;

/// <summary>
/// 통화 표기. 나라마다 소수점 자릿수가 달라서 하나로 고정할 수 없다 —
/// 원·엔은 소수점이 없고 달러·유로는 두 자리다.
/// </summary>
public static class CurrencyFormatter
{
    /// <summary>ISO 4217 에서 소수 단위를 쓰지 않는 통화들.</summary>
    private static readonly HashSet<string> ZeroDecimal = new(StringComparer.OrdinalIgnoreCase)
    {
        "BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW",
        "PYG", "RWF", "UGX", "UYI", "VND", "VUV", "XAF", "XOF", "XPF",
    };

    public static int DecimalsFor(string currency)
        => ZeroDecimal.Contains(currency) ? 0 : 2;

    public static string Format(double amount, string currency, string symbol)
    {
        int decimals = DecimalsFor(currency);
        string number = amount.ToString($"N{decimals}", CultureInfo.CurrentCulture);

        // 원·엔은 숫자 뒤에, 달러·유로는 앞에 붙이는 것이 관례다.
        return decimals == 0 ? $"{number}{symbol}" : $"{symbol}{number}";
    }

    /// <summary>
    /// 계량기처럼 흘러가는 금액 표기를 위해 숫자를 네 조각으로 쪼갠다.
    ///
    /// 통화의 정상 자릿수까지가 <c>Main</c>, 그 아래로 더 붙인 자릿수가 <c>Fraction</c> 이다.
    /// UI 는 Main 을 크게, Fraction 을 작고 흐리게 그린다 — 그래야 숫자가 계속 움직이면서도
    /// 실제 금액이 얼마인지 읽을 수 있다.
    ///
    /// 원화로 342W 를 쓰면 1초에 0.024원씩 오른다. 소수점 아래를 네 자리까지
    /// 보여 줘야 눈에 보이는 속도로 움직인다.
    /// </summary>
    public static (string Prefix, string Main, string Fraction, string Suffix) SplitTicking(
        double amount, string currency, string symbol, int extraDigits = 4)
    {
        int decimals = DecimalsFor(currency);
        var culture = CultureInfo.CurrentCulture;

        string full = amount.ToString($"N{decimals + extraDigits}", culture);
        string separator = culture.NumberFormat.NumberDecimalSeparator;

        int position = full.LastIndexOf(separator, StringComparison.Ordinal);

        // 소수점이 없을 리 없지만, 없다면 통째로 Main 으로 둔다.
        if (position < 0) return (string.Empty, full, string.Empty, symbol);

        int splitAt = decimals == 0 ? position : position + separator.Length + decimals;
        splitAt = Math.Min(splitAt, full.Length);

        string main = full[..splitAt];
        string fraction = full[splitAt..];

        return decimals == 0
            ? (string.Empty, main, fraction, symbol)
            : (symbol, main, fraction, string.Empty);
    }

    /// <summary>kWh 는 소수 한 자리면 충분하다. 그 이하는 표시해 봐야 노이즈다.</summary>
    public static string FormatKwh(double kwh)
        => kwh < 10
            ? $"{kwh.ToString("N2", CultureInfo.CurrentCulture)} kWh"
            : $"{kwh.ToString("N1", CultureInfo.CurrentCulture)} kWh";

    public static string FormatWatts(double watts)
        => watts.ToString("N0", CultureInfo.CurrentCulture);
}
