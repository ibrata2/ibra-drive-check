using IbraDriveCheck.Core.Devices;

namespace IbraDriveCheck.Core.Recovery;

/// <summary>مصدر قراءة بالبايت بأي عنوان وطول (قسم أو قرص كامل أو ملف صورة في الاختبارات).</summary>
public interface IVolumeSource : IDisposable
{
    long Size { get; }
    void ReadAt(long offset, Span<byte> destination);
}

/// <summary>يحول وحدة تخزين تتطلب محاذاة القطاعات إلى قراءة بأي عنوان.</summary>
public sealed class BlockVolumeSource : IVolumeSource
{
    private readonly IBlockDevice _dev;
    private readonly bool _own;
    private readonly AlignedBuffer _buf;
    private readonly int _align;

    public BlockVolumeSource(IBlockDevice dev, bool ownsDevice = true)
    {
        _dev = dev;
        _own = ownsDevice;
        _align = Math.Max(512, dev.SectorSize);
        _buf = new AlignedBuffer(4 << 20, Math.Max(4096, dev.Alignment));
    }

    public long Size => _dev.Size;
    /// <summary>عدد القطاعات التي تعذرت قراءتها (مُلئت بأصفار).</summary>
    public long BadSectors;

    public void ReadAt(long offset, Span<byte> destination)
    {
        while (destination.Length > 0)
        {
            if (offset >= Size) { destination.Clear(); return; }
            long start = offset / _align * _align;
            int skip = (int)(offset - start);
            int want = Math.Min(destination.Length, _buf.Length - skip);
            long len = (skip + want + _align - 1) / _align * _align;
            len = Math.Min(len, (Size - start) / _align * _align);
            if (len <= skip) { destination.Clear(); return; }
            try
            {
                _dev.Read(start, _buf.Slice(0, (int)len));
            }
            catch (BlockIoException)
            {
                // قطاعات تالفة أو نهاية القسم: نقرأ قطاعاً قطاعاً ونملأ غير المقروء بالأصفار
                for (long s = 0; s < len; s += _align)
                {
                    var one = _buf.Slice((int)s, _align);
                    try { _dev.Read(start + s, one); }
                    catch (BlockIoException) { one.Clear(); Interlocked.Increment(ref BadSectors); }
                }
            }
            int got = (int)Math.Min(want, len - skip);
            _buf.Slice(skip, got).CopyTo(destination);
            destination = destination[got..];
            offset += got;
        }
    }

    public void Dispose()
    {
        _buf.Dispose();
        if (_own) _dev.Dispose();
    }
}

/// <summary>ملف صورة (للاختبارات).</summary>
public sealed class FileVolumeSource : IVolumeSource
{
    private readonly FileStream _fs;
    public FileVolumeSource(string path) => _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    public long Size => _fs.Length;
    public void ReadAt(long offset, Span<byte> destination)
    {
        destination.Clear();
        if (offset >= _fs.Length) return;
        _fs.Position = offset;
        int total = 0;
        while (total < destination.Length)
        {
            int n = _fs.Read(destination[total..]);
            if (n <= 0) break;
            total += n;
        }
    }
    public void Dispose() => _fs.Dispose();
}

public enum RecoveryChance
{
    Good,         // البيانات لم يُكتب فوقها
    Partial,      // جزء من البيانات استُخدم لملفات أخرى
    Poor,         // البيانات استُخدمت بالكامل غالباً
    Carved,       // مستخرج بالتوقيع من المساحة الفارغة (ملف متصل)
    Unsupported,  // مضغوط/مشفر أو بدون موقع بيانات
}

public readonly record struct Extent(long Offset, long Length)
{
    /// <summary>Offset سالب = منطقة فارغة (sparse) تُكتب أصفاراً.</summary>
    public bool IsSparse => Offset < 0;
}

public sealed class RecoveryItem
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>المسار النسبي للمجلد (بفاصل /). فارغ = الجذر.</summary>
    public string Dir { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime? Modified { get; set; }
    public DateTime? Created { get; set; }
    public RecoveryChance Chance { get; set; }
    public List<Extent> Extents { get; set; } = new();
    public byte[]? Resident { get; set; }
    public string Source { get; set; } = "";
    /// <summary>false = ملف موجود (يظهر عند إنقاذ وحدة لا تفتح).</summary>
    public bool Deleted { get; set; } = true;
    public string Extension => IsDirectory ? "" : Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();
    public string FullPath => Dir.Length == 0 ? Name : Dir + "/" + Name;
}

public sealed class RecoveryScan
{
    public string FileSystem { get; set; } = "";
    public int ClusterSize { get; set; }
    public List<RecoveryItem> Items { get; } = new();
    /// <summary>عدد سجلات MFT غير المستخدمة (لإزالة آثار الأسماء في NTFS).</summary>
    public long UnusedMftRecords { get; set; }
    public long MftRecordSize { get; set; }
    public bool Cancelled { get; set; }
    public string? Error { get; set; }
    public List<string> Notes { get; } = new();

    public int DeletedNamedCount => Items.Count(i => !i.IsDirectory && i.Source != "Carved");
}

/// <summary>حالة التقدم (يحدثها المحرك وتقرأها الواجهة).</summary>
public sealed class RecoveryProgress
{
    private long _done, _total = 1;
    public volatile string Phase = "rec.phase.detect";
    public volatile int Found;
    public void SetTotal(long t) { Interlocked.Exchange(ref _total, Math.Max(1, t)); Interlocked.Exchange(ref _done, 0); }
    public void Add(long n) => Interlocked.Add(ref _done, n);
    public void Set(long n) => Interlocked.Exchange(ref _done, n);
    public double Fraction => Math.Clamp((double)Interlocked.Read(ref _done) / Interlocked.Read(ref _total), 0, 1);
}

public static class Le
{
    public static ushort U16(ReadOnlySpan<byte> b, int o) => (ushort)(b[o] | b[o + 1] << 8);
    public static uint U32(ReadOnlySpan<byte> b, int o) => (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
    public static long I64(ReadOnlySpan<byte> b, int o) => (long)(U32(b, o) | (ulong)U32(b, o + 4) << 32);
    public static ulong U64(ReadOnlySpan<byte> b, int o) => U32(b, o) | (ulong)U32(b, o + 4) << 32;
    public static uint BE32(ReadOnlySpan<byte> b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);

    public static DateTime? FileTime(long ft)
    {
        if (ft <= 0) return null;
        try { return DateTime.FromFileTimeUtc(ft); } catch { return null; }
    }

    /// <summary>تاريخ ووقت DOS (FAT / exFAT).</summary>
    public static DateTime? Dos(ushort date, ushort time)
    {
        if (date == 0) return null;
        try
        {
            int y = 1980 + (date >> 9), m = (date >> 5) & 0xF, d = date & 0x1F;
            int h = time >> 11, mi = (time >> 5) & 0x3F, s = (time & 0x1F) * 2;
            if (m is < 1 or > 12 || d is < 1 or > 31 || h > 23 || mi > 59 || s > 59) return null;
            return new DateTime(y, m, d, h, mi, s, DateTimeKind.Local);
        }
        catch { return null; }
    }
}
