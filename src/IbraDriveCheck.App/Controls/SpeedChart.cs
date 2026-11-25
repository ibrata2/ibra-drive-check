using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace IbraDriveCheck.App.Controls;

/// <summary>رسم خطي بسيط لعينات السرعة (قراءة وكتابة) بدون مكتبات خارجية.</summary>
public sealed class SpeedChart : FrameworkElement
{
    public static readonly DependencyProperty ReadSamplesProperty = DependencyProperty.Register(
        nameof(ReadSamples), typeof(IReadOnlyList<double>), typeof(SpeedChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WriteSamplesProperty = DependencyProperty.Register(
        nameof(WriteSamples), typeof(IReadOnlyList<double>), typeof(SpeedChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(SpeedChart), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AxisBrushProperty = DependencyProperty.Register(
        nameof(AxisBrush), typeof(Brush), typeof(SpeedChart), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? ReadSamples { get => (IReadOnlyList<double>?)GetValue(ReadSamplesProperty); set => SetValue(ReadSamplesProperty, value); }
    public IReadOnlyList<double>? WriteSamples { get => (IReadOnlyList<double>?)GetValue(WriteSamplesProperty); set => SetValue(WriteSamplesProperty, value); }
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public Brush AxisBrush { get => (Brush)GetValue(AxisBrushProperty); set => SetValue(AxisBrushProperty, value); }

    private static readonly Brush ReadBrush = Make(0x3B, 0x82, 0xF6);
    private static readonly Brush WriteBrush = Make(0xE6, 0x00, 0x12);

    private static Brush Make(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }

    public SpeedChart() => FlowDirection = FlowDirection.LeftToRight;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 20) return;
        var r = ReadSamples;
        var wr = WriteSamples;
        double max = 1;
        if (r != null && r.Count > 0) max = Math.Max(max, r.Max());
        if (wr != null && wr.Count > 0) max = Math.Max(max, wr.Max());
        max *= 1.15;
        double left = 46, bottom = 18, top = 6;
        var axis = new Pen(AxisBrush, 0.6);
        axis.Freeze();
        var tf = new Typeface("Segoe UI");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = 0; i <= 4; i++)
        {
            double y = top + (h - bottom - top) * i / 4;
            dc.DrawLine(axis, new Point(left, y), new Point(w, y));
            var label = new FormattedText((max * (4 - i) / 4).ToString("0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, tf, 10, AxisBrush, dpi);
            dc.DrawText(label, new Point(2, y - 7));
        }
        Draw(dc, wr, WriteBrush, left, top, w, h - bottom, max);
        Draw(dc, r, ReadBrush, left, top, w, h - bottom, max);
        var mb = new FormattedText("MB/s", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 10, AxisBrush, dpi);
        dc.DrawText(mb, new Point(2, h - 14));
    }

    private static void Draw(DrawingContext dc, IReadOnlyList<double>? s, Brush brush, double x0, double y0, double x1, double y1, double max)
    {
        if (s == null || s.Count < 2) return;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int i = 0; i < s.Count; i++)
            {
                double x = x0 + (x1 - x0) * i / (s.Count - 1);
                double y = y1 - (y1 - y0) * Math.Min(1, s[i] / max);
                if (i == 0) g.BeginFigure(new Point(x, y), false, false);
                else g.LineTo(new Point(x, y), true, true);
            }
        }
        geo.Freeze();
        var pen = new Pen(brush, 2) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
