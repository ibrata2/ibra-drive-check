using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.Core.Engine;

public sealed class BurnInPass
{
    public int Index { get; set; }
    public double WriteMBps { get; set; }
    public double ReadMBps { get; set; }
    public int Errors { get; set; }
    public double? TemperatureC { get; set; }
    public DateTime EndedUtc { get; set; }
}

public sealed class BurnInResult
{
    public List<BurnInPass> Passes { get; } = new();
    public long BytesPerPass { get; set; }
    public Verdict Verdict { get; set; } = Verdict.Inconclusive;
    public bool Cancelled { get; set; }
    public bool Disconnected { get; set; }
    public double? MaxTemperatureC => Passes.Where(p => p.TemperatureC != null).Select(p => p.TemperatureC).Max();
    public List<NoteItem> Notes { get; } = new();
}

/// <summary>
/// اختبار التحمل: كتابة وقراءة متكررة لمدة طويلة في المساحة الفارغة (غير مدمر)، كل دورة ببيانات فريدة جديدة.
/// يكشف الوحدات التي تعمل بالبداية ثم تبدأ بالأخطاء أو تنهار سرعتها أو ترتفع حرارتها.
/// </summary>
public sealed class BurnInTest
{
    private readonly ITestFileSpace _fs;
    private readonly DriveSnapshot _drive;
    private readonly Func<double?>? _temperature;

    public BurnInTest(ITestFileSpace fs, DriveSnapshot drive, Func<double?>? temperature = null)
    {
        _fs = fs;
        _drive = drive;
        _temperature = temperature;
    }

    public BurnInResult Run(TimeSpan duration, int maxPasses, long bytesPerPass, ScanSession session, Action<BurnInPass>? onPass, CancellationToken ct)
    {
        var res = new BurnInResult();
        var start = DateTime.UtcNow;
        long size = Math.Min(bytesPerPass, Math.Max(0, _fs.FreeBytes - (80L << 20)));
        size = size / (4 << 20) * (4 << 20);
        res.BytesPerPass = size;
        if (size < 16L << 20) { res.Notes.Add(new NoteItem("note.noFreeSpace")); return res; }

        for (int i = 1; i <= maxPasses && DateTime.UtcNow - start < duration; i++)
        {
            if (ct.IsCancellationRequested) { res.Cancelled = true; break; }
            var s = new ScanSession { };
            session.StageIndex = i - 1;
            var test = new FreeSpaceFileTest(_fs, _drive, new FreeSpaceOptions { MaxTestBytes = size, ReserveBytes = 64L << 20, MapCells = 256 }, s);
            var mirror = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && s.Phase != ScanPhase.Done)
                {
                    session.CurrentReadMBps = s.CurrentReadMBps;
                    session.CurrentWriteMBps = s.CurrentWriteMBps;
                    session.Map = s.Map;
                    await Task.Delay(150);
                }
            });
            var r = test.Run(ct);
            var pass = new BurnInPass
            {
                Index = i,
                WriteMBps = r.AvgWriteMBps,
                ReadMBps = r.AvgReadMBps,
                Errors = r.FailedCells + r.WriteErrorCells + r.ReadErrorCells,
                TemperatureC = SafeTemp(),
                EndedUtc = DateTime.UtcNow,
            };
            if (r.Cancelled) { res.Cancelled = true; break; }
            res.Passes.Add(pass);
            onPass?.Invoke(pass);
            if (r.DeviceDisconnected) { res.Disconnected = true; break; }
        }
        Judge(res);
        return res;
    }

    private double? SafeTemp()
    {
        try { return _temperature?.Invoke(); } catch { return null; }
    }

    public static void Judge(BurnInResult res)
    {
        if (res.Passes.Count == 0) { res.Verdict = res.Cancelled || res.Disconnected ? Verdict.Incomplete : Verdict.Inconclusive; return; }
        int errors = res.Passes.Sum(p => p.Errors);
        if (res.Disconnected) { res.Verdict = Verdict.ReadWriteErrors; res.Notes.Add(new NoteItem("burn.disconnected")); return; }
        if (errors > 0)
        {
            var firstBad = res.Passes.First(p => p.Errors > 0);
            res.Verdict = Verdict.ReadWriteErrors;
            res.Notes.Add(new NoteItem("burn.errorsFrom", firstBad.Index, errors));
            return;
        }
        var w = res.Passes.Where(p => p.WriteMBps > 0).Select(p => p.WriteMBps).ToList();
        if (w.Count >= 3 && w.TakeLast(Math.Max(1, w.Count / 3)).Average() < w.Take(Math.Max(1, w.Count / 3)).Average() * 0.5)
        {
            res.Verdict = Verdict.PoorPerformance;
            res.Notes.Add(new NoteItem("burn.slowdown"));
        }
        else res.Verdict = res.Cancelled ? Verdict.Incomplete : Verdict.Genuine;
        if (res.MaxTemperatureC is double t && t >= 70) res.Notes.Add(new NoteItem("burn.hot", t.ToString("0")));
        res.Notes.Add(new NoteItem("burn.summary", res.Passes.Count, errors));
    }
}
