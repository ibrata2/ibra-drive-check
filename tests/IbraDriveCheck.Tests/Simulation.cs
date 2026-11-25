using IbraDriveCheck.Core.Devices;
using IbraDriveCheck.Core.Engine;

namespace IbraDriveCheck.Tests;

/// <summary>ساعة افتراضية: الأجهزة المحاكاة تضيف زمن كل عملية بدون انتظار حقيقي.</summary>
public sealed class SimClock : ITimeSource
{
    private long _t;
    public long Timestamp => Interlocked.Read(ref _t);
    public double Frequency => 1_000_000; // ميكروثانية
    public void Advance(long micros) => Interlocked.Add(ref _t, micros);
}

public enum FakeMode
{
    Genuine,     // سليمة
    Wrap,        // العناوين فوق الحجم الحقيقي تلتف إلى البداية (تكتب فوق البيانات)
    DropZeros,   // الكتابة فوق الحجم الحقيقي تضيع والقراءة تعيد أصفاراً
    DropFF,      // تعيد FF
    WriteReject, // ترفض الكتابة فوق الحجم الحقيقي
}

/// <summary>ذاكرة فيزيائية مشتركة (حتى نحاكي فصل الوحدة وإعادة توصيلها بنفس البيانات).</summary>
public sealed class SimMedia
{
    public readonly Dictionary<long, byte[]> Blocks = new();
    public readonly object Gate = new();

    public static void Initial(long physBlock, Span<byte> dst)
    {
        for (int j = 0; j < dst.Length; j++) dst[j] = (byte)((physBlock * 31 + j * 7 + (physBlock >> 8)) & 0xFF);
    }

    /// <summary>هل كل كتلة تم لمسها تعود لمحتواها الأصلي؟ (للتأكد أن الاستعادة أعادت كل البيانات)</summary>
    public int CountModified()
    {
        int bad = 0;
        var tmp = new byte[4096];
        lock (Gate)
            foreach (var (k, v) in Blocks)
            {
                Initial(k, tmp);
                if (!v.AsSpan().SequenceEqual(tmp)) bad++;
            }
        return bad;
    }
}

public sealed class SimulatedDevice : IBlockDevice
{
    public const int Blk = 4096;
    private readonly SimMedia _media;
    private readonly SimClock _clock;
    private long _ops;

    public long Size { get; }
    public long RealSize { get; }
    public FakeMode Mode { get; }
    public int SectorSize => 512;
    public int Alignment => 4096;
    public bool CanWrite => true;

    public Func<long, bool>? ReadErrorAt { get; set; }
    public Func<long, bool>? WriteErrorAt { get; set; }
    public long? DisconnectAfterOps { get; set; }
    public (long start, long end, long extraMicros)? SlowRange { get; set; }
    public long BaseMicrosPer4K { get; set; } = 250;
    public Action<long, bool>? OnOp { get; set; }   // (ops, isWrite)
    public long Ops => Interlocked.Read(ref _ops);
    public int Writes;

    public SimulatedDevice(long size, long realSize, FakeMode mode, SimClock clock, SimMedia? media = null)
    {
        Size = size;
        RealSize = realSize;
        Mode = mode;
        _clock = clock;
        _media = media ?? new SimMedia();
    }

    public SimMedia Media => _media;

    private void Tick(long off, int len, bool write)
    {
        long ops = Interlocked.Increment(ref _ops);
        OnOp?.Invoke(ops, write);
        if (DisconnectAfterOps is long lim && ops > lim) throw new DeviceGoneException(1167);
        long micros = BaseMicrosPer4K * Math.Max(1, len / Blk);
        if (SlowRange is var (s, e, x) && off < e && off + len > s) micros += x;
        _clock.Advance(micros);
    }

    private long? Physical(long logicalBlock)
    {
        long realBlocks = RealSize / Blk;
        if (Mode == FakeMode.Genuine || logicalBlock < realBlocks) return logicalBlock;
        return Mode == FakeMode.Wrap ? logicalBlock % realBlocks : null;
    }

    public void Read(long offset, Span<byte> buffer)
    {
        Tick(offset, buffer.Length, false);
        if (offset + buffer.Length > Size) throw new BlockIoException(offset, false, 38);
        for (int i = 0; i < buffer.Length / Blk; i++)
        {
            long off = offset + (long)i * Blk;
            if (ReadErrorAt?.Invoke(off) == true) throw new BlockIoException(off, false, 23);
            var dst = buffer.Slice(i * Blk, Blk);
            long? p = Physical(off / Blk);
            if (p is null)
            {
                dst.Fill(Mode == FakeMode.DropFF ? (byte)0xFF : (byte)0);
                continue;
            }
            lock (_media.Gate)
            {
                if (_media.Blocks.TryGetValue(p.Value, out var data)) data.CopyTo(dst);
                else SimMedia.Initial(p.Value, dst);
            }
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        Tick(offset, buffer.Length, true);
        if (offset + buffer.Length > Size) throw new BlockIoException(offset, true, 38);
        Interlocked.Increment(ref Writes);
        for (int i = 0; i < buffer.Length / Blk; i++)
        {
            long off = offset + (long)i * Blk;
            if (WriteErrorAt?.Invoke(off) == true) throw new BlockIoException(off, true, 29);
            long? p = Physical(off / Blk);
            if (p is null)
            {
                if (Mode == FakeMode.WriteReject) throw new BlockIoException(off, true, 29);
                continue;
            }
            lock (_media.Gate) _media.Blocks[p.Value] = buffer.Slice(i * Blk, Blk).ToArray();
        }
    }

    public void Flush() { }
    public void Dispose() { }
}

/// <summary>نظام ملفات محاكى فوق ذاكرة قد تكون مزيفة (تلتف بعد الحجم الحقيقي).</summary>
public sealed class SimFileSpace : ITestFileSpace
{
    private readonly long _total;
    private readonly long _real;
    private readonly bool _wrap;
    private long _allocated;
    private readonly Dictionary<long, byte[]> _phys = new();
    private readonly Dictionary<string, (long start, long len)> _files = new();
    private readonly HashSet<string> _dirs = new();

    public SimFileSpace(string root, long total, long used, long real, bool wrap)
    {
        Root = root;
        _total = total;
        _allocated = used;
        _real = real;
        _wrap = wrap;
    }

    public string Root { get; }
    public long FreeBytes => _total - _allocated;
    public long TotalBytes => _total;
    public long MaxFileBytes => long.MaxValue;
    public bool DirectoryExists(string p) => _dirs.Contains(p);
    public int FileCount => _files.Count;

    public void CreateDirectory(string path) => _dirs.Add(path);
    public void DeleteDirectory(string path) => _dirs.Remove(path);
    public bool Exists(string path) => _files.ContainsKey(path);
    public void Delete(string path)
    {
        if (_files.Remove(path, out var f)) _allocated -= f.len;
    }

    public ITestFile Create(string path, long length)
    {
        if (length > FreeBytes) throw new DiskFullException();
        _files[path] = (_allocated, length);
        _allocated += length;
        return new F(this, _files[path].start, length);
    }

    public ITestFile OpenRead(string path) => new F(this, _files[path].start, _files[path].len);

    private long Map(long devBlock) => _wrap ? devBlock % (_real / 4096) : devBlock;

    private sealed class F : ITestFile
    {
        private readonly SimFileSpace _fs;
        private readonly long _start, _len;
        public F(SimFileSpace fs, long start, long len) { _fs = fs; _start = start; _len = len; }

        public void Write(long offset, ReadOnlySpan<byte> data)
        {
            for (int i = 0; i < data.Length / 4096; i++)
                _fs._phys[_fs.Map((_start + offset) / 4096 + i)] = data.Slice(i * 4096, 4096).ToArray();
        }

        public void Read(long offset, Span<byte> data)
        {
            for (int i = 0; i < data.Length / 4096; i++)
            {
                var dst = data.Slice(i * 4096, 4096);
                if (_fs._phys.TryGetValue(_fs.Map((_start + offset) / 4096 + i), out var b)) b.CopyTo(dst);
                else dst.Clear();
            }
        }

        public void Flush() { }
        public void Dispose() { }
    }
}
