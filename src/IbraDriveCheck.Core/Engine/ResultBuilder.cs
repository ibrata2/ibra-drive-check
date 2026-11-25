using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.Core.Engine;

internal static class ResultBuilder
{
    public static ScanResult Start(ScanKind kind, DriveSnapshot drive, long declared) => new()
    {
        Kind = kind,
        Drive = drive,
        DeclaredBytes = declared,
        StartedUtc = DateTime.UtcNow,
    };

    public static void FillFromMap(ScanResult r, ScanMap map)
    {
        var (g, s, f, w, rd, u) = map.Count();
        r.GoodCells = g; r.SlowCells = s; r.FailedCells = f; r.WriteErrorCells = w; r.ReadErrorCells = rd; r.UntestedCells = u;
        r.Map = map.Snapshot();
        var reads = r.Map.Where(c => c.ReadMBps > 0).Select(c => c.ReadMBps).ToList();
        var writes = r.Map.Where(c => c.WriteMBps > 0).Select(c => c.WriteMBps).ToList();
        if (reads.Count > 0) { r.AvgReadMBps = Math.Round(reads.Average(), 2); r.MinReadMBps = Math.Round(reads.Min(), 2); r.MaxReadMBps = Math.Round(reads.Max(), 2); }
        if (writes.Count > 0) { r.AvgWriteMBps = Math.Round(writes.Average(), 2); r.MinWriteMBps = Math.Round(writes.Min(), 2); r.MaxWriteMBps = Math.Round(writes.Max(), 2); }
    }

    public static CellState ToCell(SampleOutcome o) => o switch
    {
        SampleOutcome.Good => CellState.Good,
        SampleOutcome.Slow => CellState.Slow,
        SampleOutcome.WriteError => CellState.WriteError,
        SampleOutcome.ReadError => CellState.ReadError,
        SampleOutcome.Untested => CellState.Untested,
        _ => CellState.Failed,
    };

    public static string? NoteFor(SampleOutcome o, string? detail) => o switch
    {
        SampleOutcome.Aliased => "cell.alias",
        SampleOutcome.Corrupted => "cell.hash",
        SampleOutcome.Missing => detail switch { "zeros" => "cell.zeros", "ff" => "cell.ff", _ => "cell.foreign" },
        SampleOutcome.WriteError => "cell.writeError",
        SampleOutcome.ReadError => "cell.readError",
        SampleOutcome.Slow => "cell.slow",
        _ => null,
    };

    /// <summary>يحدد الحكم النهائي بناءً على تحليل السعة وعدد المناطق البطيئة.</summary>
    public static void ApplyVerdict(ScanResult r, CapacityAnalysis a, bool fullyVerified)
    {
        if (a.Tested == 0)
        {
            r.Verdict = r.Cancelled || r.DeviceDisconnected ? Verdict.Incomplete : Verdict.Inconclusive;
            return;
        }

        if (a.FakeDetected && a.EstimatedReal is long est)
        {
            r.EstimatedRealBytes = est;
            r.EstimatedFakeBytes = Math.Max(0, r.DeclaredBytes - est);
            r.EstimateFromWrap = a.WrapModulus == est;
            if (a.LowerBound is long lo && a.UpperBound is long hi && !r.EstimateFromWrap)
                r.Note("note.estimateRange", lo, hi);
            if (r.EstimateFromWrap) r.Note("note.wrapDetected", a.WrapModulus!.Value);
            if (a.Aliased > 0) r.Note("note.aliasCount", a.Aliased);
        }

        if (r.Cancelled || r.DeviceDisconnected)
        {
            r.Verdict = Verdict.Incomplete;
            if (a.FakeDetected) r.Note("note.partialFakeSigns");
            return;
        }

        if (a.FakeDetected) { r.Verdict = Verdict.FakeCapacity; return; }
        if (a.AllBad) { r.Verdict = Verdict.ReadWriteErrors; r.Note("note.allFailed"); return; }
        if (a.ScatteredErrors) { r.Verdict = Verdict.ReadWriteErrors; return; }

        if (a.Good > 0 && a.Slow > 0.25 * a.Good) { r.Verdict = Verdict.PoorPerformance; return; }

        r.Verdict = Verdict.Genuine;
        if (!fullyVerified) r.Note("note.genuineSampled", a.Tested);
    }
}
