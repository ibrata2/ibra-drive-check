using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using IbraDriveCheck.App.Infrastructure;
using IbraDriveCheck.Core.Devices.Windows;
using IbraDriveCheck.Core.Engine;
using IbraDriveCheck.Core.Localization;
using IbraDriveCheck.Core.Model;
using IbraDriveCheck.Core.Reports;
using IbraDriveCheck.Core.Safety;

namespace IbraDriveCheck.App.ViewModels;

public sealed class CheckRow
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Glyph { get; init; } = "";
    public Brush Color { get; init; } = Brushes.Gray;
    public string StatusText { get; init; } = "";
}

/// <summary>الفحص الشامل للشراء: الصحة ← كشف السعة ← السرعة ← حكم نهائي (+ التوافق اختيارياً).</summary>
public sealed class PurchaseViewModel : ObservableObject
{
    public static PurchaseViewModel I { get; } = new();
    public MainViewModel Main => MainViewModel.I;

    private bool _compat = true;
    public bool CheckCompatibility { get => _compat; set => Set(ref _compat, value); }

    private bool _running;
    public bool IsRunning { get => _running; private set { Set(ref _running, value); Raise(nameof(IsNotRunning)); } }
    public bool IsNotRunning => !_running;

    private string _step = "";
    public string StepText { get => _step; private set => Set(ref _step, value); }
    private double _progress;
    public double ProgressPercent { get => _progress; private set => Set(ref _progress, value); }
    private ScanMap? _map;
    public ScanMap? LiveMap { get => _map; private set => Set(ref _map, value); }
    private int _mapVersion;
    public int MapVersion { get => _mapVersion; private set => Set(ref _mapVersion, value); }
    private CellRecord[]? _records;
    public CellRecord[]? Records { get => _records; private set => Set(ref _records, value); }

    private ScanResult? _result;
    public ScanResult? Result { get => _result; private set { Set(ref _result, value); Refill(); } }
    public bool HasResult => _result?.Advice != null;
    public string LevelText => _result?.Advice is { } a ? Strings.Level(Loc.I.Lang, a.Level) : "";
    public string LevelHint => _result?.Advice is { } a ? Strings.LevelHint(Loc.I.Lang, a.Level) : "";
    public Brush LevelBrush => _result?.Advice?.Level switch
    {
        AdviceLevel.Buy => VerdictColors.For(Verdict.Genuine),
        AdviceLevel.Caution => VerdictColors.For(Verdict.PoorPerformance),
        AdviceLevel.DoNotBuy => VerdictColors.For(Verdict.FakeCapacity),
        _ => VerdictColors.For(Verdict.Inconclusive),
    };
    public string LevelGlyph => _result?.Advice?.Level switch
    {
        AdviceLevel.Buy => "",
        AdviceLevel.Caution => "",
        AdviceLevel.DoNotBuy => "",
        _ => "",
    };
    public bool ShowFix => _result?.Verdict == Verdict.FakeCapacity;
    public RelayCommand FixCommand { get; } = new(() => MainViewModel.I.Navigate("tools"));
    public ObservableCollection<CheckRow> Checks { get; } = new();
    public ObservableCollection<CheckRow> Compat { get; } = new();
    public ObservableCollection<InfoRow> SystemRows { get; } = new();
    public bool HasCompat => Compat.Count > 0;

    public RelayCommand RunCommand { get; }
    public RelayCommand CancelCommand { get; }

    private ScanSession? _session;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _timer;
    private int _stepIndex, _stepCount;

    private PurchaseViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => Tick();
        RunCommand = new RelayCommand(() => _ = RunAsync(), () => !IsRunning && Main.IsIdle && Main.SelectedDrive != null);
        CancelCommand = new RelayCommand(() => { _cts?.Cancel(); _session?.Pause.Resume(); }, () => IsRunning);
        MainViewModel.I.CancelCommands.Add(CancelCommand);
        Loc.I.LanguageChanged += Refill;
        Main.SelectedDriveChanged += () =>
        {
            var d = Main.SelectedDrive;
            Result = d == null ? null : Main.History.FirstOrDefault(h => h.Kind == ScanKind.Purchase && h.Drive.HardwareKey == d.Drive.HardwareKey);
            Records = Result?.Map;
        };
    }

    private async Task RunAsync()
    {
        var item = Main.SelectedDrive;
        if (item == null) return;
        var d = item.Drive;
        bool canQuick = SafetyPolicy.Check(d, ScanKind.Quick).Allowed;
        // كشف السعة (أو قراءة العينات للأقراص الداخلية) يحتاج وصولاً مباشراً → صلاحيات المسؤول
        if (!Main.IsElevated)
        {
            if (TestExecution.Ask(Loc.T("common.adminRequired") + "\n\n" + Loc.T("common.restartAdmin") + "?"))
                Shell.RestartElevated($"--page fake --disk {d.DiskNumber}");
            return;
        }
        if (canQuick && !TestExecution.Confirm(ScanKind.Quick, d)) return;

        Result = null;
        Records = null;
        _cts = new CancellationTokenSource();
        IsRunning = true;
        Main.IsBusy = true;
        _timer.Start();
        var started = DateTime.UtcNow;
        ScanResult? health = null, capacity = null, readOnly = null, speed = null;
        try
        {
            _stepCount = 3;
            // 1) الصحة والمعلومات
            _stepIndex = 0;
            health = await Step(new ScanRequest { Drive = d, Kind = ScanKind.Health });
            if (health?.Health != null) item.Health = health.Health;

            // 2) كشف السعة الوهمية (أو قراءة بالعينات للأقراص الداخلية المستخدمة)
            if (!_cts.IsCancellationRequested)
            {
                _stepIndex = 1;
                if (canQuick) capacity = await Step(new ScanRequest { Drive = d, Kind = ScanKind.Quick });
                else readOnly = await Step(new ScanRequest { Drive = d, Kind = ScanKind.ReadOnly, ReadOnlyFullSurface = false });
            }

            // 3) السرعة (ملف مؤقت، أو قراءة مباشرة إذا الوحدة بدون قسم)
            if (!_cts.IsCancellationRequested && SafetyPolicy.Check(d, ScanKind.Speed).Allowed)
            {
                _stepIndex = 2;
                await Main.RefreshDrivesAsync(false);   // الأقسام رجعت بعد فك القفل
                var fresh = Main.Drives.FirstOrDefault(x => x.Drive.HardwareKey == d.HardwareKey)?.Drive ?? d;
                speed = await Step(new ScanRequest { Drive = fresh, Kind = ScanKind.Speed, SpeedTestMB = Math.Max(256, Main.Settings.SpeedTestMB) });
            }

            SystemSnapshot? sys = null;
            if (CheckCompatibility) sys = await Task.Run(() => { try { return SystemInfoReader.Read(d); } catch { return null; } });

            var advice = PurchaseAdvisor.Evaluate(d, capacity, speed, health?.Health ?? item.Health, readOnly, CheckCompatibility, sys);
            var r = new ScanResult
            {
                Kind = ScanKind.Purchase,
                Drive = d,
                DeclaredBytes = d.DeclaredBytes,
                StartedUtc = started,
                FinishedUtc = DateTime.UtcNow,
                Advice = advice,
                Health = health?.Health ?? item.Health,
                Speed = speed?.Speed,
                Cancelled = _cts.IsCancellationRequested,
                Verdict = advice.Level switch
                {
                    AdviceLevel.Buy => Verdict.Genuine,
                    AdviceLevel.Caution => Verdict.PoorPerformance,
                    AdviceLevel.DoNotBuy => capacity?.Verdict == Verdict.FakeCapacity ? Verdict.FakeCapacity : Verdict.ReadWriteErrors,
                    _ => _cts.IsCancellationRequested ? Verdict.Incomplete : Verdict.Inconclusive,
                },
            };
            var cap = capacity ?? readOnly;
            if (cap != null)
            {
                r.EstimatedRealBytes = cap.EstimatedRealBytes;
                r.EstimatedFakeBytes = cap.EstimatedFakeBytes;
                r.EstimateFromWrap = cap.EstimateFromWrap;
                r.VerifiedBytes = cap.VerifiedBytes;
                r.CoveragePercent = cap.CoveragePercent;
                r.SampleCount = cap.SampleCount;
                r.Map = cap.Map;
                r.GoodCells = cap.GoodCells; r.SlowCells = cap.SlowCells; r.FailedCells = cap.FailedCells;
                r.WriteErrorCells = cap.WriteErrorCells; r.ReadErrorCells = cap.ReadErrorCells; r.RestoreOk = cap.RestoreOk;
                r.Notes.AddRange(cap.Notes);
            }
            if (speed != null) { r.AvgReadMBps = speed.AvgReadMBps; r.AvgWriteMBps = speed.AvgWriteMBps; }
            Result = r;
            Records = r.Map;
            LiveMap = null;
            MapVersion++;
            Main.AddResultSilently(r);
            Main.Notify?.Show("IBRA DRIVE CHECK", Loc.F("notify.advice", d.DisplayName, Strings.Level(Loc.I.Lang, advice.Level)), advice.Level == AdviceLevel.DoNotBuy);
            ProgressPercent = 100;
        }
        finally
        {
            _timer.Stop();
            IsRunning = false;
            Main.IsBusy = false;
            await Main.RefreshDrivesAsync(false);
        }
    }

    private async Task<ScanResult?> Step(ScanRequest req)
    {
        _session = new ScanSession();
        var r = await TestExecution.RunAsync(req, _session, _cts!.Token);
        if (r != null) Main.AddResultSilently(r);
        return r;
    }

    private void Tick()
    {
        var s = _session;
        if (s == null) return;
        double stage = s.Progress;
        ProgressPercent = Math.Round((_stepIndex + stage) / Math.Max(1, _stepCount) * 100, 1);
        string name = _stepIndex switch { 0 => Loc.T("kind.Health"), 1 => Loc.T("kind.Quick"), _ => Loc.T("kind.Speed") };
        StepText = Loc.F("adv.step", _stepIndex + 1, _stepCount, name) + " — " + Loc.T("phase." + s.Phase);
        if (s.Map != null && !ReferenceEquals(LiveMap, s.Map)) { LiveMap = s.Map; Records = null; }
        if (s.Map != null) MapVersion = s.Map.Version;
    }

    private void Refill()
    {
        Raise(nameof(HasResult)); Raise(nameof(LevelText)); Raise(nameof(LevelHint)); Raise(nameof(LevelBrush)); Raise(nameof(LevelGlyph)); Raise(nameof(ShowFix));
        Checks.Clear(); Compat.Clear(); SystemRows.Clear();
        var a = _result?.Advice;
        if (a != null)
        {
            string lang = Loc.I.Lang;
            foreach (var c in a.Checks) Checks.Add(Row(c, lang));
            if (a.CompatibilityChecked)
            {
                foreach (var c in a.Compatibility) Compat.Add(Row(c, lang));
                if (a.System != null) foreach (var (l, v) in ReportBuilder.SystemRows(a.System, lang)) SystemRows.Add(new InfoRow(l, v));
            }
        }
        Raise(nameof(HasCompat));
    }

    private static CheckRow Row(AdviceCheck c, string lang) => new()
    {
        Title = Strings.Get(lang, c.TitleKey),
        Detail = c.Detail != null ? Strings.Format(lang, c.Detail) : "",
        StatusText = Strings.Get(lang, "check." + c.Status),
        Glyph = c.Status switch { CheckStatus.Pass => "", CheckStatus.Warn => "", CheckStatus.Fail => "", CheckStatus.Info => "", _ => "" },
        Color = c.Status switch
        {
            CheckStatus.Pass => VerdictColors.For(Verdict.Genuine),
            CheckStatus.Warn => VerdictColors.For(Verdict.PoorPerformance),
            CheckStatus.Fail => VerdictColors.For(Verdict.FakeCapacity),
            CheckStatus.Info => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6)),
            _ => VerdictColors.For(Verdict.Inconclusive),
        },
    };
}
