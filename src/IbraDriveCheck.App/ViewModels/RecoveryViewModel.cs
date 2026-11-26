using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using IbraDriveCheck.App.Infrastructure;
using IbraDriveCheck.Core.Devices.Windows;
using IbraDriveCheck.Core.Localization;
using IbraDriveCheck.Core.Recovery;

namespace IbraDriveCheck.App.ViewModels;

/// <summary>عقدة في شجرة المحذوفات (مجلد أو ملف) مع اختيار ثلاثي الحالة.</summary>
public sealed class RecNode : ObservableObject
{
    public RecNode(string name, RecNode? parent, RecoveryItem? item)
    {
        Name = name;
        Parent = parent;
        Item = item;
    }

    public string Name { get; }
    public RecNode? Parent { get; }
    public RecoveryItem? Item { get; set; }
    public ObservableCollection<RecNode> Children { get; } = new();
    public Dictionary<string, RecNode>? FolderIndex;
    public bool IsFolder => Item == null || Item.IsDirectory;
    public bool IsDeletedFolder => Item?.IsDirectory == true;
    public long TotalSize { get; set; }
    public int FileCount { get; set; }

    private bool _expanded;
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }

    private bool? _checked = false;
    public bool? IsChecked
    {
        get => _checked;
        set => SetChecked(value ?? false, down: true, up: true);
    }

    private void SetChecked(bool? v, bool down, bool up)
    {
        if (_checked == v) return;
        _checked = v;
        if (down && v.HasValue) foreach (var c in Children) c.SetChecked(v, true, false);
        if (up) Parent?.Reevaluate();
        Raise(nameof(IsChecked));
    }

    private void Reevaluate()
    {
        bool? state = null;
        bool first = true;
        foreach (var c in Children)
        {
            if (first) { state = c._checked; first = false; }
            else if (state != c._checked) { state = null; break; }
        }
        SetChecked(state, down: false, up: true);
    }

    public string Glyph => IsFolder ? "" : Item!.Extension switch
    {
        "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "heic" => "",
        "mp4" or "mov" or "avi" or "mkv" or "3gp" or "wmv" => "",
        "mp3" or "wav" or "m4a" or "aac" or "flac" or "ogg" => "",
        "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "txt" => "",
        "zip" or "rar" or "7z" or "apk" => "",
        _ => "",
    };

    public Brush ChanceBrush => Item == null || Item.IsDirectory ? Brushes.Transparent : Item.Chance switch
    {
        RecoveryChance.Good => VerdictColors.Cell(Core.Model.CellState.Good),
        RecoveryChance.Partial => VerdictColors.Cell(Core.Model.CellState.Slow),
        RecoveryChance.Poor => VerdictColors.Cell(Core.Model.CellState.Failed),
        RecoveryChance.Carved => VerdictColors.Cell(Core.Model.CellState.Testing),
        _ => VerdictColors.Cell(Core.Model.CellState.Untested),
    };

    public string Info => IsFolder
        ? $"{FileCount} · {Fmt.Bytes(TotalSize)}"
        : $"{Fmt.Bytes(Item!.Size)}  ·  {Loc.T("rec.chance." + Item.Chance)}" + (Item.Modified is DateTime m ? $"  ·  {m.ToLocalTime():yyyy-MM-dd HH:mm}" : "");

    public IEnumerable<RecoveryItem> CheckedFiles()
    {
        if (Item != null && !Item.IsDirectory)
        {
            if (_checked == true) yield return Item;
            yield break;
        }
        if (_checked == false) yield break;
        foreach (var c in Children)
            foreach (var i in c.CheckedFiles()) yield return i;
    }

    public void RaiseTexts() { Raise(nameof(Info)); foreach (var c in Children) c.RaiseTexts(); }
}

public sealed class RecoveryViewModel : ObservableObject
{
    public static RecoveryViewModel I { get; } = new();
    public MainViewModel Main => MainViewModel.I;

    public ObservableCollection<VolumeOption> Volumes { get; } = new();
    private VolumeOption? _volume;
    public VolumeOption? SelectedVolume { get => _volume; set => Set(ref _volume, value); }

    private bool _deep;
    public bool Deep { get => _deep; set { Set(ref _deep, value); Raise(nameof(Quick)); } }
    public bool Quick { get => !_deep; set => Deep = !value; }

    public ObservableCollection<RecNode> Roots { get; } = new();
    private RecoveryScan? _scan;
    private VolumeOption? _scannedVolume;

    private string _filter = "";
    public string FilterText { get => _filter; set { if (Set(ref _filter, value)) Rebuild(); } }
    public List<string> TypeKeys { get; } = new() { "all", "images", "videos", "audio", "docs", "archives", "other" };
    public List<KeyValuePair<string, string>> Types => TypeKeys.Select(k => new KeyValuePair<string, string>(k, Loc.T("rec.type." + k))).ToList();
    private string _type = "all";
    public string SelectedType { get => _type; set { if (Set(ref _type, value)) Rebuild(); } }
    private bool _onlyGood;
    public bool OnlyGood { get => _onlyGood; set { if (Set(ref _onlyGood, value)) Rebuild(); } }

    private string _output = "";
    public string OutputFolder { get => _output; set => Set(ref _output, value); }

    private bool _busy;
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); Raise(nameof(IsIdle)); } }
    public bool IsIdle => !_busy;
    private double _progress;
    public double ProgressPercent { get => _progress; private set => Set(ref _progress, value); }
    private string _status = "";
    public string StatusText { get => _status; private set => Set(ref _status, value); }
    private string _summary = "";
    public string SummaryText { get => _summary; private set => Set(ref _summary, value); }
    public bool HasResults => Roots.Count > 0;

    public RelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RecoverCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand BrowseCommand { get; }

    private RecoveryProgress? _progressSrc;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _timer;

    private RecoveryViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Tick();
        ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => IsIdle && Main.IsIdle && SelectedVolume != null);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        RecoverCommand = new RelayCommand(() => _ = RecoverAsync(), () => IsIdle && _scan != null && Roots.Count > 0);
        SelectAllCommand = new RelayCommand(() => { foreach (var r in Roots) r.IsChecked = true; }, () => Roots.Count > 0);
        SelectNoneCommand = new RelayCommand(() => { foreach (var r in Roots) r.IsChecked = false; }, () => Roots.Count > 0);
        BrowseCommand = new RelayCommand(Browse);
        OutputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "IBRA Recovered");
        Loc.I.LanguageChanged += () =>
        {
            Raise(nameof(Types));
            foreach (var r in Roots) r.RaiseTexts();
            LoadVolumes();
            UpdateSummary();
        };
    }

    public void LoadVolumes()
    {
        var keep = SelectedVolume?.Letter ?? SelectedVolume?.Drive.HardwareKey;
        Volumes.Clear();
        foreach (var v in VolumeOption.From(Main.Drives, includeWholeDisks: true)) Volumes.Add(v);
        SelectedVolume = Volumes.FirstOrDefault(v => (v.Letter ?? v.Drive.HardwareKey) == keep)
                         ?? Volumes.FirstOrDefault(v => v.Drive.HardwareKey == Main.SelectedDrive?.Drive.HardwareKey && !v.IsWholeDisk)
                         ?? Volumes.FirstOrDefault(v => !v.IsSystem && !v.IsWholeDisk)
                         ?? Volumes.FirstOrDefault();
    }

    private bool EnsureAdmin(string page)
    {
        if (Main.IsElevated) return true;
        if (TestExecution.Ask(Loc.T("common.adminRequired") + "\n\n" + Loc.T("common.restartAdmin") + "?"))
            Shell.RestartElevated($"--page {page}");
        return false;
    }

    private IVolumeSource Open(VolumeOption v) => v.IsWholeDisk ? VolumeTools.OpenDiskSource(v.Drive.DiskNumber) : VolumeTools.OpenVolumeSource(v.Letter!);

    private async Task ScanAsync()
    {
        var v = SelectedVolume;
        if (v == null || !EnsureAdmin("recovery")) return;
        bool deep = Deep || v.IsWholeDisk;
        _cts = new CancellationTokenSource();
        _progressSrc = new RecoveryProgress();
        IsBusy = true;
        Main.IsBusy = true;
        Roots.Clear();
        Raise(nameof(HasResults));
        _scan = null;
        SummaryText = "";
        _timer.Start();
        try
        {
            var p = _progressSrc;
            var ct = _cts.Token;
            _scan = await Task.Run(() =>
            {
                using var src = Open(v);
                return RecoveryEngine.Scan(src, deep, p, ct);
            });
            _scannedVolume = v;
            Rebuild();
            UpdateSummary();
        }
        catch (Exception ex)
        {
            TestExecution.Msg(Loc.F("scan.failedStart", ex.Message), true);
        }
        finally
        {
            _timer.Stop();
            Tick();
            ProgressPercent = _scan != null ? 100 : 0;
            IsBusy = false;
            Main.IsBusy = false;
        }
    }

    private void UpdateSummary()
    {
        if (_scan == null) { SummaryText = ""; return; }
        int files = _scan.Items.Count(i => !i.IsDirectory);
        int good = _scan.Items.Count(i => !i.IsDirectory && i.Chance == RecoveryChance.Good);
        int carved = _scan.Items.Count(i => i.Chance == RecoveryChance.Carved);
        SummaryText = Loc.F("rec.summary", _scan.FileSystem, files, good, carved)
                      + (_scan.Cancelled ? "  — " + Loc.T("note.cancelled") : "")
                      + string.Concat(_scan.Notes.Select(n => "\n" + Loc.T(n)));
    }

    private bool TypeMatch(RecoveryItem i)
    {
        if (_type == "all" || i.IsDirectory) return true;
        string e = i.Extension;
        string cat = e switch
        {
            "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "heic" or "tif" or "tiff" or "raw" or "cr2" or "nef" or "psd" => "images",
            "mp4" or "mov" or "avi" or "mkv" or "3gp" or "wmv" or "flv" or "webm" or "mts" => "videos",
            "mp3" or "wav" or "m4a" or "aac" or "flac" or "ogg" or "wma" or "amr" => "audio",
            "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "txt" or "rtf" or "odt" or "csv" => "docs",
            "zip" or "rar" or "7z" or "apk" or "tar" or "gz" or "iso" => "archives",
            _ => "other",
        };
        return cat == _type;
    }

    private void Rebuild()
    {
        Roots.Clear();
        if (_scan == null) { Raise(nameof(HasResults)); return; }
        string f = _filter.Trim();
        var root = new RecNode("", null, null) { FolderIndex = new() };
        foreach (var item in _scan.Items)
        {
            if (!item.IsDirectory)
            {
                if (_onlyGood && item.Chance != RecoveryChance.Good) continue;
                if (!TypeMatch(item)) continue;
                if (f.Length > 0 && !item.FullPath.Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
            }
            else if (_type != "all" || _onlyGood || f.Length > 0) continue;   // المجلدات تظهر من خلال ملفاتها عند التصفية

            var parent = Folder(root, item.Dir);
            if (item.IsDirectory)
            {
                var node = Folder(parent, item.Name, isPathSegment: true);
                node.Item = item;
            }
            else
            {
                parent.Children.Add(new RecNode(item.Name, parent, item));
                for (var n = parent; n != null; n = n.Parent) { n.FileCount++; n.TotalSize += item.Size; }
            }
        }
        foreach (var c in root.Children) Roots.Add(c);
        foreach (var r in Roots) r.IsExpanded = Roots.Count <= 3;
        Raise(nameof(HasResults));
    }

    private static RecNode Folder(RecNode root, string path, bool isPathSegment = false)
    {
        var node = root;
        var parts = isPathSegment ? new[] { path } : path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            node.FolderIndex ??= new Dictionary<string, RecNode>(StringComparer.OrdinalIgnoreCase);
            if (!node.FolderIndex.TryGetValue(part, out var child))
            {
                string display = part switch { "$Carved" => Loc.T("rec.carvedFolder"), "$Orphans" => Loc.T("rec.orphans"), _ => part };
                child = new RecNode(display, node, null);
                node.FolderIndex[part] = child;
                node.Children.Insert(node.Children.TakeWhile(c => c.IsFolder).Count(), child);
            }
            node = child;
        }
        return node;
    }

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("rec.output") };
        if (Directory.Exists(OutputFolder)) dlg.InitialDirectory = OutputFolder;
        if (dlg.ShowDialog() == true) OutputFolder = dlg.FolderName;
    }

    private async Task RecoverAsync()
    {
        var v = _scannedVolume;
        if (v == null || _scan == null) return;
        var items = Roots.SelectMany(r => r.CheckedFiles()).ToList();
        if (items.Count == 0) { TestExecution.Msg(Loc.T("rec.nothingSelected")); return; }
        string outRoot;
        try { outRoot = Path.GetFullPath(OutputFolder); }
        catch { TestExecution.Msg(Loc.T("rec.badOutput"), true); return; }
        string outDrive = (Path.GetPathRoot(outRoot) ?? "").TrimEnd('\\').ToUpperInvariant();
        var sameDisk = v.IsWholeDisk ? v.Drive.Volumes.Select(x => x.Letter.ToUpperInvariant()) : new[] { v.Letter!.ToUpperInvariant() };
        if (sameDisk.Contains(outDrive)) { TestExecution.Msg(Loc.T("rec.sameDrive"), true); return; }
        if (!EnsureAdmin("recovery")) return;

        _cts = new CancellationTokenSource();
        _progressSrc = new RecoveryProgress();
        IsBusy = true;
        Main.IsBusy = true;
        _timer.Start();
        try
        {
            var p = _progressSrc;
            var ct = _cts.Token;
            var res = await Task.Run(() =>
            {
                using var src = Open(v);
                return RecoveryEngine.Recover(src, items, outRoot, p, ct);
            });
            string msg = Loc.F("rec.done", res.Files, Fmt.Bytes(res.Bytes), outRoot);
            if (res.Failed > 0) msg += "\n" + Loc.F("rec.failed", res.Failed) + "\n" + string.Join("\n", res.Errors.Take(5));
            Main.Notify?.Show("IBRA DRIVE CHECK", Loc.F("rec.done", res.Files, Fmt.Bytes(res.Bytes), outRoot));
            TestExecution.Msg(msg, res.Failed > 0);
            Shell.Open(outRoot);
        }
        catch (Exception ex)
        {
            TestExecution.Msg(Loc.F("scan.failedStart", ex.Message), true);
        }
        finally
        {
            _timer.Stop();
            ProgressPercent = 100;
            IsBusy = false;
            Main.IsBusy = false;
        }
    }

    private void Tick()
    {
        var p = _progressSrc;
        if (p == null) return;
        ProgressPercent = Math.Round(p.Fraction * 100, 1);
        StatusText = Loc.T(p.Phase) + "  —  " + Loc.F("rec.found", p.Found);
    }
}
