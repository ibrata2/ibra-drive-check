using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using IbraDriveCheck.App.Infrastructure;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.App.Controls;

/// <summary>
/// خريطة الفحص: مربع لكل منطقة بلون حالتها. ترسم مباشرة (سريعة حتى مع آلاف المربعات)،
/// وتتحدث أثناء الفحص عبر خاصية Version. الضغط على مربع يرفع CellClicked.
/// </summary>
public sealed class ScanMapControl : FrameworkElement
{
    public static readonly DependencyProperty LiveMapProperty = DependencyProperty.Register(
        nameof(LiveMap), typeof(ScanMap), typeof(ScanMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RecordsProperty = DependencyProperty.Register(
        nameof(Records), typeof(CellRecord[]), typeof(ScanMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(ScanMapControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(ScanMapControl),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty MaxCellSizeProperty = DependencyProperty.Register(
        nameof(MaxCellSize), typeof(double), typeof(ScanMapControl),
        new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public ScanMap? LiveMap { get => (ScanMap?)GetValue(LiveMapProperty); set => SetValue(LiveMapProperty, value); }
    public CellRecord[]? Records { get => (CellRecord[]?)GetValue(RecordsProperty); set => SetValue(RecordsProperty, value); }
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public double MaxCellSize { get => (double)GetValue(MaxCellSizeProperty); set => SetValue(MaxCellSizeProperty, value); }

    public event Action<int>? CellClicked;

    private const double Gap = 2;

    public ScanMapControl()
    {
        FlowDirection = FlowDirection.LeftToRight;   // العناوين تتزايد من اليسار لليمين دائماً
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    private int Count => LiveMap?.Cells.Length ?? Records?.Length ?? 0;

    private CellState StateAt(int i) => LiveMap != null ? LiveMap.Cells[i].State : Records![i].State;

    private double CellSize(int count)
    {
        double s = count switch { <= 128 => 22, <= 512 => 14, <= 1100 => 11, <= 2100 => 8, _ => 6 };
        return Math.Min(s, MaxCellSize);
    }

    private (double size, int cols) Layout(double width)
    {
        int n = Count;
        double s = CellSize(n);
        if (double.IsInfinity(width) || width <= 0) width = 600;
        int cols = Math.Max(1, (int)((width + Gap) / (s + Gap)));
        return (s, cols);
    }

    protected override Size MeasureOverride(Size available)
    {
        int n = Count;
        if (n == 0) return new Size(0, 0);
        var (s, cols) = Layout(available.Width);
        int rows = (n + cols - 1) / cols;
        double w = double.IsInfinity(available.Width) ? cols * (s + Gap) : available.Width;
        return new Size(w, rows * (s + Gap));
    }

    protected override void OnRender(DrawingContext dc)
    {
        int n = Count;
        if (n == 0) return;
        var (s, cols) = Layout(ActualWidth);
        for (int i = 0; i < n; i++)
        {
            int r = i / cols, c = i % cols;
            var rect = new Rect(c * (s + Gap), r * (s + Gap), s, s);
            double rad = s >= 10 ? 2.5 : 1.5;
            dc.DrawRoundedRectangle(VerdictColors.Cell(StateAt(i)), null, rect, rad, rad);
        }
        int sel = SelectedIndex;
        if (sel >= 0 && sel < n)
        {
            int r = sel / cols, c = sel % cols;
            var rect = new Rect(c * (s + Gap) - 1.5, r * (s + Gap) - 1.5, s + 3, s + 3);
            var pen = new Pen(Brushes.Black, 2);
            dc.DrawRoundedRectangle(null, new Pen(Brushes.White, 3.5), rect, 3, 3);
            dc.DrawRoundedRectangle(null, pen, rect, 3, 3);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        int n = Count;
        if (n == 0) return;
        var p = e.GetPosition(this);
        var (s, cols) = Layout(ActualWidth);
        int c = (int)(p.X / (s + Gap)), r = (int)(p.Y / (s + Gap));
        if (c < 0 || c >= cols) return;
        int i = r * cols + c;
        if (i < 0 || i >= n) return;
        SelectedIndex = i;
        CellClicked?.Invoke(i);
    }
}
