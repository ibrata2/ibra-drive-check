using System.Collections.ObjectModel;
using System.Windows.Threading;
using IbraDriveCheck.App.Infrastructure;
using IbraDriveCheck.Core.Devices.Windows;
using IbraDriveCheck.Core.Engine;
using IbraDriveCheck.Core.Localization;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.App.ViewModels;

public sealed class SpeedTile : ObservableObject
{
    public string TitleKey { get; init; } = "";
    public string Title => Loc.T(TitleKey);
    private string _main = "—", _sub = "";
    public string Main { get => _main; set => Set(ref _main, value); }
    public string Sub { get => _sub; set => Set(ref _sub, value); }
    public void Refresh() => Raise(nameof(Title));
}

public sealed class SpeedViewModel : ObservableObject
{
    public static SpeedViewModel I { get; } = new();
    public MainViewModel Main => MainViewModel.I;

    public ObservableCollection<VolumeSnapshot> Volumes { get; } = new();
    private VolumeSnapshot? _volume;
    public VolumeSnapshot? SelectedVolume { get => _volume; set => Set(ref _volume, value); }
    public List<int> Sizes { get; } = new() { 64, 256, 512, 1024 };
    public int SelectedSize
    {
        get => Main.Settings.SpeedTestMB;
        set { Main.Settings.SpeedTestMB = value; Main.SaveSettings(); Raise(); }
    }

    public SpeedTile SeqRead { get; } = new() { TitleKey = "speed.seqRead" };
    public SpeedTile SeqWrite { get; } = new() { TitleKey = "speed.seqWrite" };
    public SpeedTile RandRead { get; } = new() { TitleKey = "speed.randRead" };
    public SpeedTile RandWrite { get; } = new() { TitleKey = "speed.randWrite" };

    private List<double>? _readSamples, _writeSamples;
    public List<double>? ReadSamples { get => _readSamples; private set => Set(ref _readSamples, value); }
    public List<double>? WriteSamples { get => _writeSamples; private set => Set(ref _writeSamples, value); }
    private int _chartVersion;
    public int ChartVersion { get => _chartVersion; private set => Set(ref _chartVersion, value); }

    private bool _running;
    public bool IsRunning { get => _running; private set { Set(ref _running, value); Raise(nameof(IsNotRunning)); } }
    public bool IsNotRunning => !_running;
    private double _progress;
    public double ProgressPercent { get => _progress; private set => Set(ref _progress, value); }
    private string _phase = "";
    public string PhaseText { get => _phase; private set => Set(ref _phase, value); }
    private string _live = "";
    public string LiveText { get => _live; private set => Set(ref _live, value); }
    private string? _verdict;
    public string? VerdictText { get => _verdict; private set => Set(ref _verdict, value); }

    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }

    private ScanSession? _session;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _timer;
    private ScanResult? _last;
    private readonly List<double> _liveRead = new(), _liveWrite = new();

    private SpeedViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Tick();
        StartCommand = new RelayCommand(() => _ = StartAsync(), () => !IsRunning && Main.IsIdle && Main.SelectedDrive != null && (SelectedVolume != null || Volumes.Count == 0));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsRunning);
        MainViewModel.I.CancelCommands.Add(CancelCommand);
        Main.SelectedDriveChanged += LoadVolumes;
        Loc.I.LanguageChanged += () =>
        {
            foreach (var t in new[] { SeqRead, SeqWrite, RandRead, RandWrite }) t.Refresh();
            if (_last != null) Show(_last);
        };
        LoadVolumes();
    }

    public void LoadVolumes()
    {
        Volumes.Clear();
        foreach (var v in Main.SelectedDrive?.Drive.Volumes ?? new List<VolumeSnapshot>()) Volumes.Add(v);
        SelectedVolume = Volumes.OrderByDescending(v => v.FreeBytes).FirstOrDefault();
    }

    private async Task StartAsync()
    {
        var item = Main.SelectedDrive;
        if (item == null) return;
        if (!TestExecution.Confirm(ScanKind.Speed, item.Drive)) return;
        _session = new ScanSession();
        _cts = new CancellationTokenSource();
        _liveRead.Clear(); _liveWrite.Clear();
        foreach (var t in new[] { SeqRead, SeqWrite, RandRead, RandWrite }) { t.Main = "—"; t.Sub = ""; }
        VerdictText = null;
        IsRunning = true;
        Main.IsBusy = true;
        _timer.Start();
        try
        {
            var req = new ScanRequest { Drive = item.Drive, Kind = ScanKind.Speed, SpeedTestMB = SelectedSize, Volume = SelectedVolume?.Letter };
            var r = await TestExecution.RunAsync(req, _session, _cts.Token);
            if (r != null)
            {
                _last = r;
                Show(r);
                Main.AddResult(r);
                ProgressPercent = 100;
            }
        }
        finally
        {
            _timer.Stop();
            IsRunning = false;
            Main.IsBusy = false;
        }
    }

    private void Tick()
    {
        var s = _session;
        if (s == null) return;
        ProgressPercent = Math.Round(s.Progress * 100, 1);
        PhaseText = Loc.T("phase." + s.Phase);
        if (s.Phase == ScanPhase.SeqWrite && s.CurrentWriteMBps > 0) _liveWrite.Add(s.CurrentWriteMBps);
        if (s.Phase == ScanPhase.SeqRead && s.CurrentReadMBps > 0) _liveRead.Add(s.CurrentReadMBps);
        LiveText = s.Phase is ScanPhase.SeqRead or ScanPhase.RandRead ? Fmt.Speed(s.CurrentReadMBps) : Fmt.Speed(s.CurrentWriteMBps);
        WriteSamples = _liveWrite.ToList();
        ReadSamples = _liveRead.ToList();
        ChartVersion++;
    }

    private void Show(ScanResult r)
    {
        var sp = r.Speed;
        VerdictText = Strings.Verdict(Loc.I.Lang, r.Verdict) + " — " + string.Join(" ", r.Notes.Select(n => Strings.Format(Loc.I.Lang, n)));
        if (sp == null) return;
        void Seq(SpeedTile t, SpeedStat st)
        {
            if (!st.Measured) return;
            t.Main = Fmt.Speed(st.AvgMBps);
            t.Sub = $"{Loc.T("speed.min")} {Fmt.Speed(st.MinMBps)}  ·  {Loc.T("speed.max")} {Fmt.Speed(st.MaxMBps)}\n{Loc.T("speed.latency")} {st.AvgLatencyMs:0.##} ms";
        }
        void Rnd(SpeedTile t, SpeedStat st)
        {
            if (!st.Measured) return;
            t.Main = Fmt.Speed(st.AvgMBps);
            t.Sub = $"{st.Iops:0} IOPS\n{Loc.T("speed.latency")} {st.AvgLatencyMs:0.###} ms ({Loc.T("speed.min")} {st.MinLatencyMs:0.###} · {Loc.T("speed.max")} {st.MaxLatencyMs:0.###})";
        }
        Seq(SeqRead, sp.SeqRead); Seq(SeqWrite, sp.SeqWrite);
        Rnd(RandRead, sp.RandRead4K); Rnd(RandWrite, sp.RandWrite4K);
        if (sp.SeqRead.Samples.Count > 1) ReadSamples = sp.SeqRead.Samples;
        if (sp.SeqWrite.Samples.Count > 1) WriteSamples = sp.SeqWrite.Samples;
        ChartVersion++;
    }
}

public sealed class HealthViewModel : ObservableObject
{
    public static HealthViewModel I { get; } = new();
    public MainViewModel Main => MainViewModel.I;

    public ObservableCollection<InfoRow> InfoRows { get; } = new();
    public ObservableCollection<InfoRow> HealthRows { get; } = new();
    public ObservableCollection<string> Messages { get; } = new();

    private bool _loading;
    public bool IsLoading { get => _loading; private set { Set(ref _loading, value); Raise(nameof(IsNotLoading)); } }
    public bool IsNotLoading => !_loading;

    public RelayCommand ReadCommand { get; }

    private HealthViewModel()
    {
        ReadCommand = new RelayCommand(() => _ = ReadAsync(true), () => !IsLoading && Main.SelectedDrive != null);
        Main.SelectedDriveChanged += () => Fill();
        Loc.I.LanguageChanged += Fill;
    }

    public async Task ReadAsync(bool save)
    {
        var item = Main.SelectedDrive;
        if (item == null) return;
        IsLoading = true;
        try
        {
            var req = new ScanRequest { Drive = item.Drive, Kind = ScanKind.Health };
            var r = await TestExecution.RunAsync(req, new ScanSession(), CancellationToken.None);
            if (r?.Health != null)
            {
                item.Health = r.Health;
                if (save) Main.AddResult(r);
            }
        }
        finally
        {
            IsLoading = false;
            Fill();
        }
    }

    public void Fill()
    {
        InfoRows.Clear(); HealthRows.Clear(); Messages.Clear();
        var item = Main.SelectedDrive;
        if (item == null) return;
        foreach (var r in MainViewModel.InfoRows(item.Drive, item.Health, item.Last)) InfoRows.Add(r);
        var h = item.Health;
        string NA = Loc.T("common.notAvailable");
        if (h == null) return;
        HealthRows.Add(new InfoRow(Loc.T("health.supported"), h.SmartSupported ? Loc.T("common.yes") : Loc.T("common.no")));
        HealthRows.Add(new InfoRow(Loc.T("health.status"), h.HealthStatus != null ? Loc.T("health." + h.HealthStatus) : NA, true));
        HealthRows.Add(new InfoRow(Loc.T("health.predict"), h.PredictFailure is bool p ? (p ? Loc.T("common.yes") : Loc.T("common.no")) : NA));
        HealthRows.Add(new InfoRow(Loc.T("health.temp"), h.TemperatureC is double t ? $"{t:0} °C" : NA));
        HealthRows.Add(new InfoRow(Loc.T("health.tempMax"), h.TemperatureMaxC is double tm ? $"{tm:0} °C" : NA));
        HealthRows.Add(new InfoRow(Loc.T("health.hours"), h.PowerOnHours?.ToString() ?? NA));
        HealthRows.Add(new InfoRow(Loc.T("health.cycles"), h.StartStopCycles?.ToString() ?? NA));
        HealthRows.Add(new InfoRow(Loc.T("health.readErr"), h.ReadErrorsTotal?.ToString() ?? NA));
        HealthRows.Add(new InfoRow(Loc.T("health.readErrUnc"), h.ReadErrorsUncorrected?.ToString() ?? NA));
        HealthRows.Add(new InfoRow(Loc.T("health.writeErr"), h.WriteErrorsTotal?.ToString() ?? NA));
        HealthRows.Add(new InfoRow(Loc.T("health.wear"), h.WearPercent is int w ? $"{w}%" : NA));
        HealthRows.Add(new InfoRow(Loc.T("health.media"), h.MediaType ?? NA));
        HealthRows.Add(new InfoRow(Loc.T("health.op"), h.OperationalStatus ?? NA));
        HealthRows.Add(new InfoRow(Loc.T("health.written"), h.DataWrittenBytes is long dw ? Fmt.Bytes(dw) : NA, true));
        HealthRows.Add(new InfoRow(Loc.T("health.readData"), h.DataReadBytes is long dr ? Fmt.Bytes(dr) : NA));
        HealthRows.Add(new InfoRow(Loc.T("health.percentUsed"), h.PercentUsed is int pu ? $"{pu}%" : NA));
        HealthRows.Add(new InfoRow(Loc.T("health.spare"), h.AvailableSpare is int sp ? $"{sp}%" : NA));
        HealthRows.Add(new InfoRow(Loc.T("health.powerCycles"), h.PowerCycles?.ToString() ?? NA));
        HealthRows.Add(new InfoRow(Loc.T("health.unsafe"), h.UnsafeShutdowns?.ToString() ?? NA));
        HealthRows.Add(new InfoRow(Loc.T("health.mediaErrors"), h.MediaErrors?.ToString() ?? NA));
        HealthRows.Add(new InfoRow(Loc.T("health.critical"), h.CriticalWarning?.ToString() ?? NA));
        foreach (var m in h.Messages) Messages.Add(Loc.T(m));
    }
}
