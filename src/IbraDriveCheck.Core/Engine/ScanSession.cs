using System.Diagnostics;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.Core.Engine;

/// <summary>مصدر الوقت (قابل للاستبدال في الاختبارات لمحاكاة البطء بدون انتظار حقيقي).</summary>
public interface ITimeSource
{
    long Timestamp { get; }
    double Frequency { get; }
}

public sealed class StopwatchTime : ITimeSource
{
    public static readonly StopwatchTime Instance = new();
    public long Timestamp => Stopwatch.GetTimestamp();
    public double Frequency => Stopwatch.Frequency;
}

/// <summary>إيقاف مؤقت / استئناف بين خطوات الفحص.</summary>
public sealed class PauseGate
{
    private readonly ManualResetEventSlim _running = new(true);
    public bool IsPaused => !_running.IsSet;
    public void Pause() => _running.Reset();
    public void Resume() => _running.Set();

    public void WaitIfPaused(CancellationToken ct)
    {
        if (_running.IsSet) return;
        try { _running.Wait(ct); }
        catch (OperationCanceledException) { }
    }
}

public enum ScanPhase
{
    Preparing,
    ReadingOriginals,
    Writing,
    Verifying,
    Restoring,
    Reading,
    SeqWrite,
    SeqRead,
    RandWrite,
    RandRead,
    Cleaning,
    Done,
}

/// <summary>
/// حالة فحص جارٍ: يحدثها المحرك من خيط الخلفية، وتقرأها الواجهة كل 100ms.
/// </summary>
public sealed class ScanSession
{
    private readonly ITimeSource _time;
    private readonly long _start;
    private long _bytesTotal;
    private long _bytesDone;
    private long _pausedTicks;
    private long _pauseStartedAt;

    public ScanSession(ITimeSource? time = null)
    {
        _time = time ?? StopwatchTime.Instance;
        _start = Stopwatch.GetTimestamp();
    }

    public ITimeSource Time => _time;
    public PauseGate Pause { get; } = new();
    public ScanMap? Map { get; set; }
    public volatile bool PauseSupported = true;

    private volatile int _phase;
    public ScanPhase Phase { get => (ScanPhase)_phase; set => _phase = (int)value; }

    /// <summary>عدد مراحل الفحص الكلي ورقم المرحلة الحالية (لحساب نسبة التقدم الإجمالية).</summary>
    public int StageCount { get; set; } = 1;
    public int StageIndex { get; set; }

    public double CurrentReadMBps { get; set; }
    public double CurrentWriteMBps { get; set; }
    public long BytesTested;           // يُزاد بـ Interlocked
    public long Operations;

    public void SetStage(int index, ScanPhase phase, long bytesTotal)
    {
        StageIndex = index;
        Phase = phase;
        Interlocked.Exchange(ref _bytesTotal, Math.Max(1, bytesTotal));
        Interlocked.Exchange(ref _bytesDone, 0);
    }

    public void AddProgress(long bytes) => Interlocked.Add(ref _bytesDone, bytes);

    public double StageFraction => Math.Clamp((double)Interlocked.Read(ref _bytesDone) / Interlocked.Read(ref _bytesTotal), 0, 1);

    public double Progress => StageCount <= 0 ? 0 : Math.Clamp((StageIndex + StageFraction) / StageCount, 0, 1);

    public void MarkPaused(bool paused)
    {
        if (paused) _pauseStartedAt = Stopwatch.GetTimestamp();
        else if (_pauseStartedAt != 0)
        {
            _pausedTicks += Stopwatch.GetTimestamp() - _pauseStartedAt;
            _pauseStartedAt = 0;
        }
    }

    public TimeSpan Elapsed
    {
        get
        {
            long now = Stopwatch.GetTimestamp();
            long paused = _pausedTicks + (_pauseStartedAt != 0 ? now - _pauseStartedAt : 0);
            return TimeSpan.FromSeconds((now - _start - paused) / (double)Stopwatch.Frequency);
        }
    }

    public TimeSpan? Remaining
    {
        get
        {
            double p = Progress;
            if (p < 0.01) return null;
            var e = Elapsed.TotalSeconds;
            return TimeSpan.FromSeconds(e / p * (1 - p));
        }
    }

    /// <summary>سجل نصي مختصر للأحداث (مفاتيح ترجمة مع وسائط).</summary>
    public List<NoteItem> Log { get; } = new();
    public void AddLog(string key, params object[] args) { lock (Log) Log.Add(new NoteItem(key, args)); }

    public double Seconds(long t0, long t1) => (t1 - t0) / _time.Frequency;
}
