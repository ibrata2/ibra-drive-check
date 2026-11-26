using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using IbraDriveCheck.App.Infrastructure;
using IbraDriveCheck.App.Views;
using IbraDriveCheck.Core.Devices.Windows;
using IbraDriveCheck.Core.Engine;
using IbraDriveCheck.Core.Localization;
using IbraDriveCheck.Core.Model;
using IbraDriveCheck.Core.Recovery;
using IbraDriveCheck.Core.Security;
using IbraDriveCheck.Core.Storage;
using IbraDriveCheck.Core.Tools;

namespace IbraDriveCheck.App.ViewModels;

/// <summary>أساس مشترك لصفحات الأدوات: تشغيل في الخلفية، تقدم، إلغاء، صلاحيات.</summary>
public abstract class ToolPage : ObservableObject
{
    public MainViewModel Main => MainViewModel.I;
    private bool _busy;
    public bool IsBusy { get => _busy; protected set { Set(ref _busy, value); Raise(nameof(IsIdle)); } }
    public bool IsIdle => !_busy;
    private double _progress;
    public double ProgressPercent { get => _progress; protected set => Set(ref _progress, value); }
    private string _status = "";
    public string StatusText { get => _status; protected set => Set(ref _status, value); }
    private string _result = "";
    public string ResultText { get => _result; protected set { Set(ref _result, value); Raise(nameof(HasResult)); } }
    public bool HasResult => _result.Length > 0;
    public RelayCommand CancelCommand { get; }
    protected CancellationTokenSource? Cts;
    protected RecoveryProgress? P;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    protected ToolPage()
    {
        CancelCommand = new RelayCommand(() => Cts?.Cancel(), () => IsBusy);
        Main.CancelCommands.Add(CancelCommand);
        _timer.Tick += (_, _) => OnTick();
    }

    protected virtual void OnTick()
    {
        if (P == null) return;
        ProgressPercent = Math.Round(P.Fraction * 100, 1);
        StatusText = Loc.T(P.Phase) + (P.Found > 0 ? "  —  " + Loc.F("rec.found", P.Found) : "");
    }

    protected bool EnsureAdmin(string page)
    {
        if (Main.IsElevated) return true;
        if (TestExecution.Ask(Loc.T("common.adminRequired") + "\n\n" + Loc.T("common.restartAdmin") + "?"))
            Shell.RestartElevated($"--page {page}" + (Main.SelectedDrive is { } d ? $" --disk {d.Drive.DiskNumber}" : ""));
        return false;
    }

    /// <summary>ينفذ عملاً في الخلفية مع التقدم والإلغاء وتحويل الأخطاء إلى رسائل.</summary>
    protected async Task<T?> RunAsync<T>(Func<RecoveryProgress, CancellationToken, T> work) where T : class
    {
        Cts = new CancellationTokenSource();
        P = new RecoveryProgress();
        IsBusy = true;
        Main.IsBusy = true;
        ResultText = "";
        ProgressPercent = 0;
        _timer.Start();
        try
        {
            var p = P;
            var ct = Cts.Token;
            return await Task.Run(() => work(p, ct));
        }
        catch (SafetyException ex) { TestExecution.Msg(Loc.T(ex.ReasonKey), true); }
        catch (IdentityChangedException) { TestExecution.Msg(Loc.T("scan.identityChanged"), true); }
        catch (VolumeInUseException ex) { TestExecution.Msg(Loc.F("scan.volumeInUse", ex.Volume), true); }
        catch (UnauthorizedAccessException) { TestExecution.Msg(Loc.T("common.adminRequired"), true); }
        catch (Exception ex) { TestExecution.Msg(Loc.F("scan.failedStart", ex.Message), true); }
        finally
        {
            _timer.Stop();
            OnTick();
            IsBusy = false;
            Main.IsBusy = false;
        }
        return null;
    }

    protected static bool ConfirmTyped(DriveSnapshot d, string title, string message)
    {
        var dlg = new ConfirmDialog($"{d.DisplayName}  {d.Letters}  —  {Fmt.Bytes(d.DeclaredBytes)}", title, message, Fmt.Bytes(d.DeclaredBytes), danger: true)
        {
            Owner = Application.Current.MainWindow,
        };
        return dlg.ShowDialog() == true;
    }

    protected void Done(string text)
    {
        ResultText = text;
        Main.Notify?.Show("IBRA DRIVE CHECK", text.Split('\n')[0]);
    }
}

// ═════════════════════════ اختبار التحمل ═════════════════════════
public sealed class BurnInViewModel : ToolPage
{
    public static BurnInViewModel I { get; } = new();
    public ObservableCollection<VolumeOption> Volumes { get; } = new();
    private VolumeOption? _vol;
    public VolumeOption? SelectedVolume { get => _vol; set => Set(ref _vol, value); }
    public List<int> Hours { get; } = new() { 1, 2, 4, 8, 12, 24 };
    private int _hours = 2;
    public int SelectedHours { get => _hours; set => Set(ref _hours, value); }
    public List<int> SizesGb { get; } = new() { 1, 4, 8, 16, 32, 64 };
    private int _size = 4;
    public int SelectedSizeGb { get => _size; set => Set(ref _size, value); }
    public ObservableCollection<string> PassLines { get; } = new();
    private List<double>? _r, _w;
    public List<double>? ReadSamples { get => _r; set => Set(ref _r, value); }
    public List<double>? WriteSamples { get => _w; set => Set(ref _w, value); }
    private int _ver;
    public int ChartVersion { get => _ver; set => Set(ref _ver, value); }
    public RelayCommand StartCommand { get; }
    private ScanSession? _session;
    private DateTime _started;

    private BurnInViewModel()
    {
        StartCommand = new RelayCommand(() => _ = StartAsync(), () => IsIdle && Main.IsIdle && SelectedVolume != null);
    }

    public void Load()
    {
        if (IsBusy) return;
        Volumes.Clear();
        foreach (var v in VolumeOption.From(Main.Drives, false).Where(v => !v.IsSystem)) Volumes.Add(v);
        SelectedVolume = Volumes.FirstOrDefault(v => v.Drive.HardwareKey == Main.SelectedDrive?.Drive.HardwareKey) ?? Volumes.FirstOrDefault();
    }

    protected override void OnTick()
    {
        if (_session == null || !IsBusy) { base.OnTick(); return; }
        double total = SelectedHours * 3600.0;
        ProgressPercent = Math.Round(Math.Min(100, (DateTime.UtcNow - _started).TotalSeconds / total * 100), 1);
        StatusText = Loc.F("burn.live", _session.StageIndex + 1, Fmt.Speed(_session.CurrentWriteMBps), Fmt.Speed(_session.CurrentReadMBps), Fmt.Duration(DateTime.UtcNow - _started));
    }

    private async Task StartAsync()
    {
        var v = SelectedVolume;
        if (v?.Volume == null) return;
        if (!TestExecution.Ask(Loc.F("burn.confirm", v.Letter!, SelectedHours))) return;
        PassLines.Clear();
        var reads = new List<double>();
        var writes = new List<double>();
        _session = new ScanSession();
        _started = DateTime.UtcNow;
        var d = v.Drive;
        var fs = new RealFileSpace(v.Letter + "\\", v.FileSystem);
        long bytes = SelectedSizeGb * 1_000_000_000L;
        int hours = SelectedHours;
        var session = _session;
        var dispatcher = Application.Current.Dispatcher;
        var res = await RunAsync((p, ct) =>
        {
            var test = new BurnInTest(fs, d, () => { try { return HealthReader.Read(d).TemperatureC; } catch { return null; } });
            return test.Run(TimeSpan.FromHours(hours), 100_000, bytes, session, pass => dispatcher.Invoke(() =>
            {
                PassLines.Insert(0, Loc.F("burn.passLine", pass.Index, Fmt.Speed(pass.WriteMBps), Fmt.Speed(pass.ReadMBps), pass.Errors, pass.TemperatureC is double t ? $"{t:0}°C" : "—"));
                writes.Add(pass.WriteMBps);
                reads.Add(pass.ReadMBps);
                WriteSamples = writes.ToList();
                ReadSamples = reads.ToList();
                ChartVersion++;
            }), ct);
        });
        _session = null;
        if (res == null) return;
        var r = new ScanResult
        {
            Kind = ScanKind.BurnIn, Drive = d, DeclaredBytes = d.DeclaredBytes, StartedUtc = _started, FinishedUtc = DateTime.UtcNow,
            Verdict = res.Verdict, Cancelled = res.Cancelled, DeviceDisconnected = res.Disconnected,
            AvgWriteMBps = res.Passes.Count > 0 ? Math.Round(res.Passes.Average(x => x.WriteMBps), 2) : 0,
            AvgReadMBps = res.Passes.Count > 0 ? Math.Round(res.Passes.Average(x => x.ReadMBps), 2) : 0,
            BytesTested = res.BytesPerPass * res.Passes.Count * 2,
        };
        r.Notes.AddRange(res.Notes);
        Main.AddResult(r);
        ResultText = Strings.Verdict(Loc.I.Lang, res.Verdict) + "\n" + string.Join("\n", res.Notes.Select(n => Strings.Format(Loc.I.Lang, n)));
    }
}

// ═════════════════════════ عمر الـ SSD ═════════════════════════
public sealed class LifeViewModel : ToolPage
{
    public static LifeViewModel I { get; } = new();
    public ObservableCollection<InfoRow> Rows { get; } = new();
    private string _big = "—", _years = "", _note = "";
    public string HealthText { get => _big; set => Set(ref _big, value); }
    public string YearsText { get => _years; set => Set(ref _years, value); }
    public string NoteText { get => _note; set => Set(ref _note, value); }
    private double _pct;
    public double HealthPercent { get => _pct; set => Set(ref _pct, value); }
    public RelayCommand ReadCommand { get; }

    private LifeViewModel()
    {
        ReadCommand = new RelayCommand(() => _ = ReadAsync(), () => IsIdle && Main.SelectedDrive != null);
        Main.SelectedDriveChanged += () => { if (IsIdle) _ = ReadAsync(); };
        Loc.I.LanguageChanged += () => { if (IsIdle) _ = ReadAsync(); };
    }

    public void ReadIfNeeded()
    {
        if (IsIdle && Rows.Count == 0) _ = ReadAsync();
    }

    public async Task ReadAsync()
    {
        var item = Main.SelectedDrive;
        if (item == null) return;
        var d = item.Drive;
        var h = item.Health ?? await RunAsync((p, ct) => { p.Phase = "health.read"; return HealthReader.Read(d); });
        if (h == null) return;
        item.Health = h;
        var spec = ModelDatabase.Match(d.Manufacturer, d.Model);
        var obs = SsdLife.Observe(d.HardwareKey, h);
        var e = SsdLife.Estimate(h, d.DeclaredBytes, spec, obs, DateTime.UtcNow);
        string NA = Loc.T("common.notAvailable");
        HealthPercent = e.HealthPercent ?? 0;
        HealthText = e.HealthPercent is int hp ? $"{hp}%" : NA;
        YearsText = e.YearsLeft is double y ? Loc.F("life.years", y < 1 ? (y * 12).ToString("0") + " " + Loc.T("life.months") : y.ToString("0.#")) : Loc.T("life.yearsUnknown");
        Rows.Clear();
        Rows.Add(new InfoRow(Loc.T("info.model"), d.Model ?? NA));
        Rows.Add(new InfoRow(Loc.T("life.spec"), spec?.Name ?? Loc.T("life.noSpec")));
        Rows.Add(new InfoRow(Loc.T("life.tbw"), e.TbwRated is int t ? $"{t} TBW" : NA));
        Rows.Add(new InfoRow(Loc.T("health.written"), e.WrittenBytes is long w ? Fmt.Bytes(w) : NA, true));
        Rows.Add(new InfoRow(Loc.T("health.percentUsed"), h.PercentUsed is int pu ? $"{pu}%" : h.WearPercent is int wp ? $"{wp}%" : NA));
        Rows.Add(new InfoRow(Loc.T("health.hours"), h.PowerOnHours?.ToString() ?? NA));
        Rows.Add(new InfoRow(Loc.T("life.perDay"), e.GbPerDay is double g ? $"{g:0.#} GB" : NA));
        Rows.Add(new InfoRow(Loc.T("health.spare"), h.AvailableSpare is int sp ? $"{sp}%" : NA));
        NoteText = string.Join("\n", e.BasisKey.Split('|').Select(Loc.T)) + "\n" + Loc.T("life.disclaimer");
    }
}

// ═════════════════════════ المراقبة بالخلفية ═════════════════════════
public sealed class MonitorViewModel : ObservableObject
{
    public static MonitorViewModel I { get; } = new();
    public MainViewModel Main => MainViewModel.I;
    public ObservableCollection<MonitorEntry> Entries => MonitorService.I.Entries;
    public bool Enabled { get => Main.Settings.MonitorEnabled; set { Main.Settings.MonitorEnabled = value; Save(); } }
    public bool StartWithWindows { get => Main.Settings.StartWithWindows; set { Main.Settings.StartWithWindows = value; Save(); } }
    public List<int> Limits { get; } = new() { 50, 55, 60, 65, 70, 75 };
    public int TempLimit { get => Main.Settings.TempLimit; set { Main.Settings.TempLimit = value; Save(); } }
    public List<int> Intervals { get; } = new() { 1, 5, 10, 30, 60 };
    public int Interval { get => Main.Settings.MonitorMinutes; set { Main.Settings.MonitorMinutes = value; Save(); } }
    public RelayCommand CheckCommand { get; } = new(() => _ = MonitorService.I.PollAsync());

    private void Save()
    {
        Main.SaveSettings();
        MonitorService.I.Apply();
        RaiseAll();
    }
}

// ═════════════════════════ مقارنة الموديل ═════════════════════════
public sealed class CompareViewModel : ObservableObject
{
    public static CompareViewModel I { get; } = new();
    public MainViewModel Main => MainViewModel.I;
    public ObservableCollection<InfoRow> Rows { get; } = new();
    private string _hint = "";
    public string HintText { get => _hint; set => Set(ref _hint, value); }
    private double _readPct, _writePct;
    public double ReadPercent { get => _readPct; set => Set(ref _readPct, value); }
    public double WritePercent { get => _writePct; set => Set(ref _writePct, value); }
    private string _readTxt = "", _writeTxt = "";
    public string ReadText { get => _readTxt; set => Set(ref _readTxt, value); }
    public string WriteText { get => _writeTxt; set => Set(ref _writeTxt, value); }
    public RelayCommand SpeedCommand { get; } = new(() => MainViewModel.I.Navigate("speed"));

    private CompareViewModel()
    {
        Main.SelectedDriveChanged += Refresh;
        Loc.I.LanguageChanged += Refresh;
        Main.History.CollectionChanged += (_, _) => Refresh();
    }

    public void Refresh()
    {
        Rows.Clear();
        var item = Main.SelectedDrive;
        if (item == null) return;
        var d = item.Drive;
        string NA = Loc.T("common.notAvailable");
        var spec = ModelDatabase.Match(d.Manufacturer, d.Model);
        var speed = Main.History.FirstOrDefault(h => h.Drive.HardwareKey == d.HardwareKey && h.Speed is { SeqRead.Measured: true });
        double r = speed?.Speed?.SeqRead.AvgMBps ?? 0, w = speed?.Speed?.SeqWrite.Measured == true ? speed.Speed.SeqWrite.AvgMBps : 0;
        Rows.Add(new InfoRow(Loc.T("info.model"), $"{d.Manufacturer} {d.Model}".Trim()));
        Rows.Add(new InfoRow(Loc.T("compare.match"), spec?.Name ?? Loc.T("compare.noMatch"), true));
        Rows.Add(new InfoRow(Loc.T("compare.iface"), spec?.Interface ?? NA));
        Rows.Add(new InfoRow(Loc.T("info.bus"), d.BusType ?? NA));
        Rows.Add(new InfoRow(Loc.T("compare.ratedRead"), spec != null ? $"{spec.ReadMBps} MB/s" : NA));
        Rows.Add(new InfoRow(Loc.T("compare.ratedWrite"), spec != null ? $"{spec.WriteMBps} MB/s" : NA));
        Rows.Add(new InfoRow(Loc.T("compare.measuredRead"), r > 0 ? Fmt.Speed(r) : Loc.T("compare.noSpeed")));
        Rows.Add(new InfoRow(Loc.T("compare.measuredWrite"), w > 0 ? Fmt.Speed(w) : Loc.T("compare.noSpeed")));
        ReadPercent = spec != null && r > 0 ? Math.Min(100, r / spec.ReadMBps * 100) : 0;
        WritePercent = spec != null && w > 0 ? Math.Min(100, w / spec.WriteMBps * 100) : 0;
        ReadText = spec != null && r > 0 ? $"{ReadPercent:0}%" : "—";
        WriteText = spec != null && w > 0 ? $"{WritePercent:0}%" : "—";

        var hints = new List<string>();
        if (spec == null) hints.Add(Loc.T("compare.hintUnknown"));
        else if (r > 0)
        {
            if (d.BusType == "USB" && r < 45) hints.Add(Loc.T("compare.hintUsb2"));
            else if (d.BusType == "USB" && spec.Interface.StartsWith("NVMe") && r < 1100) hints.Add(Loc.T("compare.hintEnclosure"));
            else if (r < spec.ReadMBps * 0.5) hints.Add(Loc.T("compare.hintSlow"));
            else hints.Add(Loc.T("compare.hintOk"));
        }
        else hints.Add(Loc.T("compare.hintRun"));
        HintText = string.Join("\n", hints);
    }
}

// ═════════════════════════ إنقاذ وحدة لا تفتح ═════════════════════════
public sealed class RescueCandidateRow
{
    public FsCandidate C { get; init; } = new();
    public DriveSnapshot Drive { get; init; } = new();
    public string Title => $"{C.FileSystem}  —  {Fmt.Bytes(C.Size)}";
    public string Detail => $"{Loc.T("rescue.offset")}: {Fmt.Bytes(C.Offset)}  ·  {Loc.T("rescue.boot." + C.Boot)}" + (C.InPartitionTable ? "" : "  ·  " + Loc.T("rescue.notInTable"));
    public bool Repairable => RescueEngine.IsRepairable(C);
}

public sealed class RescueViewModel : ToolPage
{
    public static RescueViewModel I { get; } = new();
    public ObservableCollection<DriveItem> Drives { get; } = new();
    private DriveItem? _drive;
    public DriveItem? SelectedDrive { get => _drive; set => Set(ref _drive, value); }
    public ObservableCollection<RescueCandidateRow> Candidates { get; } = new();
    private RescueCandidateRow? _sel;
    public RescueCandidateRow? SelectedCandidate { get => _sel; set => Set(ref _sel, value); }
    public RelayCommand SearchCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand RepairCommand { get; }

    private RescueViewModel()
    {
        SearchCommand = new RelayCommand(() => _ = SearchAsync(), () => IsIdle && Main.IsIdle && SelectedDrive != null);
        OpenCommand = new RelayCommand(Open, () => IsIdle && SelectedCandidate != null && SelectedCandidate.C.FileSystem != "?");
        RepairCommand = new RelayCommand(() => _ = RepairAsync(), () => IsIdle && SelectedCandidate?.Repairable == true);
    }

    public void Load()
    {
        if (IsBusy) return;
        var keep = SelectedDrive?.Drive.HardwareKey;
        Drives.Clear();
        foreach (var d in Main.Drives.Where(d => !d.Drive.IsSystemDisk)) Drives.Add(d);
        // الوحدات التي "تطلب تهيئة" (قسم بدون نظام ملفات) أولاً
        SelectedDrive = Drives.FirstOrDefault(d => d.Drive.HardwareKey == keep)
                        ?? Drives.FirstOrDefault(d => d.Drive.Volumes.Count == 0 || d.Drive.Volumes.Any(v => v.FileSystem is "" or "RAW"))
                        ?? Drives.FirstOrDefault();
    }

    private async Task SearchAsync()
    {
        var d = SelectedDrive?.Drive;
        if (d == null || !EnsureAdmin("rescue")) return;
        Candidates.Clear();
        var list = await RunAsync((p, ct) =>
        {
            using var disk = VolumeTools.OpenDiskSource(d.DiskNumber);
            return RescueEngine.Find(disk, p, Math.Min(disk.Size, 1L << 30), ct);
        });
        if (list == null) return;
        foreach (var c in list) Candidates.Add(new RescueCandidateRow { C = c, Drive = d });
        SelectedCandidate = Candidates.FirstOrDefault(c => c.C.FileSystem != "?");
        ResultText = list.Count == 0 ? Loc.T("rescue.none") : Loc.F("rescue.foundCount", list.Count(c => c.FileSystem != "?"));
    }

    private void Open()
    {
        var c = SelectedCandidate;
        if (c == null) return;
        Main.Navigate("recovery");
        RecoveryViewModel.I.OpenRescue(new VolumeOption { Drive = c.Drive, Candidate = c.C });
    }

    private async Task RepairAsync()
    {
        var c = SelectedCandidate;
        if (c == null || !EnsureAdmin("rescue")) return;
        if (!TestExecution.Ask(Loc.T("rescue.repairConfirm"))) return;
        var d = c.Drive;
        var ok = await RunAsync((p, ct) =>
        {
            if (d.IsSystemDisk) throw new SafetyException("safety.systemDisk");
            var fresh = DriveEnumerator.Query(d.DiskNumber);
            if (fresh == null || fresh.IdentityKey != d.IdentityKey) throw new IdentityChangedException();
            int bytes = c.C.BackupSectors * 512;
            using var locker = VolumeLocker.LockDisk(d.DiskNumber);
            using var dev = RawDiskDevice.Open(d.DiskNumber, write: true);
            int align = Math.Max(4096, dev.Alignment);
            long start = c.C.Offset / align * align;
            int len = (int)((c.C.Offset + bytes - start + align - 1) / align * align);
            using var buf = new IbraDriveCheck.Core.Devices.AlignedBuffer(len, align);
            dev.Read(start, buf.Span);
            // نسخة من القطاعات الحالية قبل الكتابة
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllBytes(Path.Combine(AppPaths.Root, $"boot_backup_{DateTime.Now:yyyyMMdd_HHmmss}_disk{d.DiskNumber}_{c.C.Offset}.bin"), buf.Span.ToArray());
            using var src = new BlockVolumeSource(dev, ownsDevice: false);
            var backup = new byte[bytes];
            src.ReadAt(c.C.BackupOffset, backup);
            backup.CopyTo(buf.Span[(int)(c.C.Offset - start)..]);
            dev.Write(start, buf.Span);
            dev.Flush();
            Native2.UpdateDisk(dev);
            return "ok";
        });
        if (ok != null)
        {
            Done(Loc.T("rescue.repaired"));
            await Main.RefreshDrivesAsync(false);
        }
    }
}

internal static class Native2
{
    public static void UpdateDisk(RawDiskDevice dev) { try { dev.UpdateProperties(); } catch { } }
}

// ═════════════════════════ الملفات التالفة ═════════════════════════
public sealed class DamagedRow
{
    public string Path { get; init; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
    public string Reason { get; init; } = "";
    public string Size { get; init; } = "";
}

public sealed class DamagedViewModel : ToolPage
{
    public static DamagedViewModel I { get; } = new();
    public ObservableCollection<VolumeOption> Volumes { get; } = new();
    private VolumeOption? _vol;
    public VolumeOption? SelectedVolume { get => _vol; set { Set(ref _vol, value); if (value?.Letter != null) Folder = value.Letter + "\\"; } }
    private string _folder = "";
    public string Folder { get => _folder; set => Set(ref _folder, value); }
    public ObservableCollection<DamagedRow> Damaged { get; } = new();
    private DamagedRow? _sel;
    public DamagedRow? Selected { get => _sel; set => Set(ref _sel, value); }
    public RelayCommand ScanCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand ExportCommand { get; }

    private DamagedViewModel()
    {
        ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => IsIdle && Directory.Exists(Folder));
        BrowseCommand = new RelayCommand(() =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog();
            if (dlg.ShowDialog() == true) Folder = dlg.FolderName;
        });
        OpenCommand = new RelayCommand(() => { if (Selected != null) Shell.Open("explorer.exe", $"/select,\"{Selected.Path}\""); }, () => Selected != null);
        ExportCommand = new RelayCommand(Export, () => Damaged.Count > 0);
    }

    public void Load()
    {
        if (IsBusy) return;
        var keep = SelectedVolume?.Letter;
        Volumes.Clear();
        foreach (var v in VolumeOption.From(Main.Drives, false)) Volumes.Add(v);
        SelectedVolume = Volumes.FirstOrDefault(v => v.Letter == keep) ?? Volumes.FirstOrDefault(v => !v.IsSystem) ?? Volumes.FirstOrDefault();
    }

    private async Task ScanAsync()
    {
        Damaged.Clear();
        string root = Folder;
        var res = await RunAsync((p, ct) => DamagedFileChecker.Scan(root, p, ct));
        if (res == null) return;
        foreach (var d in res.Damaged) Damaged.Add(new DamagedRow { Path = d.Path, Reason = Loc.T(d.ReasonKey), Size = Fmt.Bytes(d.Size) });
        Done(Loc.F("damaged.summary", res.Checked, res.Damaged.Count) + (res.Damaged.Any(x => x.ReasonKey == "damaged.zeros") ? "\n" + Loc.T("damaged.zerosHint") : ""));
    }

    private void Export()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "damaged-files.csv", Filter = "CSV|*.csv" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, "﻿" + string.Join("\r\n", Damaged.Select(d => $"\"{d.Path}\",\"{d.Reason}\",\"{d.Size}\"")), System.Text.Encoding.UTF8);
    }
}

// ═════════════════════════ نسخ القرص ═════════════════════════
public sealed class CloneViewModel : ToolPage
{
    public static CloneViewModel I { get; } = new();
    private string _folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public string Folder { get => _folder; set => Set(ref _folder, value); }
    private bool _vhd = true;
    public bool AsVhd { get => _vhd; set { Set(ref _vhd, value); Raise(nameof(AsImg)); } }
    public bool AsImg { get => !_vhd; set => AsVhd = !value; }
    private string _image = "";
    public string ImagePath { get => _image; set => Set(ref _image, value); }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand CreateCommand { get; }
    public RelayCommand PickImageCommand { get; }
    public RelayCommand RestoreCommand { get; }

    private CloneViewModel()
    {
        BrowseCommand = new RelayCommand(() =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog();
            if (dlg.ShowDialog() == true) Folder = dlg.FolderName;
        });
        CreateCommand = new RelayCommand(() => _ = CreateAsync(), () => IsIdle && Main.IsIdle && Main.SelectedDrive != null);
        PickImageCommand = new RelayCommand(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "VHD / IMG|*.vhd;*.img;*.bin|*.*|*.*" };
            if (dlg.ShowDialog() == true) ImagePath = dlg.FileName;
        });
        RestoreCommand = new RelayCommand(() => _ = RestoreAsync(), () => IsIdle && Main.IsIdle && Main.SelectedDrive != null && File.Exists(ImagePath));
    }

    private async Task CreateAsync()
    {
        var d = Main.SelectedDrive?.Drive;
        if (d == null || !EnsureAdmin("clone")) return;
        string root = (Path.GetPathRoot(Path.GetFullPath(Folder)) ?? "").TrimEnd('\\').ToUpperInvariant();
        if (d.Volumes.Any(v => v.Letter.ToUpperInvariant() == root)) { TestExecution.Msg(Loc.T("clone.sameDrive"), true); return; }
        try
        {
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < d.DeclaredBytes + (64L << 20)) { TestExecution.Msg(Loc.F("clone.noSpace", Fmt.Bytes(d.DeclaredBytes), Fmt.Bytes(free)), true); return; }
        }
        catch { }
        bool vhd = AsVhd && d.DeclaredBytes <= DiskImager.MaxVhdBytes;
        string name = new string(d.DisplayName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '_' : ch).ToArray());
        string path = Path.Combine(Folder, $"{name}_{DateTime.Now:yyyyMMdd_HHmm}.{(vhd ? "vhd" : "img")}");
        var res = await RunAsync((p, ct) =>
        {
            using var src = VolumeTools.OpenDiskSource(d.DiskNumber);
            return DiskImager.Create(src, path, vhd, p, ct);
        });
        if (res == null) return;
        if (res.Cancelled) { ResultText = Loc.T("note.cancelled"); return; }
        Done(Loc.F("clone.done", res.Path, Fmt.Bytes(res.Bytes)) + (res.BadSectors > 0 ? "\n" + Loc.F("clone.bad", res.BadSectors) : "") + (res.Vhd ? "\n" + Loc.T("clone.vhdHint") : ""));
        Shell.Open("explorer.exe", $"/select,\"{res.Path}\"");
    }

    private async Task RestoreAsync()
    {
        var d = Main.SelectedDrive?.Drive;
        if (d == null || !EnsureAdmin("clone")) return;
        var dec = SecurityTools.CanErase(d);
        if (!dec.Allowed) { TestExecution.Msg(Loc.T(dec.ReasonKey), true); return; }
        long size = DiskImager.DataSize(ImagePath);
        if (size > d.DeclaredBytes) { TestExecution.Msg(Loc.T("clone.tooSmall"), true); return; }
        if (!ConfirmTyped(d, Loc.T("clone.restoreTitle"), Loc.F("clone.restoreConfirm", Path.GetFileName(ImagePath)))) return;
        string img = ImagePath;
        var res = await RunAsync((p, ct) =>
        {
            var fresh = DriveEnumerator.Query(d.DiskNumber);
            if (fresh == null || fresh.IdentityKey != d.IdentityKey) throw new IdentityChangedException();
            using var locker = VolumeLocker.LockDisk(d.DiskNumber);
            using var dev = RawDiskDevice.Open(d.DiskNumber, write: true);
            long n = DiskImager.Restore(img, dev, p, ct);
            try { dev.UpdateProperties(); } catch { }
            return (object)n;
        });
        if (res != null) Done(Loc.F("clone.restored", Fmt.Bytes((long)res)));
        await Main.RefreshDrivesAsync(false);
    }
}

// ═════════════════════════ الحماية من الفيروسات ═════════════════════════
public sealed class ThreatRow
{
    public Threat T { get; init; } = new();
    public string Name => Path.GetFileName(T.Path.TrimEnd('\\'));
    public string Kind => Loc.T("guard.kind." + T.Kind);
    public string Detail => T.Detail ?? "";
}

public sealed class GuardViewModel : ToolPage
{
    public static GuardViewModel I { get; } = new();
    public ObservableCollection<VolumeOption> Volumes { get; } = new();
    private VolumeOption? _vol;
    public VolumeOption? SelectedVolume { get => _vol; set => Set(ref _vol, value); }
    public ObservableCollection<ThreatRow> Threats { get; } = new();
    private bool _vaccinate = true;
    public bool Vaccinate { get => _vaccinate; set => Set(ref _vaccinate, value); }
    private string _state = "";
    public string StateText { get => _state; set => Set(ref _state, value); }
    public RelayCommand ScanCommand { get; }
    public RelayCommand CleanCommand { get; }
    public RelayCommand VaccinateCommand { get; }
    public RelayCommand QuarantineCommand { get; } = new(() => { Directory.CreateDirectory(Path.Combine(AppPaths.Root, "quarantine")); Shell.Open(Path.Combine(AppPaths.Root, "quarantine")); });
    private GuardResult? _scan;

    private GuardViewModel()
    {
        ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => IsIdle && SelectedVolume?.Letter != null);
        CleanCommand = new RelayCommand(() => _ = CleanAsync(), () => IsIdle && _scan != null && (Threats.Count > 0 || Vaccinate && !_scan.Vaccinated));
        VaccinateCommand = new RelayCommand(() =>
        {
            if (SelectedVolume?.Letter == null) return;
            ResultText = UsbGuard.Vaccinate(SelectedVolume.Letter + "\\") ? Loc.T("guard.vaccinated") : Loc.T("guard.vaccineFail");
        }, () => IsIdle && SelectedVolume?.Letter != null);
    }

    public void Load()
    {
        if (IsBusy) return;
        var keep = SelectedVolume?.Letter;
        Volumes.Clear();
        foreach (var v in VolumeOption.From(Main.Drives, false).Where(v => !v.IsSystem)) Volumes.Add(v);
        SelectedVolume = Volumes.FirstOrDefault(v => v.Letter == keep) ?? Volumes.FirstOrDefault(v => v.Drive.IsExternalBus) ?? Volumes.FirstOrDefault();
    }

    private async Task ScanAsync()
    {
        string root = SelectedVolume!.Letter + "\\";
        Threats.Clear();
        _scan = await RunAsync((p, ct) => { p.Phase = "guard.phase"; return UsbGuard.Scan(root); });
        if (_scan == null) return;
        foreach (var t in _scan.Threats) Threats.Add(new ThreatRow { T = t });
        StateText = (_scan.Threats.Count == 0 ? Loc.T("guard.clean") : Loc.F("guard.foundCount", _scan.Threats.Count))
                    + "\n" + (_scan.Vaccinated ? Loc.T("guard.isVaccinated") : Loc.T("guard.notVaccinated"));
    }

    private async Task CleanAsync()
    {
        if (_scan == null) return;
        string root = SelectedVolume!.Letter + "\\";
        var scan = _scan;
        bool vac = Vaccinate;
        var res = await RunAsync((p, ct) => { p.Phase = "guard.phaseClean"; return UsbGuard.Clean(root, scan, vac); });
        if (res == null) return;
        Done(Loc.F("guard.cleaned", res.Quarantined, res.Unhidden) + (res.Vaccinated ? "\n" + Loc.T("guard.vaccinated") : "") + (res.Errors > 0 ? "\n" + Loc.F("rec.failed", res.Errors) : ""));
        _scan = null;
        await ScanAsync();
    }
}

// ═════════════════════════ قفل الكتابة ═════════════════════════
public sealed class WriteLockViewModel : ToolPage
{
    public static WriteLockViewModel I { get; } = new();
    public string DriveState => Main.SelectedDrive?.Drive.WriteProtected switch
    {
        true => Loc.T("info.wpOn"),
        false => Loc.T("info.wpOff"),
        _ => Loc.T("common.notAvailable"),
    };
    private bool _policy;
    public string PolicyState => _policy ? Loc.T("lock.policyOn") : Loc.T("lock.policyOff");
    public RelayCommand LockCommand { get; }
    public RelayCommand UnlockCommand { get; }
    public RelayCommand PolicyOnCommand { get; }
    public RelayCommand PolicyOffCommand { get; }

    private WriteLockViewModel()
    {
        LockCommand = new RelayCommand(() => _ = SetAsync(true), () => IsIdle && Main.IsIdle && Main.SelectedDrive != null && !Main.SelectedDrive.Drive.IsSystemDisk);
        UnlockCommand = new RelayCommand(() => _ = SetAsync(false), () => IsIdle && Main.IsIdle && Main.SelectedDrive != null && !Main.SelectedDrive.Drive.IsSystemDisk);
        PolicyOnCommand = new RelayCommand(() => SetPolicy(true), () => IsIdle);
        PolicyOffCommand = new RelayCommand(() => SetPolicy(false), () => IsIdle);
        Main.SelectedDriveChanged += () => Raise(nameof(DriveState));
        Loc.I.LanguageChanged += () => RaiseAll();
    }

    public void Load()
    {
        try { _policy = SecurityTools.GetUsbWritePolicy(); } catch { }
        RaiseAll();
    }

    private async Task SetAsync(bool on)
    {
        var d = Main.SelectedDrive?.Drive;
        if (d == null || !EnsureAdmin("writelock")) return;
        var res = await RunAsync((p, ct) => { p.Phase = "lock.phase"; return SecurityTools.SetDiskReadOnly(d, on); });
        if (res == null) return;
        await Main.RefreshDrivesAsync(false);
        Raise(nameof(DriveState));
        Done(on ? Loc.T("lock.locked") : Loc.T("lock.unlocked"));
    }

    private void SetPolicy(bool on)
    {
        if (!EnsureAdmin("writelock")) return;
        try
        {
            SecurityTools.SetUsbWritePolicy(on);
            _policy = on;
            Raise(nameof(PolicyState));
            Done(on ? Loc.T("lock.policySetOn") : Loc.T("lock.policySetOff"));
        }
        catch (Exception ex) { TestExecution.Msg(ex.Message, true); }
    }
}

// ═════════════════════════ المسح الكامل قبل البيع ═════════════════════════
public sealed class EraseViewModel : ToolPage
{
    public static EraseViewModel I { get; } = new();
    private bool _three;
    public bool ThreePasses { get => _three; set => Set(ref _three, value); }
    private bool _builtIn = true;
    public bool TryBuiltIn { get => _builtIn; set => Set(ref _builtIn, value); }
    public bool IsNvme => Main.SelectedDrive?.Drive.BusType == "NVMe";
    private bool _format = true;
    public bool FormatAfter { get => _format; set => Set(ref _format, value); }
    public string? BlockedText => Main.SelectedDrive?.Drive is { } d && SecurityTools.CanErase(d) is { Allowed: false } dec ? Loc.T(dec.ReasonKey) : null;
    public RelayCommand EraseCommand { get; }

    private EraseViewModel()
    {
        EraseCommand = new RelayCommand(() => _ = EraseAsync(), () => IsIdle && Main.IsIdle && Main.SelectedDrive != null && BlockedText == null);
        Main.SelectedDriveChanged += () => { Raise(nameof(IsNvme)); Raise(nameof(BlockedText)); };
    }

    private async Task EraseAsync()
    {
        var d = Main.SelectedDrive?.Drive;
        if (d == null || !EnsureAdmin("erase")) return;
        if (!ConfirmTyped(d, Loc.T("tab.erase"), Loc.T("erase.confirm"))) return;
        int passes = ThreePasses ? 3 : 1;
        bool builtIn = TryBuiltIn && IsNvme, format = FormatAfter;
        var res = await RunAsync((p, ct) => SecurityTools.Erase(d, passes, builtIn, p, ct));
        if (res == null) return;
        string text = res.Cancelled ? Loc.T("note.cancelled") : Loc.F("erase.done", Fmt.Bytes(res.BytesWritten), res.Passes) + "\n" + (res.Verified ? Loc.T("erase.verified") : Loc.T("erase.notVerified"));
        if (res.UsedBuiltIn) text += "\n" + Loc.T("erase.builtInUsed");
        if (res.WriteErrors > 0) text += "\n" + Loc.F("erase.errors", res.WriteErrors);
        if (!res.Cancelled && format)
        {
            var part = await RunAsync((p, ct) => { p.Phase = "tools.working"; return VolumeTools.Repartition(DriveEnumerator.Query(d.DiskNumber) ?? d, null, "exFAT", "IBRA"); });
            if (part?.Ok == true) text += "\n" + Loc.F("tools.done", part.Letter ?? "?", Fmt.Bytes(part.PartitionBytes), part.FileSystem ?? "exFAT");
        }
        Done(text);
        await Main.RefreshDrivesAsync(false);
    }
}

// ═════════════════════════ الإخراج الآمن ═════════════════════════
public sealed class LockerRow
{
    public LockerInfo L { get; init; } = new();
    public string Title => L.Pid > 0 ? $"{L.Name}  (PID {L.Pid})" : L.Name;
    public string Reason => Loc.T(L.Reason);
}

public sealed class EjectViewModel : ToolPage
{
    public static EjectViewModel I { get; } = new();
    public ObservableCollection<LockerRow> Lockers { get; } = new();
    private LockerRow? _sel;
    public LockerRow? Selected { get => _sel; set => Set(ref _sel, value); }
    public RelayCommand EjectCommand { get; }
    public RelayCommand WhoCommand { get; }
    public RelayCommand CloseWindowsCommand { get; }
    public RelayCommand EndProcessCommand { get; }

    private EjectViewModel()
    {
        EjectCommand = new RelayCommand(() => _ = EjectAsync(), () => IsIdle && Main.IsIdle && Main.SelectedDrive is { Drive.IsSystemDisk: false });
        WhoCommand = new RelayCommand(() => _ = WhoAsync(), () => IsIdle && Main.SelectedDrive is { Drive.IsSystemDisk: false });
        CloseWindowsCommand = new RelayCommand(() =>
        {
            if (Main.SelectedDrive == null) return;
            int n = SecurityTools.CloseExplorerWindows(Main.SelectedDrive.Drive);
            ResultText = Loc.F("eject.closed", n);
            _ = WhoAsync();
        }, () => IsIdle && Lockers.Any(l => l.L.Pid == -1));
        EndProcessCommand = new RelayCommand(() =>
        {
            var l = Selected?.L;
            if (l == null || l.Pid <= 0) return;
            if (!TestExecution.Ask(Loc.F("eject.endConfirm", l.Name))) return;
            try { System.Diagnostics.Process.GetProcessById(l.Pid).Kill(); } catch (Exception ex) { TestExecution.Msg(ex.Message, true); }
            _ = WhoAsync();
        }, () => IsIdle && Selected?.L.Pid > 0);
    }

    private async Task WhoAsync()
    {
        var d = Main.SelectedDrive?.Drive;
        if (d == null) return;
        Lockers.Clear();
        var list = await RunAsync((p, ct) => { p.Phase = "eject.phaseWho"; return SecurityTools.FindLockers(d); });
        if (list == null) return;
        foreach (var l in list) Lockers.Add(new LockerRow { L = l });
        ResultText = list.Count == 0 ? Loc.T("eject.nobody") : Loc.F("eject.users", list.Count);
    }

    private async Task EjectAsync()
    {
        var d = Main.SelectedDrive?.Drive;
        if (d == null) return;
        Lockers.Clear();
        var res = await RunAsync((p, ct) => { p.Phase = "eject.phase"; return SecurityTools.Eject(d); });
        if (res == null) return;
        if (res.Ejected)
        {
            Done(Loc.F("eject.done", d.DisplayName));
            await Main.RefreshDrivesAsync(false);
            return;
        }
        ResultText = Loc.T("eject.vetoed") + (string.IsNullOrWhiteSpace(res.VetoName) ? "" : $"\n{res.VetoName}");
        await WhoAsync();
    }
}
