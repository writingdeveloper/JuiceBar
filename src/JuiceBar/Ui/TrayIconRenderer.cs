using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

// 이 파일만 GDI+ 로 그린다. WPF 쪽에 같은 이름의 타입이 있어 하나씩 지정해 준다.
using Bitmap = System.Drawing.Bitmap;
using Color = System.Drawing.Color;
using Graphics = System.Drawing.Graphics;
using Icon = System.Drawing.Icon;
using Pen = System.Drawing.Pen;
using Rectangle = System.Drawing.Rectangle;
using RectangleF = System.Drawing.RectangleF;
using SolidBrush = System.Drawing.SolidBrush;

namespace JuiceBar.Ui;

/// <summary>
/// 트레이 아이콘을 매초 다시 그린다.
///
/// 16px 안에서 바늘 계기판은 읽히지 않으므로, 아래에서 위로 차오르는
/// 연료 탱크 모양으로 단순화했다. 색은 팝업 게이지와 같은 램프를 쓴다.
///
/// 실제 크기의 4배로 그린 뒤 줄이는데, GDI+ 의 안티앨리어싱만으로는
/// 이 크기에서 테두리가 지저분해지기 때문이다.
/// </summary>
public sealed class TrayIconRenderer : IDisposable
{
    private const int Supersample = 4;

    private Icon? _current;
    private bool _disposed;

    /// <summary>
    /// 아이콘을 새로 만들고 직전 것을 해제한다.
    ///
    /// <see cref="Bitmap.GetHicon"/> 이 넘겨주는 핸들은 GC가 회수하지 않는다.
    /// 매초 갱신하는 앱에서 이걸 놓치면 하루 만에 GDI 핸들 상한(10,000)에 부딪혀
    /// 화면 전체가 그리기를 멈춘다. 반드시 DestroyIcon 으로 직접 해제해야 한다.
    /// </summary>
    public Icon Render(double fraction, int pixelSize, ThemeVariant taskbarTheme)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        fraction = Math.Clamp(fraction, 0, 1);
        pixelSize = Math.Clamp(pixelSize, 16, 64);

        using var large = DrawTank(fraction, pixelSize * Supersample, taskbarTheme);
        using var scaled = Downscale(large, pixelSize);

        var icon = FromBitmap(scaled);

        ReplaceCurrent(icon);
        return icon;
    }

    private static Bitmap DrawTank(double fraction, int size, ThemeVariant taskbarTheme)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // 작업표시줄이 밝으면 어두운 테두리라야 보인다. 그 반대도 마찬가지다.
        Color outline = taskbarTheme == ThemeVariant.Light
            ? Color.FromArgb(0xD0, 0x20, 0x20, 0x24)
            : Color.FromArgb(0xD8, 0xEC, 0xEC, 0xF0);

        float unit = size / 16f;
        float stroke = 1.35f * unit;
        float inset = stroke;

        // 탱크 본체 — 위쪽 주입구를 위해 살짝 내려서 그린다.
        var body = new RectangleF(
            inset + (unit * 2f),
            inset + (unit * 2.2f),
            size - (2 * inset) - (unit * 4f),
            size - (2 * inset) - (unit * 2.6f));

        float radius = unit * 2.6f;

        using var bodyPath = RoundedRect(body, radius);

        // 채움은 아래에서 위로. 클리핑으로 잘라 내면 둥근 모서리를 자연스럽게 따라간다.
        if (fraction > 0.001)
        {
            var (r, gg, b) = GaugePalette.At(fraction);
            var fill = Color.FromArgb(0xFF, r, gg, b);

            float fillHeight = body.Height * (float)fraction;
            var fillRect = new RectangleF(body.X, body.Bottom - fillHeight, body.Width, fillHeight);

            var previousClip = g.Clip;
            g.SetClip(bodyPath);

            using (var brush = new SolidBrush(fill))
                g.FillRectangle(brush, fillRect);

            // 수면에 밝은 선을 얹어 액체처럼 보이게 한다.
            if (fraction < 0.985)
            {
                using var surface = new Pen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), unit * 0.7f);
                g.DrawLine(surface, fillRect.Left, fillRect.Top, fillRect.Right, fillRect.Top);
            }

            g.Clip = previousClip;
        }

        using (var pen = new Pen(outline, stroke) { LineJoin = LineJoin.Round })
            g.DrawPath(pen, bodyPath);

        // 주입구 노즐 — 이 형태가 있어야 배터리가 아니라 연료 탱크로 읽힌다.
        var spout = new RectangleF(
            body.X + (body.Width * 0.28f),
            inset * 0.2f,
            body.Width * 0.44f,
            unit * 2.6f);

        using (var spoutPath = RoundedRect(spout, unit * 1.1f))
        using (var pen = new Pen(outline, stroke) { LineJoin = LineJoin.Round })
            g.DrawPath(pen, spoutPath);

        return bitmap;
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();

        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new RectangleF(bounds.X, bounds.Y, diameter, diameter);

        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();

        return path;
    }

    private static Bitmap Downscale(Bitmap source, int size)
    {
        var result = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(result);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, size, size));

        return result;
    }

    private static Icon FromBitmap(Bitmap bitmap)
    {
        nint handle = bitmap.GetHicon();

        try
        {
            // Icon.FromHandle 은 핸들을 빌려 쓸 뿐이라, 원본이 사라지면 아이콘도 깨진다.
            // 복제해서 관리 객체로 만들어 두고 원본 핸들은 바로 반납한다.
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private void ReplaceCurrent(Icon icon)
    {
        var previous = _current;
        _current = icon;
        previous?.Dispose();
    }

    /// <summary>DPI에 맞는 트레이 아이콘 크기.</summary>
    public static int RecommendedSize()
    {
        int size = GetSystemMetrics(SM_CXSMICON);
        return size >= 16 ? size : 16;
    }

    private const int SM_CXSMICON = 49;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _current?.Dispose();
        _current = null;
    }
}
