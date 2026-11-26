namespace IbraDriveCheck.Core.Recovery;

/// <summary>نظام ملفات عُثر عليه على قرص (قسم موجود أو مفقود أو بقطاع إقلاع تالف).</summary>
public sealed class FsCandidate
{
    public long Offset { get; init; }
    public long Size { get; init; }
    public string FileSystem { get; init; } = "";
    /// <summary>primary = قطاع الإقلاع سليم، backup = يُستخدم النسخة الاحتياطية، table = من جدول الأقسام فقط.</summary>
    public string Boot { get; init; } = "primary";
    /// <summary>عنوان النسخة الاحتياطية (من بداية القرص) وعدد قطاعاتها — لإصلاح القطاع الأصلي.</summary>
    public long BackupOffset { get; init; } = -1;
    public int BackupSectors { get; init; }
    public bool InPartitionTable { get; init; }
    public string Key => $"{Offset}:{FileSystem}";
}

/// <summary>مصدر قراءة لجزء من القرص، مع إمكانية استبدال القطاعات الأولى (قطاع إقلاع من النسخة الاحتياطية).</summary>
public sealed class OffsetVolumeSource : IVolumeSource
{
    private readonly IVolumeSource _inner;
    private readonly long _offset;
    private readonly byte[]? _head;
    private readonly bool _own;

    public OffsetVolumeSource(IVolumeSource inner, long offset, long size, byte[]? replaceHead = null, bool ownsInner = false)
    {
        _inner = inner;
        _offset = offset;
        Size = Math.Min(size, inner.Size - offset);
        _head = replaceHead;
        _own = ownsInner;
    }

    public long Size { get; }

    public void ReadAt(long offset, Span<byte> destination)
    {
        if (offset >= Size) { destination.Clear(); return; }
        int len = (int)Math.Min(destination.Length, Size - offset);
        _inner.ReadAt(_offset + offset, destination[..len]);
        if (len < destination.Length) destination[len..].Clear();
        if (_head != null && offset < _head.Length)
        {
            int n = (int)Math.Min(_head.Length - offset, len);
            _head.AsSpan((int)offset, n).CopyTo(destination);
        }
    }

    public void Dispose()
    {
        if (_own) _inner.Dispose();
    }
}

/// <summary>
/// إنقاذ وحدة "لا تفتح" أو "تطلب تهيئة": يبحث عن أنظمة الملفات في جدول الأقسام وفي كامل بداية القرص،
/// ويستخدم النسخة الاحتياطية من قطاع الإقلاع إذا كان الأصلي تالفاً. القراءة فقط — لا يكتب شيئاً.
/// </summary>
public static class RescueEngine
{
    public static List<FsCandidate> Find(IVolumeSource disk, RecoveryProgress? p = null, long searchBytes = 256L << 20, CancellationToken ct = default)
    {
        p ??= new RecoveryProgress();
        p.Phase = "rescue.phase.search";
        var found = new Dictionary<string, FsCandidate>();
        void Add(FsCandidate c)
        {
            if (c.Size <= 0 || c.Offset < 0 || c.Offset >= disk.Size) return;
            if (!found.TryGetValue(c.Key, out var old) || Rank(c) < Rank(old)) found[c.Key] = c;
        }

        var sector = new byte[512];
        // 1) جدول الأقسام MBR / GPT
        var parts = new List<(long start, long size)>();
        disk.ReadAt(0, sector);
        if (sector[510] == 0x55 && sector[511] == 0xAA && !LooksLikeBoot(sector))
        {
            bool gpt = false;
            for (int i = 0; i < 4; i++)
            {
                int e = 446 + i * 16;
                byte type = sector[e + 4];
                long lba = Le.U32(sector, e + 8), cnt = Le.U32(sector, e + 12);
                if (type == 0xEE) gpt = true;
                else if (type != 0 && lba > 0 && cnt > 0) parts.Add((lba * 512, cnt * 512));
            }
            if (gpt)
            {
                var hdr = new byte[512];
                disk.ReadAt(512, hdr);
                if (hdr.AsSpan(0, 8).SequenceEqual("EFI PART"u8))
                {
                    long entLba = Le.I64(hdr, 72);
                    int count = (int)Math.Min(Le.U32(hdr, 80), 256u), esize = (int)Le.U32(hdr, 84);
                    if (esize is >= 128 and <= 1024)
                    {
                        var ents = new byte[count * esize];
                        disk.ReadAt(entLba * 512, ents);
                        for (int i = 0; i < count; i++)
                        {
                            var e = ents.AsSpan(i * esize, esize);
                            if (e[..16].IndexOfAnyExcept((byte)0) < 0) continue;
                            long first = Le.I64(e, 32), last = Le.I64(e, 40);
                            if (first > 0 && last >= first) parts.Add((first * 512, (last - first + 1) * 512));
                        }
                    }
                }
            }
        }
        else if (LooksLikeBoot(sector)) parts.Add((0, disk.Size));   // نظام ملفات بدون جدول أقسام (شائع في الفلاشات)

        foreach (var (start, size) in parts)
        {
            if (Probe(disk, start, size) is { } c) Add(c.with2(true));
            else Add(new FsCandidate { Offset = start, Size = size, FileSystem = "?", Boot = "table", InPartitionTable = true });
        }

        // 2) بحث في بداية القرص عن قطاعات إقلاع (أقسام ممسوحة من الجدول)
        long limit = Math.Min(disk.Size, searchBytes);
        var buf = new byte[1 << 20];
        p.SetTotal(limit);
        for (long pos = 0; pos < limit && !ct.IsCancellationRequested; pos += buf.Length)
        {
            int n = (int)Math.Min(buf.Length, limit - pos);
            disk.ReadAt(pos, buf.AsSpan(0, n));
            for (int o = 0; o + 512 <= n; o += 512)
            {
                var s = buf.AsSpan(o, 512);
                if (s[510] != 0x55 || s[511] != 0xAA || !LooksLikeBoot(s)) continue;
                long at = pos + o;
                var c = FromBoot(disk, s.ToArray(), at);
                if (c != null) Add(c);
            }
            p.Set(pos + n);
        }

        // 3) نسخة NTFS الاحتياطية في آخر كل قسم معروف
        foreach (var (start, size) in parts)
        {
            if (size < 1 << 20) continue;
            var last = new byte[512];
            disk.ReadAt(start + size - 512, last);
            if (NtfsScanner.Detect(last) && last[510] == 0x55)
            {
                long total = Le.I64(last, 0x28) * Le.U16(last, 11);
                long vstart = start + size - 512 - total;
                if (vstart >= 0) Add(new FsCandidate { Offset = vstart, Size = total + 512, FileSystem = "NTFS", Boot = "backup", BackupOffset = start + size - 512, BackupSectors = 1, InPartitionTable = vstart == start });
            }
        }
        return found.Values.Where(c => c.FileSystem != "?" || !found.Values.Any(o => o != c && o.Offset == c.Offset))
                           .OrderBy(c => c.Offset).ToList();
    }

    private static int Rank(FsCandidate c) => c.Boot switch { "primary" => 0, "backup" => 1, _ => 2 } - (c.InPartitionTable ? 0 : 0);

    private static FsCandidate with2(this FsCandidate c, bool inTable) => new()
    {
        Offset = c.Offset, Size = c.Size, FileSystem = c.FileSystem, Boot = c.Boot, BackupOffset = c.BackupOffset, BackupSectors = c.BackupSectors, InPartitionTable = inTable,
    };

    private static bool LooksLikeBoot(ReadOnlySpan<byte> s) =>
        NtfsScanner.Detect(s) || ExFatScanner.Detect(s) || FatScanner.Detect(s) is "FAT32" or "FAT16";

    /// <summary>يفحص بداية قسم: قطاع إقلاع سليم، أو نسخة احتياطية.</summary>
    private static FsCandidate? Probe(IVolumeSource disk, long start, long size)
    {
        var s0 = new byte[512];
        disk.ReadAt(start, s0);
        if (LooksLikeBoot(s0) && FromBoot(disk, s0, start) is { Boot: "primary" } ok) return ok;

        // FAT32: نسخة احتياطية في القطاع 6 عادة
        var b = new byte[512];
        disk.ReadAt(start + 6 * 512, b);
        if (FatScanner.Detect(b) == "FAT32")
            return new FsCandidate { Offset = start, Size = size, FileSystem = "FAT32", Boot = "backup", BackupOffset = start + 6 * 512, BackupSectors = 1 };
        // exFAT: منطقة احتياطية من 12 قطاعاً تبدأ عند القطاع 12
        disk.ReadAt(start + 12 * 512, b);
        if (ExFatScanner.Detect(b))
            return new FsCandidate { Offset = start, Size = size, FileSystem = "exFAT", Boot = "backup", BackupOffset = start + 12 * 512, BackupSectors = 12 };
        // NTFS: نسخة في آخر القسم
        if (size > 1024)
        {
            disk.ReadAt(start + size - 512, b);
            if (NtfsScanner.Detect(b))
                return new FsCandidate { Offset = start, Size = size, FileSystem = "NTFS", Boot = "backup", BackupOffset = start + size - 512, BackupSectors = 1 };
        }
        return null;
    }

    /// <summary>من قطاع إقلاع عند عنوان at: هل هو الأصلي (بداية قسم) أم نسخة احتياطية؟ نتحقق بموقع النسخة الثانية.</summary>
    private static FsCandidate? FromBoot(IVolumeSource disk, byte[] s, long at)
    {
        var other = new byte[512];
        bool Same(long pos) { if (pos < 0 || pos + 512 > disk.Size) return false; disk.ReadAt(pos, other); return other.AsSpan(0, 90).SequenceEqual(s.AsSpan(0, 90)); }

        if (NtfsScanner.Detect(s))
        {
            long total = Le.I64(s, 0x28) * Math.Max(512, (int)Le.U16(s, 11));
            if (total <= 0 || total > disk.Size) return null;
            if (Same(at + total) || at + total >= disk.Size) return new FsCandidate { Offset = at, Size = total + 512, FileSystem = "NTFS", Boot = "primary" };
            if (at - total >= 0) return new FsCandidate { Offset = at - total, Size = total + 512, FileSystem = "NTFS", Boot = "backup", BackupOffset = at, BackupSectors = 1 };
            return null;
        }
        if (ExFatScanner.Detect(s))
        {
            long len = (long)Le.U64(s, 72) << s[108];
            if (len <= 0) return null;
            var b = new byte[512];
            disk.ReadAt(at + 12 * 512, b);
            if (ExFatScanner.Detect(b)) return new FsCandidate { Offset = at, Size = len, FileSystem = "exFAT", Boot = "primary" };
            if (at >= 12 * 512) return new FsCandidate { Offset = at - 12 * 512, Size = len, FileSystem = "exFAT", Boot = "backup", BackupOffset = at, BackupSectors = 12 };
            return null;
        }
        var fat = FatScanner.Detect(s);
        if (fat is "FAT32" or "FAT16")
        {
            int bps = Le.U16(s, 11);
            long tot = Le.U16(s, 19) != 0 ? Le.U16(s, 19) : Le.U32(s, 32);
            if (tot <= 0) return null;
            if (fat == "FAT32")
            {
                int bk = Le.U16(s, 50);
                if (bk is > 0 and < 32 && !Same(at + bk * (long)bps) && at - bk * (long)bps >= 0)
                {
                    // هذه هي النسخة الاحتياطية والقطاع الأصلي تالف
                    var prim = new byte[512];
                    disk.ReadAt(at - bk * (long)bps, prim);
                    if (FatScanner.Detect(prim) != "FAT32")
                        return new FsCandidate { Offset = at - bk * (long)bps, Size = tot * bps, FileSystem = fat, Boot = "backup", BackupOffset = at, BackupSectors = 1 };
                    return null;   // نسخة احتياطية لقسم سليم — سيظهر من قطاعه الأصلي
                }
            }
            return new FsCandidate { Offset = at, Size = tot * bps, FileSystem = fat, Boot = "primary" };
        }
        return null;
    }

    /// <summary>يفتح المرشح كقسم قابل للقراءة (مع قطاع الإقلاع الاحتياطي إذا لزم).</summary>
    public static IVolumeSource Open(IVolumeSource disk, FsCandidate c)
    {
        byte[]? head = null;
        if (c.Boot == "backup" && c.BackupOffset >= 0)
        {
            head = new byte[c.BackupSectors * 512];
            disk.ReadAt(c.BackupOffset, head);
        }
        return new OffsetVolumeSource(disk, c.Offset, c.Size, head);
    }

    /// <summary>هل نظام الملفات الأصلي (عند البداية) تالف بينما النسخة الاحتياطية سليمة؟ (قابل للإصلاح بكتابة النسخة)</summary>
    public static bool IsRepairable(FsCandidate c) => c.Boot == "backup" && c.BackupOffset >= 0 && c.BackupSectors > 0;
}
