using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using IbraDriveCheck.App.Controls;
using IbraDriveCheck.Core.Localization;
using IbraDriveCheck.Core.Model;
using IbraDriveCheck.Core.Reports;

namespace IbraDriveCheck.App.Infrastructure;

/// <summary>طباعة التقرير وحفظه PDF (عبر طابعة Microsoft Print to PDF المدمجة في ويندوز).</summary>
public static class ReportPrinter
{
    public static FlowDocument Build(ScanResult r, string lang, double pageWidth)
    {
        bool rtl = lang != "en";
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            PagePadding = new Thickness(48),
            ColumnWidth = pageWidth,
            PageWidth = pageWidth,
            Background = Brushes.White,
            Foreground = Brushes.Black,
        };
        var red = new SolidColorBrush(Color.FromRgb(0xE6, 0x00, 0x12));
        doc.Blocks.Add(new Paragraph(new Run("IBRA DRIVE CHECK")) { FontSize = 24, FontWeight = FontWeights.Bold, Foreground = red, Margin = new Thickness(0) });
        doc.Blocks.Add(new Paragraph(new Run(Strings.Get(lang, "rep.header"))) { FontSize = 14, Margin = new Thickness(0, 0, 0, 12) });

        string verdict = r.Advice != null ? Strings.Level(lang, r.Advice.Level) : Strings.Verdict(lang, r.Verdict);
        string hint = r.Advice != null ? Strings.LevelHint(lang, r.Advice.Level) : Strings.VerdictHint(lang, r.Verdict);
        var badge = new Paragraph(new Run("  " + verdict + "  ")) { FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Background = VerdictColors.For(r.Verdict), Margin = new Thickness(0, 0, 0, 4) };
        doc.Blocks.Add(badge);
        doc.Blocks.Add(new Paragraph(new Run(hint)) { Margin = new Thickness(0, 0, 0, 12) });

        var table = new Table { CellSpacing = 0 };
        table.Columns.Add(new TableColumn { Width = new GridLength(pageWidth * 0.36) });
        table.Columns.Add(new TableColumn { Width = new GridLength(pageWidth * 0.54) });
        var group = new TableRowGroup();
        foreach (var (k, v) in ReportBuilder.Summary(r, lang))
        {
            var row = new TableRow();
            row.Cells.Add(new TableCell(new Paragraph(new Run(k)) { Foreground = Brushes.DimGray, Margin = new Thickness(0, 3, 6, 3) }) { BorderBrush = Brushes.Gainsboro, BorderThickness = new Thickness(0, 0, 0, 1) });
            row.Cells.Add(new TableCell(new Paragraph(new Run(v)) { Margin = new Thickness(0, 3, 0, 3) }) { BorderBrush = Brushes.Gainsboro, BorderThickness = new Thickness(0, 0, 0, 1) });
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        doc.Blocks.Add(table);

        if (r.Notes.Count > 0)
        {
            doc.Blocks.Add(new Paragraph(new Run(Strings.Get(lang, "res.notes"))) { FontSize = 14, FontWeight = FontWeights.Bold, Foreground = red, Margin = new Thickness(0, 14, 0, 4) });
            var list = new System.Windows.Documents.List();
            foreach (var n in r.Notes) list.ListItems.Add(new ListItem(new Paragraph(new Run(Strings.Format(lang, n)))));
            doc.Blocks.Add(list);
        }

        if (r.Map.Length > 0)
        {
            doc.Blocks.Add(new Paragraph(new Run(Strings.Get(lang, "rep.map"))) { FontSize = 14, FontWeight = FontWeights.Bold, Foreground = red, Margin = new Thickness(0, 14, 0, 6) });
            var map = new ScanMapControl { Records = r.Map, Width = pageWidth - 110, MaxCellSize = 9 };
            map.Measure(new Size(map.Width, double.PositiveInfinity));
            map.Height = map.DesiredSize.Height;
            doc.Blocks.Add(new BlockUIContainer(map));
        }

        doc.Blocks.Add(new Paragraph(new Run($"{Strings.Get(lang, "rep.generated")} {ReportBuilder.Credit}")) { FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(0, 18, 0, 0) });
        return doc;
    }

    public static void Print(ScanResult r, string lang, bool toPdf)
    {
        var pd = new PrintDialog();
        if (toPdf)
        {
            try
            {
                var queue = new LocalPrintServer().GetPrintQueues().FirstOrDefault(q => q.Name.Contains("PDF", StringComparison.OrdinalIgnoreCase) && q.Name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));
                if (queue != null) pd.PrintQueue = queue;
                else if (pd.ShowDialog() != true) return;
            }
            catch
            {
                if (pd.ShowDialog() != true) return;
            }
        }
        else if (pd.ShowDialog() != true) return;

        double width = pd.PrintableAreaWidth > 100 ? pd.PrintableAreaWidth : 793;
        var doc = Build(r, lang, width);
        doc.PageHeight = pd.PrintableAreaHeight > 100 ? pd.PrintableAreaHeight : 1122;
        pd.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, "IBRA DRIVE CHECK Report");
    }
}
