using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using IbraDriveCheck.App.Infrastructure;
using IbraDriveCheck.Core.Devices.Windows;
using IbraDriveCheck.Core.Engine;
using IbraDriveCheck.Core.Localization;
using IbraDriveCheck.Core.Model;
using IbraDriveCheck.Core.Safety;
using IbraDriveCheck.Core.Storage;

namespace IbraDriveCheck.App.ViewModels;

public sealed class KindOption : ObservableObject
{
    public ScanKind Kind { get; init; }
    public string Glyph { get; init; } = "";
    public string Title => Strings.Kind(Loc.I.Lang, Kind);
    public string Desc => Loc.T("kindDesc." + Kind);
    private string? _blocked;
    public string? BlockedReason { get => _blocked; set { Set(ref _blocked, value); Raise(nameof(IsAllowed)); } }
    public bool IsAllowed => _blocked == null;
    public void Refresh() => RaiseAll();
}

/// <summary>شاشة الفحص: اختيار الوحدة ونوع الفحص، المتابعة المباشرة، الخريطة والنتيجة.</summary>
public sealed class ScannerViewModel : ObservableObject
{
    public static ScannerViewModel I { get; } = new();
    public MainViewModel Main => MainViewModel.I;

    public ObservableCollection<KindOption> Kinds { get; } = new()
    {
        new() { Kind = ScanKind.Quick, Glyph = "" },
        new() { Kind = ScanKind.Deep, Glyph = "" },
        new() { Kind = ScanKind.FullFreeSpace, Glyph = "" },
        new() { Kind = ScanKind.FullDestructive, Glyph = "" },
        new() { Kind = ScanKind.ReadOnly, Glyph = "" },
    };

    private KindOption? _kind;
    public KindOption? SelectedKind { get => _kind; set { Set(ref _kind, value); Raise(nameof(ShowReadOnlyMode)); } }
    public bool ShowReadOnlyMode => _kind?.Kind == ScanKind.ReadOnly;

    private bool _readOnlyFull = true;
    public bool ReadOnlyFull { get => _readOnlyFull; set => Set(ref _readOnlyFull, value); }
    public bool ReadOnlySampled { get => !_readOnlyFull; set => ReadOnlyFull = !value; }

    private ScanSession? _session;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _timer;

    private bool _running;
    public bool IsRunning { get => _running; private set { if (Set(ref _running, value)) { Raise(nameof(IsNotRunning)); Raise(nameof(CanPause)); Raise(nameof(CanResume)); } } }
    public bool IsNotRunning => !_running;
    private bool _paused;
    public bool IsPaused { get => _paused; private set { if (Set(ref _paused, value)) { Raise(nameof(CanPause)); Raise(nameof(CanResume)); } } }
    public bool CanPause => _running && !_paused && (_session?.PauseSupported ?? false) && _session?.Phase != ScanPhase.Restoring;
    public bool CanResume => _running && _paused;

    // ── المتابعة المباشرة ──
    private double _progress;
    public double ProgressPercent { get => _progress; private set => Set(ref _progress, value); }
    private string _phase = "";
    public string PhaseText { get => _phase; private set => Set(ref _phase, value); }
    private string _tested = "—", _good = "0", _bad = "0", _read = "—", _write = "—", _elapsed = "00:00", _remaining = "—";
    public string TestedText { get => _tested; private set => Set(ref _tested, value); }
    public string GoodText { get => _good; private set => Set(ref _good, value); }
    public string BadText { get => _bad; private set => Set(ref _bad, value); }
    public string ReadText { get => _read; private set => Set(ref _read, value); }
    public string WriteText { get => _write; private set => Set(ref _write, value); }
    public string ElapsedText { get => _elapsed; private set => Set(ref _elapsed, value); }
    public string RemainingText { get => _remaining; private set => Set(ref _remaining, value); }

    private ScanMap? _map;
    public ScanMap? LiveMap { get => _map; private set => Set(ref _map, value); }
    private CellRecord[]? _records;
    public CellRecord[]? Records { get => _records; private set => Set(ref _records, value); }
    private int _mapVersion;
    public int MapVersion { get => _mapVersion; private set => Set(ref _mapVersion, value); }
    private int _selectedCell = -1;
    public int SelectedCell { get => _selectedCell; set { Set(ref _selectedCell, value); UpdateCellDetails(); } }
    public ObservableCollection<InfoRow> CellDetails { get; } = new();
    public bool HasCellDetails => CellDetails.Count > 0;

    // ── النتيجة ──
    private ScanResult? _result;
    public ScanResult? Result { get => _result; private set { Set(ref _result, value); RaiseResult(); } }
    public bool HasResult => _result != null;
    public string VerdictText => _result == null ? "" : Strings.Verdict(Loc.I.Lang, _result.Verdict);
    public string VerdictHint => _result == null ? "" : Strings.VerdictHint(Loc.I.Lang, _result.Verdict);
    public Brush VerdictBrush => _result == null ? Brushes.Gray : VerdictColors.For(_result.Verdict);
    public string VerdictGlyph => _result == null ? "" : VerdictColors.Glyph(_result.Verdict);
    public bool ShowFix => _result?.Verdict == Verdict.FakeCapacity;
    public RelayCommand FixCommand { get; } = new(() => MainViewModel.I.Navigate("tools"));
    public ObservableCollection<InfoRow> ResultRows { get; } = new();
    public ObservableCollection<string> ResultNotes { get; } = new();

    public ObservableCollection<InfoRow> DriveInfo { get; } = new();

    // ── تنبيهات ──
    private string? _pending;
    public string? PendingRestoreText { get => _pending; private set { Set(ref _pending, value); Raise(nameof(HasPendingRestore)); } }
    public bool HasPendingRestore => _pending != null;
    private string? _leftovers;
    public string? LeftoversText { get => _leftovers; private set { Set(ref _leftovers, value); Raise(nameof(HasLeftovers)); } }
    public bool HasLeftovers => _leftovers != null;
    private string[] _leftoverDirs = Array.Empty<string>();

    public RelayCommand StartCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand DeleteLeftoversCommand { get; }
    public RelayCommand RestartAdminCommand { get; }

    private ScannerViewModel()
    {
        _kind = Kinds[0];
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => Tick();

        StartCommand = new RelayCommand(() => _ = StartAsync(SelectedKind?.Kind ?? ScanKind.Quick), () => !IsRunning && Main.IsIdle && Main.SelectedDrive != null && (SelectedKind?.IsAllowed ?? false));
        PauseCommand = new RelayCommand(Pause, () => CanPause);
        ResumeCommand = new RelayCommand(Resume, () => CanResume);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        RestoreCommand = new RelayCommand(() => _ = RestoreAsync(), () => HasPendingRestore && !IsRunning);
        DeleteLeftoversCommand = new RelayCommand(DeleteLeftovers, () => HasLeftovers && !IsRunning);
        RestartAdminCommand = new RelayCommand(() => Shell.RestartElevated("--page scanner"));

        Main.SelectedDriveChanged += OnDriveChanged;
        Loc.I.LanguageChanged += () =>
        {
            foreach (var k in Kinds) k.Refresh();
            OnDriveChanged(refreshHealth: false);
            RaiseResult();
            UpdateCellDetails();
        };
        CheckPendingRestore();
    }

    public void OnDriveChanged() => OnDriveChanged(true);

    private void OnDriveChanged(bool refreshHealth)
    {
        var item = Main.SelectedDrive;
        DriveInfo.Clear();
        foreach (var k in Kinds) k.BlockedReason = null;
        if (item == null) { LeftoversText = null; return; }
        var d = item.Drive;
        foreach (var r in MainViewModel.InfoRows(d, item.Health, item.Last)) DriveInfo.Add(r);
        foreach (var k in Kinds)
        {
            var dec = SafetyPolicy.Check(d, k.Kind);
            k.BlockedReason = dec.Allowed ? null : Loc.T(dec.ReasonKey);
        }
        if (SelectedKind != null && !SelectedKind.IsAllowed)
            SelectedKind = Kinds.FirstOrDefault(k => k.IsAllowed) ?? SelectedKind;

        // بقايا ملفات اختبار من تشغيل سابق انقطع
        _leftoverDirs = d.Volumes.SelectMany(v => FreeSpaceFileTest.FindLeftovers(v.Letter + "\\")).ToArray();
        LeftoversText = _leftoverDirs.Length > 0 ? string.Join("\n", _leftoverDirs) : null;

        if (refreshHealth && item.Health == null && !IsRunning)
            _ = LoadHealthAsync(item);
    }

    private async Task LoadHealthAsync(DriveItem item)
    {
        try
        {
            var h = await Task.Run(() => HealthReader.Read(item.Drive));
            item.Health = h;
            if (Main.SelectedDrive == item) OnDriveChanged(refreshHealth: false);
        }
        catch { }
    }

    public async Task StartAsync(ScanKind kind)
    {
        var item = Main.SelectedDrive;
        if (item == null || IsRunning || Main.IsBusy) return;
        SelectedKind = Kinds.FirstOrDefault(k => k.Kind == kind) ?? SelectedKind;
        if (!TestExecution.Confirm(kind, item.Drive)) return;

        Result = null;
        Records = null;
        SelectedCell = -1;
        _session = new ScanSession();
        _cts = new CancellationTokenSource();
        IsPaused = false;
        IsRunning = true;
        Main.IsBusy = true;
        LiveMap = null;
        ProgressPercent = 0;
        _timer.Start();
        try
        {
            var req = new ScanRequest { Drive = item.Drive, Kind = kind, ReadOnlyFullSurface = ReadOnlyFull, SpeedTestMB = Main.Settings.SpeedTestMB };
            var r = await TestExecution.RunAsync(req, _session, _cts.Token);
            Tick();
            if (r != null)
            {
                if (item.Health != null && r.Health == null && kind != ScanKind.Health) r.Health = null;
                Result = r;
                Records = r.Map;
                LiveMap = null;
                MapVersion++;
                Main.AddResult(r);
                if (r.Verdict != Verdict.Incomplete) ProgressPercent = 100;
            }
        }
        finally
        {
            _timer.Stop();
            IsRunning = false;
            IsPaused = false;
            Main.IsBusy = false;
            CheckPendingRestore();
            await Main.RefreshDrivesAsync(false);
            OnDriveChanged(refreshHealth: false);
            CommandRefresh();
        }
    }

    private void Tick()
    {
        var s = _session;
        if (s == null) return;
        if (s.Map != null && !ReferenceEquals(LiveMap, s.Map)) { LiveMap = s.Map; Records = null; }
        if (s.Map != null && s.Map.Version != MapVersion) MapVersion = s.Map.Version;
        ProgressPercent = Math.Round(s.Progress * 100, 1);
        PhaseText = Loc.T("phase." + s.Phase) + (IsPaused ? " — " + Loc.T("scan.pause") : "");
        TestedText = Fmt.Bytes(Interlocked.Read(ref s.BytesTested));
        if (s.Map != null)
        {
            var (g, sl, f, w, r, _) = s.Map.Count();
            GoodText = (g + sl).ToString();
            BadText = (f + w + r).ToString();
        }
        ReadText = Fmt.Speed(s.CurrentReadMBps);
        WriteText = Fmt.Speed(s.CurrentWriteMBps);
        ElapsedText = Fmt.Duration(s.Elapsed);
        RemainingText = s.Remaining is TimeSpan t ? Fmt.Duration(t) : "—";
        if (SelectedCell >= 0) UpdateCellDetails();
        Raise(nameof(CanPause));
    }

    private void Pause()
    {
        if (_session == null) return;
        _session.Pause.Pause();
        IsPaused = true;
    }

    private void Resume()
    {
        if (_session == null) return;
        _session.Pause.Resume();
        IsPaused = false;
    }

    private void Cancel()
    {
        _cts?.Cancel();
        _session?.Pause.Resume();
        IsPaused = false;
    }

    private void UpdateCellDetails()
    {
        CellDetails.Clear();
        int i = _selectedCell;
        CellRecord? c = null;
        if (LiveMap != null && i >= 0 && i < LiveMap.Cells.Length)
        {
            var x = LiveMap.Cells[i];
            c = new CellRecord { Offset = x.Offset, Length = x.Length, State = x.State, ReadMBps = x.ReadMBps, WriteMBps = x.WriteMBps, ReadLatencyMs = x.ReadLatencyMs, WriteLatencyMs = x.WriteLatencyMs, AliasOfOffset = x.AliasOfOffset, NoteKey = x.NoteKey };
        }
        else if (Records != null && i >= 0 && i < Records.Length) c = Records[i];
        if (c != null) foreach (var r in CellRows(c, i)) CellDetails.Add(r);
        Raise(nameof(HasCellDetails));
    }

    public static IEnumerable<InfoRow> CellRows(CellRecord c, int index)
    {
        yield return new InfoRow("#", (index + 1).ToString());
        yield return new InfoRow(Loc.T("cell.offset"), $"{Fmt.Bytes(c.Offset)}  ({Fmt.Hex(c.Offset)})");
        yield return new InfoRow(Loc.T("cell.length"), Fmt.Bytes(c.Length));
        yield return new InfoRow(Loc.T("cell.state"), Strings.Cell(Loc.I.Lang, c.State), true);
        if (c.NoteKey != null) yield return new InfoRow("", Loc.T(c.NoteKey));
        yield return new InfoRow(Loc.T("cell.writeSpeed"), Fmt.Speed(c.WriteMBps));
        yield return new InfoRow(Loc.T("cell.readSpeed"), Fmt.Speed(c.ReadMBps));
        if (c.ReadLatencyMs > 0 || c.WriteLatencyMs > 0)
            yield return new InfoRow(Loc.T("cell.latency"), $"W {c.WriteLatencyMs:0.##} ms / R {c.ReadLatencyMs:0.##} ms");
        if (c.AliasOfOffset is long a) yield return new InfoRow(Loc.T("cell.aliasOf"), $"{Fmt.Bytes(a)}  ({Fmt.Hex(a)})");
    }

    private void RaiseResult()
    {
        Raise(nameof(HasResult)); Raise(nameof(VerdictText)); Raise(nameof(VerdictHint)); Raise(nameof(VerdictBrush)); Raise(nameof(VerdictGlyph)); Raise(nameof(ShowFix));
        ResultRows.Clear();
        ResultNotes.Clear();
        var r = _result;
        if (r == null) return;
        string lang = Loc.I.Lang;
        ResultRows.Add(new InfoRow(Loc.T("res.declared"), Fmt.Bytes(r.DeclaredBytes)));
        if (r.VerifiedBytes is long v) ResultRows.Add(new InfoRow(Loc.T("res.verified"), Fmt.Bytes(v), true));
        if (r.EstimatedRealBytes is long e) ResultRows.Add(new InfoRow(Loc.T("res.estimatedReal"), $"{Fmt.Bytes(e)}  ({Loc.T("res.estimatedNote")})", true));
        if (r.EstimatedFakeBytes is long f) ResultRows.Add(new InfoRow(Loc.T("res.estimatedFake"), $"{Fmt.Bytes(f)}  ({Loc.T("res.estimatedNote")})", true));
        ResultRows.Add(new InfoRow(Loc.T("res.coverage"), r.CoveragePercent.ToString("0.######") + "%"));
        ResultRows.Add(new InfoRow(Loc.T("res.samples"), r.SampleCount.ToString()));
        if (r.AvgWriteMBps > 0) ResultRows.Add(new InfoRow(Loc.T("res.avgWrite"), Fmt.Speed(r.AvgWriteMBps)));
        if (r.AvgReadMBps > 0) ResultRows.Add(new InfoRow(Loc.T("res.avgRead"), Fmt.Speed(r.AvgReadMBps)));
        if (r.RestoreOk is bool ok) ResultRows.Add(new InfoRow(Loc.T("res.restore"), ok ? Loc.T("res.restoreOk") : Loc.F("res.restoreFail", r.RestoreFailures)));
        ResultRows.Add(new InfoRow(Loc.T("res.duration"), Fmt.Duration(r.Duration)));
        foreach (var n in r.Notes) ResultNotes.Add(Strings.Format(lang, n));
    }

    // ── استعادة فحص سابق انقطع ──
    private List<(string BasePath, JournalHeader Header)> _pendingList = new();

    private void CheckPendingRestore()
    {
        _pendingList = FileJournal.FindPending(AppPaths.Journals);
        PendingRestoreText = _pendingList.Count == 0 ? null : string.Join("\n", _pendingList.Select(p => Loc.F("scan.pendingRestore", p.Header.DriveName)));
    }

    private async Task RestoreAsync()
    {
        if (!Main.IsElevated) { TestExecution.Msg(Loc.T("common.adminRequired"), true); return; }
        foreach (var (basePath, header) in _pendingList.ToList())
        {
            try
            {
                var (ok, failed) = await Task.Run(() => ScanRunner.RestorePending(basePath, header));
                TestExecution.Msg(Loc.F("scan.restoreDone", ok, failed), failed > 0);
            }
            catch (IdentityChangedException) { TestExecution.Msg(Loc.F("scan.pendingRestore", header.DriveName), true); }
            catch (VolumeInUseException ex) { TestExecution.Msg(Loc.F("scan.volumeInUse", ex.Volume), true); }
            catch (Exception ex) { TestExecution.Msg(Loc.F("scan.failedStart", ex.Message), true); }
        }
        CheckPendingRestore();
    }

    private void DeleteLeftovers()
    {
        foreach (var d in _leftoverDirs)
        {
            try { System.IO.Directory.Delete(d, true); } catch { }
        }
        OnDriveChanged(refreshHealth: false);
    }

    private static void CommandRefresh() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();
}
