namespace JuiceBar.Ui;

/// <summary>
/// 게이지 눈금 색. 트레이 아이콘(GDI+)과 팝업(WPF)이 같은 램프를 써야
/// 두 곳의 색이 어긋나 보이지 않는다.
/// </summary>
public static class GaugePalette
{
    /// <summary>(멈춤 지점, R, G, B). 사이 값은 선형 보간한다.</summary>
    private static readonly (double Stop, byte R, byte G, byte B)[] Ramp =
    [
        (0.00, 0x2E, 0xD5, 0x73),   // 여유 — 초록
        (0.55, 0x6B, 0xD9, 0x3C),   // 초록에서 노랑으로 넘어가는 길목
        (0.72, 0xF5, 0xC2, 0x2C),   // 주의 — 노랑
        (0.88, 0xF5, 0x8A, 0x2C),   // 경고 — 주황
        (1.00, 0xF0, 0x4B, 0x4B),   // 초과 임박 — 빨강
    ];

    public static (byte R, byte G, byte B) At(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);

        for (int i = 1; i < Ramp.Length; i++)
        {
            var upper = Ramp[i];
            if (fraction > upper.Stop) continue;

            var lower = Ramp[i - 1];
            double span = upper.Stop - lower.Stop;
            double t = span <= 0 ? 0 : (fraction - lower.Stop) / span;

            return (
                Lerp(lower.R, upper.R, t),
                Lerp(lower.G, upper.G, t),
                Lerp(lower.B, upper.B, t));
        }

        var last = Ramp[^1];
        return (last.R, last.G, last.B);
    }

    private static byte Lerp(byte from, byte to, double t)
        => (byte)Math.Round(from + ((to - from) * Math.Clamp(t, 0, 1)));

    public static System.Windows.Media.Color ToMediaColor(double fraction)
    {
        var (r, g, b) = At(fraction);
        return System.Windows.Media.Color.FromRgb(r, g, b);
    }

    public static System.Drawing.Color ToDrawingColor(double fraction)
    {
        var (r, g, b) = At(fraction);
        return System.Drawing.Color.FromArgb(r, g, b);
    }
}
