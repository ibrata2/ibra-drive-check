using System.Collections.ObjectModel;
using System.Windows.Threading;
using IbraDriveCheck.App.Infrastructure;
using IbraDriveCheck.Core.Devices.Windows;
using IbraDriveCheck.Core.Engine;
using IbraDriveCheck.Core.Localization;
using IbraDriveCheck.Core.Recovery;

namespace IbraDriveCheck.App.ViewModels;

/// <summary>إزالة آثار المحذوفات + الحذف الآمن.</summary>
public sealed class WipeViewModel : ObservableObject
{
    public static WipeViewModel I { get; } = new();
    public MainViewModel Main => MainViewModel.I;

    public ObservableCollection<VolumeOption> Volumes { get; } = new();
    private VolumeOption? _volume;
    public VolumeOption? SelectedVolume { get => _volume; set { Set(ref _volume, value); Raise(nameof(IsNtfs)); } }
    public bool IsNtfs => _volume?.FileSystem == "NTFS";

    private bool _names = true, _free = true, _usn;
    public bool ScrubNames { get => _names; set => Set(ref _names, value); }
    public bool WipeFree { get => _free; set => Set(ref _free, value); }
    public bool DeleteUsn { get => _usn; set => Set(ref _usn, value); }

    public ObservableCollection<string> ShredList { get; } = new();
    private string? _shredSel;
    public string? SelectedShred { get => _shredSel; set => Set(ref _shredSel, value); }
    private bool _threePasses;
    public bool ThreePasses { get => _threePasses; set => Set(ref _threePasses, value); }

    private bool _busy;
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); Raise(nameof(IsIdle)); } }
    public bool IsIdle => !_busy;
    private double _progress;
    public double ProgressPercent { get => _progress; private set => Set(ref _progress, value); }
    private string _status = "";
    public string StatusText { get => _status; private set => Set(ref _status, value); }
    private string _result = "";
    public string ResultText { get => _result; private set => Set(ref _result, value); }

    public RelayCommand StartCommand { get; }
    public RelayCommand VerifyCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand AddFilesCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ShredCommand { get; }

    private RecoveryProgress? _p;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _timer;

    private WipeViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Tick();
        StartCommand = new RelayCommand(() => _ = WipeAsync(), () => IsIdle && Main.IsIdle && SelectedVolume != null && (ScrubNames || WipeFree || DeleteUsn));
        VerifyCommand = new RelayCommand(() => _ = VerifyAsync(), () => IsIdle && Main.IsIdle && SelectedVolume != null);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        AddFilesCommand = new RelayCommand(AddFiles, () => IsIdle);
        AddFolderCommand = new RelayCommand(AddFolder, () => IsIdle);
        RemoveCommand = new RelayCommand(() => { if (SelectedShred != null) ShredList.Remove(SelectedShred); }, () => IsIdle && SelectedShred != null);
        ShredCommand = new RelayCommand(() => _ = ShredAsync(), () => IsIdle && ShredList.Count > 0);
        Loc.I.LanguageChanged += LoadVolumes;
    }

    public void LoadVolumes()
    {
        var keep = SelectedVolume?.Letter;
        Volumes.Clear();
        foreach (var v in VolumeOption.From(Main.Drives, includeWholeDisks: false)) Volumes.Add(v);
        SelectedVolume = Volumes.FirstOrDefault(v => v.Letter == keep) ?? Volumes.FirstOrDefault(v => !v.IsSystem) ?? Volumes.FirstOrDefault();
    }

    private bool EnsureAdmin()
    {
        if (Main.IsElevated) return true;
        if (TestExecution.Ask(Loc.T("common.adminRequired") + "\n\n" + Loc.T("common.restartAdmin") + "?"))
            Shell.RestartElevated("--page wipe");
        return false;
    }

    private async Task Run(Func<RecoveryProgress, CancellationToken, string> work)
    {
        _cts = new CancellationTokenSource();
        _p = new RecoveryProgress();
        IsBusy = true;
        Main.IsBusy = true;
        ResultText = "";
        _timer.Start();
        try
        {
            var p = _p;
            var ct = _cts.Token;
            ResultText = await Task.Run(() => work(p, ct));
            Main.Notify?.Show("IBRA DRIVE CHECK", ResultText);
        }
        catch (VolumeInUseException ex) { TestExecution.Msg(Loc.F("scan.volumeInUse", ex.Volume), true); }
        catch (Exception ex) { TestExecution.Msg(Loc.F("scan.failedStart", ex.Message), true); }
        finally
        {
            _timer.Stop();
            ProgressPercent = 100;
            StatusText = "";
            IsBusy = false;
            Main.IsBusy = false;
        }
    }

    private static int RemainingNames(string letter)
    {
        using var src = VolumeTools.OpenVolumeSource(letter);
        return RecoveryEngine.Scan(src, false, new RecoveryProgress(), CancellationToken.None).Items.Count(i => !i.IsDirectory);
    }

    private async Task VerifyAsync()
    {
        var v = SelectedVolume;
        if (v?.Letter == null || !EnsureAdmin()) return;
        await Run((p, ct) =>
        {
            p.Phase = "rec.phase.mft";
            using var src = VolumeTools.OpenVolumeSource(v.Letter);
            var scan = RecoveryEngine.Scan(src, false, p, ct);
            return Loc.F("wipe.verify", v.Letter, scan.Items.Count(i => !i.IsDirectory), scan.Items.Count(i => !i.IsDirectory && i.Chance == RecoveryChance.Good));
        });
    }

    private async Task WipeAsync()
    {
        var v = SelectedVolume;
        if (v?.Letter == null) return;
        if (v.IsSystem) { TestExecution.Msg(Loc.T("wipe.systemBlocked"), true); return; }
        if (ScrubNames && !EnsureAdmin()) return;
        if (!TestExecution.Ask(Loc.F("wipe.confirm", v.Letter))) return;
        string letter = v.Letter, fs = v.FileSystem;
        bool names = ScrubNames, free = WipeFree, usn = DeleteUsn && fs == "NTFS";
        await Run((p, ct) =>
        {
            int before = names ? RemainingNames(letter) : -1;
            var lines = new List<string>();
            if (names)
            {
                if (fs == "NTFS")
                {
                    long unused;
                    using (var src = VolumeTools.OpenVolumeSource(letter)) unused = new NtfsScanner(src, p).Scan(ct).UnusedMftRecords;
                    int made = TraceCleaner.FillMftRecords(letter + "\\", unused, p, ct);
                    lines.Add(Loc.F("wipe.mftDone", made));
                }
                else if (fs is "FAT32" or "FAT16" or "exFAT")
                {
                    p.Phase = "wipe.phase.names";
                    int n = VolumeTools.ScrubDeletedNames(letter, fs);
                    Thread.Sleep(1500);
                    lines.Add(Loc.F("wipe.namesDone", n));
                }
                else lines.Add(Loc.F("wipe.fsUnsupported", fs));
            }
            if (usn && !ct.IsCancellationRequested)
                lines.Add(VolumeTools.DeleteUsnJournal(letter) ? Loc.T("wipe.usnDone") : Loc.T("wipe.usnFail"));
            if (free && !ct.IsCancellationRequested)
            {
                long bytes = TraceCleaner.WipeFreeSpace(new RealFileSpace(letter + "\\", fs), p, ct);
                lines.Add(Loc.F("wipe.freeDone", Fmt.Bytes(bytes)));
            }
            if (ct.IsCancellationRequested) lines.Add(Loc.T("note.cancelled"));
            if (names && Main.IsElevated)
            {
                p.Phase = "rec.phase.mft";
                int after = RemainingNames(letter);
                lines.Add(Loc.F("wipe.beforeAfter", before, after));
            }
            lines.Add(Loc.T("wipe.ssdNote"));
            return string.Join("\n", lines);
        });
    }

    private void AddFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = Loc.T("wipe.addFiles") };
        if (dlg.ShowDialog() == true)
            foreach (var f in dlg.FileNames) if (!ShredList.Contains(f)) ShredList.Add(f);
    }

    private void AddFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("wipe.addFolder"), Multiselect = true };
        if (dlg.ShowDialog() == true)
            foreach (var f in dlg.FolderNames) if (!ShredList.Contains(f)) ShredList.Add(f);
    }

    private async Task ShredAsync()
    {
        var blocked = ShredList.Where(p => !TraceCleaner.IsShredAllowed(p)).ToList();
        if (blocked.Count > 0) { TestExecution.Msg(Loc.T("wipe.shredBlocked") + "\n" + string.Join("\n", blocked), true); return; }
        if (!TestExecution.Ask(Loc.F("wipe.shredConfirm", ShredList.Count))) return;
        var list = ShredList.ToList();
        int passes = ThreePasses ? 3 : 1;
        await Run((p, ct) =>
        {
            p.Phase = "wipe.phase.shred";
            int ok = 0, err = 0;
            foreach (var path in list)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    if (Directory.Exists(path)) { var (o, e) = TraceCleaner.ShredDirectory(path, passes, p, ct); ok += o; err += e; }
                    else if (File.Exists(path)) { TraceCleaner.ShredFile(path, passes); ok++; }
                }
                catch { err++; }
            }
            return Loc.F("wipe.shredDone", ok, err) + "\n" + Loc.T("wipe.ssdNote");
        });
        foreach (var path in list) if (!File.Exists(path) && !Directory.Exists(path)) ShredList.Remove(path);
    }

    private void Tick()
    {
        var p = _p;
        if (p == null) return;
        ProgressPercent = Math.Round(p.Fraction * 100, 1);
        StatusText = Loc.T(p.Phase);
    }
}
