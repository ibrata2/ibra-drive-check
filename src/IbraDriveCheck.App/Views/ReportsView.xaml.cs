using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using IbraDriveCheck.App.Infrastructure;
using IbraDriveCheck.App.ViewModels;
using IbraDriveCheck.Core.Localization;
using IbraDriveCheck.Core.Model;
using IbraDriveCheck.Core.Reports;
using Microsoft.Win32;

namespace IbraDriveCheck.App.Views;

public partial class ReportsView : UserControl, IPageActivated
{
    /// <summary>فحص مختار من صفحة السجل لفتحه هنا.</summary>
    public static ScanResult? Pending { get; set; }
    private ScanResult? _current;

    public ReportsView()
    {
        InitializeComponent();
        DataContext = HistoryList.I;
        Loc.I.LanguageChanged += () => Show(_current);
        Show(null);
    }

    public void OnActivated()
    {
        var target = Pending ?? _current ?? HistoryList.I.Items.FirstOrDefault()?.Result;
        Pending = null;
        var item = HistoryList.I.Items.FirstOrDefault(i => i.Result == target);
        if (item != null) Items.SelectedItem = item;
        Show(target);
    }

    private void OnSelect(object sender, SelectionChangedEventArgs e)
    {
        if (Items.SelectedItem is HistoryItem h) Show(h.Result);
    }

    private void Show(ScanResult? r)
    {
        _current = r;
        Preview.Visibility = r == null ? Visibility.Collapsed : Visibility.Visible;
        if (r == null) return;
        string lang = Loc.I.Lang;
        VerdictBox.Background = VerdictColors.For(r.Verdict);
        VerdictTitle.Text = r.Advice != null ? Strings.Level(lang, r.Advice.Level) : Strings.Verdict(lang, r.Verdict);
        VerdictHint.Text = r.Advice != null ? Strings.LevelHint(lang, r.Advice.Level) : Strings.VerdictHint(lang, r.Verdict);
        SummaryRows.ItemsSource = ReportBuilder.Summary(r, lang).Select(x => new InfoRow(x.Label, x.Value)).ToList();
        var notes = r.Notes.Select(n => Strings.Format(lang, n)).ToList();
        NotesList.ItemsSource = notes;
        NotesTitle.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MapView.Records = r.Map;
        MapView.Visibility = r.Map.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string BaseName(ScanResult r)
    {
        var name = new string(r.Drive.DisplayName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
        return $"IBRA-DRIVE-CHECK_{name}_{r.StartedUtc.ToLocalTime():yyyyMMdd_HHmm}".Replace(' ', '_');
    }

    private void Save(string ext, string filter, Func<ScanResult, string> content)
    {
        if (_current == null) return;
        var dlg = new SaveFileDialog { FileName = BaseName(_current) + ext, Filter = filter, AddExtension = true };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, content(_current), new UTF8Encoding(ext != ".csv"));
            TestExecution.Msg(Loc.F("rep.saved", dlg.FileName));
        }
        catch (Exception ex)
        {
            TestExecution.Msg(ex.Message, true);
        }
    }

    private void OnCsv(object sender, RoutedEventArgs e) => Save(".csv", "CSV|*.csv", r => ReportBuilder.ToCsv(r, Loc.I.Lang));
    private void OnJson(object sender, RoutedEventArgs e) => Save(".json", "JSON|*.json", ReportBuilder.ToJson);
    private void OnHtml(object sender, RoutedEventArgs e) => Save(".html", "HTML|*.html", r => ReportBuilder.ToHtml(r, Loc.I.Lang));

    private void OnPdf(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        try { ReportPrinter.Print(_current, Loc.I.Lang, toPdf: true); }
        catch (Exception ex) { TestExecution.Msg(ex.Message, true); }
    }

    private void OnPrint(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        try { ReportPrinter.Print(_current, Loc.I.Lang, toPdf: false); }
        catch (Exception ex) { TestExecution.Msg(ex.Message, true); }
    }
}
