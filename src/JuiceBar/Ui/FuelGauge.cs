using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace JuiceBar.Ui;

/// <summary>
/// 주유소 연료계를 본뜬 원호 게이지.
///
/// 눈금 호는 값에 따라 색이 변하는데, WPF에는 원뿔형 그라디언트가 없어
/// 호를 잘게 쪼개 각 조각에 램프 색을 칠하는 방식으로 흉내 낸다.
/// 바늘은 값이 바뀔 때마다 부드럽게 스윙한다 — 매초 값이 튀는 것보다
/// 눈에 편하고, 계기판다운 느낌을 준다.
/// </summary>
public sealed class FuelGauge : FrameworkElement
{
    // 화면 좌표는 y축이 아래로 향하므로 각도는 시계 방향으로 증가한다.
    // 150°(좌하) 에서 시작해 240° 를 돌면 390°(=30°, 우하) 에서 끝난다. 꼭대기는 270°.
    private const double StartAngle = 150;
    private const double SweepAngle = 240;

    private const double TrackThickness = 14;
    private const int RampSegments = 72;

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(FuelGauge),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0~1 채움 비율.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(
            nameof(TrackBrush), typeof(Brush), typeof(FuelGauge),
            new FrameworkPropertyMetadata(
                new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
                FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public static readonly DependencyProperty NeedleBrushProperty =
        DependencyProperty.Register(
            nameof(NeedleBrush), typeof(Brush), typeof(FuelGauge),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush NeedleBrush
    {
        get => (Brush)GetValue(NeedleBrushProperty);
        set => SetValue(NeedleBrushProperty, value);
    }

    public static readonly DependencyProperty LabelBrushProperty =
        DependencyProperty.Register(
            nameof(LabelBrush), typeof(Brush), typeof(FuelGauge),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    /// <summary>값을 애니메이션으로 옮긴다. 매초 갱신되는 값을 그대로 꽂으면 눈이 피곤하다.</summary>
    public void AnimateTo(double target)
    {
        target = Math.Clamp(target, 0, 1);

        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(650))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        BeginAnimation(ValueProperty, animation);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        // 호가 아래쪽으로 열려 있으므로 중심을 살짝 위로 올려야 시각적으로 균형이 맞는다.
        var center = new Point(width / 2, height * 0.53);

        // E · F 글자가 호 바깥에 붙으므로 그만큼 여백을 남겨 둔다.
        double radius = Math.Min(width / 2, height * 0.58) - (TrackThickness / 2) - 16;
        if (radius <= 0) return;

        DrawTrack(dc, center, radius);
        DrawTicks(dc, center, radius);
        DrawValueArc(dc, center, radius);
        DrawEndLabels(dc, center, radius);
        DrawNeedle(dc, center, radius);
    }

    private void DrawTrack(DrawingContext dc, Point center, double radius)
    {
        var pen = new Pen(TrackBrush, TrackThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, pen, BuildArc(center, radius, StartAngle, SweepAngle));
    }

    /// <summary>
    /// 값 구간을 잘게 쪼개 각 조각을 램프 색으로 칠한다.
    /// 조각마다 그리는 대신 하나의 그라디언트를 쓰면 호를 따라가지 않고 직선으로 흘러 어색하다.
    /// </summary>
    private void DrawValueArc(DrawingContext dc, Point center, double radius)
    {
        double fraction = Math.Clamp(Value, 0, 1);
        if (fraction <= 0.001) return;

        int segments = Math.Max(1, (int)Math.Ceiling(RampSegments * fraction));
        double segmentSweep = SweepAngle * fraction / segments;

        // 조각 사이가 갈라져 보이지 않게 서로 살짝 겹쳐 그린다.
        double overlap = segmentSweep * 0.35;

        for (int i = 0; i < segments; i++)
        {
            double from = StartAngle + (segmentSweep * i);
            double segmentPosition = (double)(i + 1) / segments * fraction;

            var color = GaugePalette.ToMediaColor(segmentPosition);
            var pen = new Pen(new SolidColorBrush(color), TrackThickness)
            {
                StartLineCap = i == 0 ? PenLineCap.Round : PenLineCap.Flat,
                EndLineCap = i == segments - 1 ? PenLineCap.Round : PenLineCap.Flat,
            };

            dc.DrawGeometry(null, pen, BuildArc(center, radius, from, segmentSweep + overlap));
        }
    }

    private void DrawTicks(DrawingContext dc, Point center, double radius)
    {
        var pen = new Pen(LabelBrush, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        double inner = radius - TrackThickness - 4;
        double outer = radius - TrackThickness - 11;

        // 0, 1/4, 1/2, 3/4, 1 다섯 지점. 자동차 연료계의 E · 1/4 · 1/2 · 3/4 · F 에 해당한다.
        for (int i = 0; i <= 4; i++)
        {
            double angle = StartAngle + (SweepAngle * i / 4.0);
            dc.DrawLine(pen, PointOnCircle(center, inner, angle), PointOnCircle(center, outer, angle));
        }
    }

    private void DrawEndLabels(DrawingContext dc, Point center, double radius)
    {
        // 눈금 호 바깥에 둔다. 안쪽에 두면 한가운데 큰 숫자와 부딪힌다.
        double labelRadius = radius + (TrackThickness / 2) + 9;

        DrawLabel("E", StartAngle);
        DrawLabel("F", StartAngle + SweepAngle);

        void DrawLabel(string text, double angle)
        {
            var formatted = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                11,
                LabelBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            var point = PointOnCircle(center, labelRadius, angle);
            dc.DrawText(formatted, new Point(point.X - (formatted.Width / 2), point.Y - (formatted.Height / 2)));
        }
    }

    private void DrawNeedle(DrawingContext dc, Point center, double radius)
    {
        double angle = StartAngle + (SweepAngle * Math.Clamp(Value, 0, 1));
        double length = radius - TrackThickness - 6;

        var tip = PointOnCircle(center, length, angle);

        // 바늘 뒤쪽으로 조금 나오게 해서 축이 있는 것처럼 보이게 한다.
        var tail = PointOnCircle(center, -12, angle);

        var pen = new Pen(NeedleBrush, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Triangle };
        dc.DrawLine(pen, tail, tip);

        dc.DrawEllipse(NeedleBrush, null, center, 5.5, 5.5);
        dc.DrawEllipse(TrackBrush, null, center, 2.5, 2.5);
    }

    private static PathGeometry BuildArc(Point center, double radius, double startAngle, double sweep)
    {
        sweep = Math.Min(sweep, 359.9);

        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, startAngle + sweep);

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(
            end,
            new Size(radius, radius),
            0,
            isLargeArc: sweep > 180,
            SweepDirection.Clockwise,
            isStroked: true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();

        return geometry;
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180.0;
        return new Point(
            center.X + (radius * Math.Cos(radians)),
            center.Y + (radius * Math.Sin(radians)));
    }
}
