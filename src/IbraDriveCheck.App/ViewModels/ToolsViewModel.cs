using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using IbraDriveCheck.App.Infrastructure;
using IbraDriveCheck.App.Views;
using IbraDriveCheck.Core.Devices.Windows;
using IbraDriveCheck.Core.Engine;
using IbraDriveCheck.Core.Localization;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.App.ViewModels;

/// <summary>إصلاح السعة المزيفة وتغيير حجم القسم: قسم واحد بالحجم الحقيقي (أو حجم يختاره المستخدم).</summary>
public sealed class ToolsViewModel : ObservableObject
{
    public static ToolsViewModel I { get; } = new();
    public MainViewModel Main => MainViewModel.I;

    private CapacityPlan? _plan;
    public string DeclaredText => Main.SelectedDrive is { } d ? Fmt.Bytes(d.Drive.DeclaredBytes) : "—";
    public string RealText => _plan == null ? "—" : _plan.Source is CapacitySource.None ? Loc.T("tools.noScan") : $"{Fmt.Bytes(_plan.RealBytes)}  ({Loc.T("tools.src." + _plan.Source)})";
    public string RecommendedText => _plan == null ? "—" : Fmt.Bytes(_plan.RecommendedBytes);
    public bool IsFake => _plan?.Source is CapacitySource.WrapExact or CapacitySource.LowerBound or CapacitySource.Verified;
    public bool NoScan => _plan?.Source == CapacitySource.None;

    private string _mode = "recommended";
    public bool ModeRecommended { get => _mode == "recommended"; set { if (value) { _mode = "recommended"; Changed(); } } }
    public bool ModeFull { get => _mode == "full"; set { if (value) { _mode = "full"; Changed(); } } }
    public bool ModeCustom { get => _mode == "custom"; set { if (value) { _mode = "custom"; Changed(); } } }

    private string _customGb = "";
    public string CustomGB { get => _customGb; set { Set(ref _customGb, value); Changed(); } }

    public ObservableCollection<string> FileSystems { get; } = new();
    private string _fs = "exFAT";
    public string SelectedFs { get => _fs; set => Set(ref _fs, value); }
    private string _label = "IBRA";
    public string Label { get => _label; set => Set(ref _label, value); }
    private bool _verify = true;
    public bool VerifyAfter { get => _verify; set => Set(ref _verify, value); }

    public string TargetText => TargetBytes() is long b ? Fmt.Bytes(b) : Loc.T("tools.invalidSize");
    public string? BlockedText
    {
        get
        {
            var d = Main.SelectedDrive?.Drive;
            if (d == null) return null;
            var dec = VolumeTools.CanRepartition(d);
            return dec.Allowed ? null : Loc.T(dec.ReasonKey);
        }
    }
    public bool IsBlocked => BlockedText != null;

    private bool _busy;
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); Raise(nameof(IsIdle)); } }
    public bool IsIdle => !_busy;
    private string _log = "";
    public string LogText { get => _log; private set => Set(ref _log, value); }

    public RelayCommand ApplyCommand { get; }

    private ToolsViewModel()
    {
        ApplyCommand = new RelayCommand(() => _ = ApplyAsync(), () => IsIdle && Main.IsIdle && Main.SelectedDrive != null && !IsBlocked && TargetBytes() != null);
        Main.SelectedDriveChanged += Refresh;
        Loc.I.LanguageChanged += () => RaiseAll();
        Refresh();
    }

    public void Refresh()
    {
        var d = Main.SelectedDrive?.Drive;
        _plan = d == null ? null : CapacityFixPlanner.Plan(d, Main.History);
        _mode = IsFake || NoScan ? "recommended" : "full";
        if (_plan != null)
            _customGb = (_plan.RecommendedBytes / 1e9).ToString("0.##", CultureInfo.InvariantCulture);
        Changed();
    }

    private void Changed()
    {
        var target = TargetBytes() ?? 0;
        var allowed = VolumeTools.FileSystemsFor(target);
        FileSystems.Clear();
        foreach (var f in allowed) FileSystems.Add(f);
        if (!allowed.Contains(_fs)) _fs = "exFAT";
        RaiseAll();
    }

    /// <summary>null للحجم الكامل (diskpart يأخذ كل المساحة)، أو عدد البايتات.</summary>
    private long? TargetBytes()
    {
        var d = Main.SelectedDrive?.Drive;
        if (d == null || _plan == null) return null;
        switch (_mode)
        {
            case "full":
                return CapacityFixPlanner.Floor(d.DeclaredBytes - CapacityFixPlanner.MiB);
            case "custom":
                if (!double.TryParse(_customGb.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) || gb <= 0.008) return null;
                long b = CapacityFixPlanner.Floor((long)(gb * 1e9));
                return b <= d.DeclaredBytes - CapacityFixPlanner.MiB ? b : null;
            default:
                return _plan.RecommendedBytes;
        }
    }

    private async Task ApplyAsync()
    {
        var item = Main.SelectedDrive;
        long? target = TargetBytes();
        if (item == null || target == null) return;
        var d = item.Drive;
        if (!Main.IsElevated)
        {
            if (TestExecution.Ask(Loc.T("common.adminRequired") + "\n\n" + Loc.T("common.restartAdmin") + "?"))
                Shell.RestartElevated($"--page tools --disk {d.DiskNumber}");
            return;
        }
        if (_mode == "full" && IsFake && !TestExecution.Ask(Loc.T("tools.fullOnFake"))) return;
        string message = Loc.F("tools.confirm", Fmt.Bytes(target.Value), SelectedFs);
        var dlg = new ConfirmDialog($"{d.DisplayName}  {d.Letters}  —  {Fmt.Bytes(d.DeclaredBytes)}", Loc.T("nav.tools"), message, Fmt.Bytes(d.DeclaredBytes), danger: true)
        {
            Owner = Application.Current.MainWindow,
        };
        if (dlg.ShowDialog() != true) return;

        IsBusy = true;
        Main.IsBusy = true;
        LogText = Loc.T("tools.working");
        PartitionResult? res = null;
        string fs = SelectedFs, label = Label;
        long? size = _mode == "full" ? null : target;
        try
        {
            res = await Task.Run(() => VolumeTools.Repartition(d, size, fs, label));
            LogText = res.Ok ? Loc.F("tools.done", res.Letter ?? "?", Fmt.Bytes(res.PartitionBytes), res.FileSystem ?? fs) : Loc.T("tools.failed") + "\n\n" + res.Log;
        }
        catch (SafetyException ex) { LogText = Loc.T(ex.ReasonKey); }
        catch (IdentityChangedException) { LogText = Loc.T("scan.identityChanged"); }
        catch (Exception ex) { LogText = Loc.F("scan.failedStart", ex.Message); }
        finally
        {
            IsBusy = false;
            Main.IsBusy = false;
        }
        await Main.RefreshDrivesAsync(false);
        Main.SelectedDrive = Main.Drives.FirstOrDefault(x => x.Drive.HardwareKey == d.HardwareKey) ?? Main.SelectedDrive;
        if (res?.Ok == true)
        {
            Main.Notify?.Show("IBRA DRIVE CHECK", LogText);
            if (VerifyAfter && TestExecution.Ask(Loc.T("tools.verifyNow")))
            {
                Main.Navigate("scanner");
                await ScannerViewModel.I.StartAsync(ScanKind.FullFreeSpace);
            }
        }
        else TestExecution.Msg(LogText, true);
    }
}
