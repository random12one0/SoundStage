using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Soundstage.App.Controls;

/// <summary>
/// Draws the frequency response of the current EQ: log-frequency x-axis (20 Hz–20 kHz),
/// ±15 dB y-axis, quiet gridlines, a filled accent curve. Points arrive in data
/// coordinates (X = log10 of frequency, Y = dB) from the viewmodel, which reuses the same
/// biquad math the clipping analyzer runs on — the plot IS what safety sees.
/// </summary>
public sealed class EqCurveControl : FrameworkElement
{
    private const double MinDb = -15;
    private const double MaxDb = 15;
    private static readonly double LogMin = Math.Log10(20);
    private static readonly double LogMax = Math.Log10(20000);

    private static readonly double[] GridFrequencies = [50, 100, 200, 500, 1000, 2000, 5000, 10000];
    private static readonly double[] LabeledFrequencies = [100, 1000, 10000];

    public static readonly DependencyProperty ResponseProperty = DependencyProperty.Register(
        nameof(Response), typeof(IReadOnlyList<Point>), typeof(EqCurveControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurveBrushProperty = DependencyProperty.Register(
        nameof(CurveBrush), typeof(Brush), typeof(EqCurveControl),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(EqCurveControl),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(EqCurveControl),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<Point>? Response
    {
        get => (IReadOnlyList<Point>?)GetValue(ResponseProperty);
        set => SetValue(ResponseProperty, value);
    }

    public Brush CurveBrush
    {
        get => (Brush)GetValue(CurveBrushProperty);
        set => SetValue(CurveBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 10 || height < 10)
        {
            return;
        }

        // Hit-test/clip background.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var gridPen = new Pen(GridBrush, 1);
        gridPen.Freeze();

        // Vertical gridlines at round frequencies.
        foreach (var f in GridFrequencies)
        {
            var x = XFor(Math.Log10(f), width);
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, height));
        }

        // Horizontal gridlines every 5 dB; 0 dB slightly stronger.
        for (var db = MinDb; db <= MaxDb; db += 5)
        {
            var y = YFor(db, height);
            var pen = db == 0 ? new Pen(LabelBrush, 1) : gridPen;
            dc.DrawLine(pen, new Point(0, y), new Point(width, y));
        }

        DrawLabels(dc, width, height);

        var response = Response;
        if (response is not { Count: > 1 })
        {
            return;
        }

        // Build curve + filled area.
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(ToScreen(response[0], width, height), isFilled: false, isClosed: false);
            for (var i = 1; i < response.Count; i++)
            {
                ctx.LineTo(ToScreen(response[i], width, height), true, true);
            }
        }

        geometry.Freeze();

        var fill = new StreamGeometry();
        using (var ctx = fill.Open())
        {
            var zeroY = YFor(0, height);
            ctx.BeginFigure(new Point(ToScreen(response[0], width, height).X, zeroY), isFilled: true, isClosed: true);
            foreach (var p in response)
            {
                ctx.LineTo(ToScreen(p, width, height), false, false);
            }

            ctx.LineTo(new Point(ToScreen(response[^1], width, height).X, zeroY), false, false);
        }

        fill.Freeze();

        var fillBrush = CurveBrush.Clone();
        fillBrush.Opacity = 0.12;
        fillBrush.Freeze();
        dc.DrawGeometry(fillBrush, null, fill);

        var curvePen = new Pen(CurveBrush, 2) { LineJoin = PenLineJoin.Round };
        curvePen.Freeze();
        dc.DrawGeometry(null, curvePen, geometry);
    }

    private void DrawLabels(DrawingContext dc, double width, double height)
    {
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        foreach (var f in LabeledFrequencies)
        {
            var text = f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}";
            var formatted = new FormattedText(
                text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, LabelBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(formatted, new Point(XFor(Math.Log10(f), width) + 3, height - formatted.Height - 2));
        }

        foreach (var db in new[] { 10.0, -10.0 })
        {
            var formatted = new FormattedText(
                $"{db:+0;-0}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, LabelBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(formatted, new Point(4, YFor(db, height) - formatted.Height - 1));
        }
    }

    private static double XFor(double logF, double width) => (logF - LogMin) / (LogMax - LogMin) * width;

    private static double YFor(double db, double height) => (MaxDb - Math.Clamp(db, MinDb, MaxDb)) / (MaxDb - MinDb) * height;

    private static Point ToScreen(Point dataPoint, double width, double height) =>
        new(XFor(dataPoint.X, width), YFor(dataPoint.Y, height));
}
