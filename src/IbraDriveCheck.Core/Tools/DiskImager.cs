using System.Buffers.Binary;
using IbraDriveCheck.Core.Devices;
using IbraDriveCheck.Core.Recovery;

namespace IbraDriveCheck.Core.Tools;

public sealed class ImageResult
{
    public long Bytes;
    public long BadSectors;
    public bool Vhd;
    public bool Cancelled;
    public string Path = "";
}

/// <summary>
/// نسخة كاملة من وحدة تخزين إلى ملف صورة: VHD ثابت (يفتح بويندوز مباشرة بالنقر المزدوج) أو IMG خام.
/// القطاعات التالفة تُملأ بأصفار وتُحسب، حتى تنجح النسخة من وحدة على وشك العطل.
/// </summary>
public static class DiskImager
{
    public const long MaxVhdBytes = 2040L * 1024 * 1024 * 1024;
    public const int FooterSize = 512;

    public static ImageResult Create(IVolumeSource src, string outPath, bool vhd, RecoveryProgress p, CancellationToken ct)
    {
        var res = new ImageResult { Path = outPath, Vhd = vhd && src.Size <= MaxVhdBytes && src.Size % 512 == 0 };
        p.Phase = "clone.phase.read";
        p.SetTotal(src.Size);
        var buf = new byte[4 << 20];
        using (var fs = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
        {
            long pos = 0;
            while (pos < src.Size)
            {
                if (ct.IsCancellationRequested) { res.Cancelled = true; break; }
                int n = (int)Math.Min(buf.Length, src.Size - pos);
                src.ReadAt(pos, buf.AsSpan(0, n));
                fs.Write(buf, 0, n);
                pos += n;
                p.Set(pos);
            }
            res.Bytes = pos;
            if (res.Vhd && !res.Cancelled) fs.Write(VhdFooter(src.Size));
            fs.Flush(true);
        }
        if (src is BlockVolumeSource b) res.BadSectors = Interlocked.Read(ref b.BadSectors);
        if (res.Cancelled) { try { File.Delete(outPath); } catch { } }
        return res;
    }

    /// <summary>حجم البيانات داخل ملف صورة (بدون تذييل VHD).</summary>
    public static long DataSize(string imagePath)
    {
        long len = new FileInfo(imagePath).Length;
        if (len < FooterSize) return len;
        using var fs = File.OpenRead(imagePath);
        fs.Position = len - FooterSize;
        var f = new byte[8];
        fs.ReadExactly(f);
        return f.AsSpan().SequenceEqual("conectix"u8) ? len - FooterSize : len;
    }

    /// <summary>يكتب صورة إلى وحدة تخزين (يمسح محتواها). الوحدة يجب أن تكون مساوية أو أكبر.</summary>
    public static long Restore(string imagePath, IBlockDevice dev, RecoveryProgress p, CancellationToken ct)
    {
        long size = DataSize(imagePath);
        if (size > dev.Size) throw new InvalidOperationException("Target is smaller than the image");
        p.Phase = "clone.phase.write";
        p.SetTotal(size);
        int align = Math.Max(4096, dev.Alignment);
        using var buf = new AlignedBuffer(4 << 20, align);
        using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        long pos = 0;
        while (pos < size && !ct.IsCancellationRequested)
        {
            int n = (int)Math.Min(buf.Length, size - pos);
            var span = buf.Slice(0, n);
            int read = 0;
            while (read < n) { int r = fs.Read(span[read..]); if (r <= 0) break; read += r; }
            int wlen = (n + dev.SectorSize - 1) / dev.SectorSize * dev.SectorSize;
            if (wlen > n) buf.Slice(n, wlen - n).Clear();
            dev.Write(pos, buf.Slice(0, wlen));
            pos += n;
            p.Set(pos);
        }
        dev.Flush();
        return pos;
    }

    /// <summary>تذييل VHD ثابت (Microsoft Virtual Hard Disk Image Format 1.0).</summary>
    public static byte[] VhdFooter(long size)
    {
        var f = new byte[FooterSize];
        "conectix"u8.CopyTo(f);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(12), 0x00010000);
        BinaryPrimitives.WriteUInt64BigEndian(f.AsSpan(16), ulong.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(24), (uint)(DateTime.UtcNow - new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
        "ibra"u8.CopyTo(f.AsSpan(28));
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(32), 0x00010000);
        "Wi2k"u8.CopyTo(f.AsSpan(36));
        BinaryPrimitives.WriteUInt64BigEndian(f.AsSpan(40), (ulong)size);
        BinaryPrimitives.WriteUInt64BigEndian(f.AsSpan(48), (ulong)size);
        var (c, h, s) = Geometry(size);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(56), c);
        f[58] = h;
        f[59] = s;
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(60), 2);
        Guid.NewGuid().TryWriteBytes(f.AsSpan(68));
        uint sum = 0;
        foreach (var b in f) sum += b;
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(64), ~sum);
        return f;
    }

    public static (ushort cyl, byte heads, byte spt) Geometry(long size)
    {
        long total = size / 512;
        if (total > 65535L * 16 * 255) total = 65535L * 16 * 255;
        long spt, heads, cth;
        if (total >= 65535L * 16 * 63) { spt = 255; heads = 16; cth = total / spt; }
        else
        {
            spt = 17;
            cth = total / spt;
            heads = (cth + 1023) / 1024;
            if (heads < 4) heads = 4;
            if (cth >= heads * 1024 || heads > 16) { spt = 31; heads = 16; cth = total / spt; }
            if (cth >= heads * 1024) { spt = 63; heads = 16; cth = total / spt; }
        }
        return ((ushort)Math.Min(65535, cth / heads), (byte)heads, (byte)spt);
    }
}
