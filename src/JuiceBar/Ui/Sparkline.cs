using System.Windows;
using System.Windows.Media;

namespace JuiceBar.Ui;

/// <summary>
/// 최근 전력 추이를 보여 주는 작은 면적 그래프.
///
/// 축도 눈금도 없다 — 정확한 값을 읽는 용도가 아니라 "방금 뭔가 튀었나"를
/// 한눈에 알아보는 용도이기 때문이다.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(
            nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public static readonly DependencyProperty LineBrushProperty =
        DependencyProperty.Register(
            nameof(LineBrush), typeof(Brush), typeof(Sparkline),
            new FrameworkPropertyMetadata(Brushes.SkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        double width = ActualWidth, height = ActualHeight;

        if (values is null || values.Count < 2 || width <= 0 || height <= 0) return;

        double max = 0;
        foreach (double value in values) max = Math.Max(max, value);

        // 전부 0이면 그릴 것이 없다. 바닥에 붙은 직선은 오히려 오해를 부른다.
        if (max <= 0) return;

        // 위쪽에 여유를 두어 최고점이 잘려 보이지 않게 한다.
        double scale = height * 0.88 / max;
        double step = width / (values.Count - 1);

        var line = new PathFigure { StartPoint = new Point(0, height - (values[0] * scale)), IsFilled = false };
        var area = new PathFigure { StartPoint = new Point(0, height), IsFilled = true };
        area.Segments.Add(new LineSegment(line.StartPoint, isStroked: false));

        for (int i = 1; i < values.Count; i++)
        {
            var point = new Point(step * i, height - (values[i] * scale));
            line.Segments.Add(new LineSegment(point, isStroked: true));
            area.Segments.Add(new LineSegment(point, isStroked: false));
        }

        area.Segments.Add(new LineSegment(new Point(width, height), isStroked: false));
        area.IsClosed = true;

        var lineGeometry = new PathGeometry([line]);
        var areaGeometry = new PathGeometry([area]);
        lineGeometry.Freeze();
        areaGeometry.Freeze();

        var lineColor = LineBrush is SolidColorBrush solid ? solid.Color : Colors.SkyBlue;

        var fill = new LinearGradientBrush(
            Color.FromArgb(0x5A, lineColor.R, lineColor.G, lineColor.B),
            Color.FromArgb(0x00, lineColor.R, lineColor.G, lineColor.B),
            new Point(0, 0),
            new Point(0, 1));
        fill.Freeze();

        dc.DrawGeometry(fill, null, areaGeometry);
        dc.DrawGeometry(null, new Pen(LineBrush, 1.6) { LineJoin = PenLineJoin.Round }, lineGeometry);
    }
}
