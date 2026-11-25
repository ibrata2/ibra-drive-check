using System.Windows;
using System.Windows.Controls;
using IbraDriveCheck.App.Infrastructure;
using IbraDriveCheck.App.ViewModels;
using IbraDriveCheck.Core.Localization;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.App.Views;

public sealed record CompareRow(string Label, string A, string B);

public partial class HistoryView : UserControl, IPageActivated
{
    public HistoryView()
    {
        InitializeComponent();
        DataContext = HistoryList.I;
        HistoryList.I.Items.CollectionChanged += (_, _) => UpdateEmpty();
        UpdateEmpty();
    }

    public void OnActivated() => UpdateEmpty();

    private void UpdateEmpty() => EmptyText.Visibility = HistoryList.I.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private List<ScanResult> Selected() => HistList.SelectedItems.Cast<HistoryItem>().Select(h => h.Result).ToList();

    private void OnCompare(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count != 2)
        {
            TestExecution.Msg(Loc.T("hist.compareHint"));
            return;
        }
        var (a, b) = (sel[0], sel[1]);
        string lang = Loc.I.Lang;
        string Cap(long? v) => v is long x ? Fmt.Bytes(x) : "—";
        var rows = new List<CompareRow>
        {
            new(Loc.T("res.date"), a.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), b.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
            new(Loc.T("res.drive"), a.Drive.DisplayName, b.Drive.DisplayName),
            new(Loc.T("res.kind"), Strings.Kind(lang, a.Kind), Strings.Kind(lang, b.Kind)),
            new(Loc.T("res.verdict"), Strings.Verdict(lang, a.Verdict), Strings.Verdict(lang, b.Verdict)),
            new(Loc.T("res.declared"), Fmt.Bytes(a.DeclaredBytes), Fmt.Bytes(b.DeclaredBytes)),
            new(Loc.T("res.verified"), Cap(a.VerifiedBytes), Cap(b.VerifiedBytes)),
            new(Loc.T("res.estimatedReal"), Cap(a.EstimatedRealBytes), Cap(b.EstimatedRealBytes)),
            new(Loc.T("res.coverage"), a.CoveragePercent + "%", b.CoveragePercent + "%"),
            new(Loc.T("scan.goodCells"), (a.GoodCells + a.SlowCells).ToString(), (b.GoodCells + b.SlowCells).ToString()),
            new(Loc.T("scan.badCells"), (a.FailedCells + a.WriteErrorCells + a.ReadErrorCells).ToString(), (b.FailedCells + b.WriteErrorCells + b.ReadErrorCells).ToString()),
            new(Loc.T("res.avgRead"), Fmt.Speed(a.AvgReadMBps), Fmt.Speed(b.AvgReadMBps)),
            new(Loc.T("res.avgWrite"), Fmt.Speed(a.AvgWriteMBps), Fmt.Speed(b.AvgWriteMBps)),
            new(Loc.T("res.duration"), Fmt.Duration(a.Duration), Fmt.Duration(b.Duration)),
        };
        CompareRows.ItemsSource = rows;
        ComparePanel.Visibility = Visibility.Visible;
    }

    private void OnCloseCompare(object sender, RoutedEventArgs e) => ComparePanel.Visibility = Visibility.Collapsed;

    private void OnReport(object sender, RoutedEventArgs e)
    {
        if (HistList.SelectedItem is HistoryItem h)
        {
            ReportsView.Pending = h.Result;
            MainViewModel.I.Navigate("reports");
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        MainViewModel.I.DeleteResults(sel);
    }

    private void OnDeleteAll(object sender, RoutedEventArgs e)
    {
        if (HistoryList.I.Items.Count == 0) return;
        if (TestExecution.Ask(Loc.T("hist.confirmDeleteAll"))) MainViewModel.I.DeleteAllResults();
    }
}
