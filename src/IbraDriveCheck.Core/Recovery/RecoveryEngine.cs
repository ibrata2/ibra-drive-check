namespace IbraDriveCheck.Core.Recovery;

public sealed class RecoverResult
{
    public int Files;
    public int Failed;
    public long Bytes;
    public List<string> Errors = new();
}

/// <summary>يكتشف نظام الملفات ويشغل الفاحص المناسب، ثم البحث العميق اختيارياً، ويستعيد الملفات المحددة.</summary>
public static class RecoveryEngine
{
    public static string? DetectFileSystem(IVolumeSource src)
    {
        var boot = new byte[512];
        src.ReadAt(0, boot);
        if (NtfsScanner.Detect(boot)) return "NTFS";
        if (ExFatScanner.Detect(boot)) return "exFAT";
        return FatScanner.Detect(boot);
    }

    public static RecoveryScan Scan(IVolumeSource src, bool deep, RecoveryProgress p, CancellationToken ct, bool includeLive = false)
    {
        p.Phase = "rec.phase.detect";
        string? fs = DetectFileSystem(src);
        RecoveryScan scan;
        Func<long, bool>? allocated = null;
        long dataStart = 0;
        int unit = 512;
        try
        {
            switch (fs)
            {
                case "NTFS":
                {
                    var s = new NtfsScanner(src, p, includeLive);
                    scan = s.Scan(ct);
                    allocated = s.IsAllocatedOffset;
                    unit = s.ClusterSize;
                    break;
                }
                case "exFAT":
                {
                    var s = new ExFatScanner(src, p, includeLive);
                    scan = s.Scan(ct);
                    allocated = s.IsAllocatedOffset;
                    dataStart = s.HeapOffset;
                    unit = s.ClusterSize;
                    break;
                }
                case "FAT32":
                case "FAT16":
                {
                    var s = new FatScanner(src, p, includeLive);
                    scan = s.Scan(ct);
                    allocated = s.IsAllocatedOffset;
                    dataStart = s.DataStart;
                    unit = s.ClusterSize;
                    break;
                }
                default:
                    scan = new RecoveryScan { FileSystem = fs ?? "RAW" };
                    scan.Notes.Add(fs == "FAT12" ? "rec.note.fat12" : "rec.note.unknownFs");
                    deep = true;
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException)
        {
            // نظام ملفات تالف: نكمل بالبحث العميق على كامل المساحة
            scan = new RecoveryScan { FileSystem = fs ?? "RAW", Error = ex.Message };
            scan.Notes.Add("rec.note.corruptFs");
            allocated = null;
            dataStart = 0;
            unit = 512;
            deep = true;
        }

        if (deep && !ct.IsCancellationRequested)
        {
            long nextId = scan.Items.Count == 0 ? 1 : scan.Items.Max(i => i.Id) + 1;
            var carved = new Carver(src, p).Carve(dataStart, unit, allocated, nextId, ct);
            scan.Items.AddRange(carved);
        }
        scan.Cancelled = ct.IsCancellationRequested;
        p.Found = scan.Items.Count;
        p.Phase = "rec.phase.done";
        return scan;
    }

    private static readonly char[] Bad = Path.GetInvalidFileNameChars();

    public static string SafeName(string name)
    {
        var s = new string(name.Select(c => Bad.Contains(c) || c < 32 ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (s.Length == 0) s = "_";
        if (s.Length > 180) s = s[..180];
        string up = Path.GetFileNameWithoutExtension(s).ToUpperInvariant();
        if (up is "CON" or "PRN" or "AUX" or "NUL" || up.Length == 4 && (up.StartsWith("COM") || up.StartsWith("LPT")) && char.IsDigit(up[3])) s = "_" + s;
        return s;
    }

    /// <summary>يستعيد الملفات إلى مجلد آخر مع الحفاظ على شجرة المجلدات.</summary>
    public static RecoverResult Recover(IVolumeSource src, IEnumerable<RecoveryItem> items, string outputRoot, RecoveryProgress p, CancellationToken ct)
    {
        var res = new RecoverResult();
        var list = items.Where(i => !i.IsDirectory).ToList();
        p.Phase = "rec.phase.recover";
        p.SetTotal(Math.Max(1, list.Sum(i => Math.Max(1, i.Size))));
        var buf = new byte[4 << 20];
        foreach (var item in list)
        {
            if (ct.IsCancellationRequested) break;
            string dir = outputRoot;
            foreach (var part in item.Dir.Split('/', StringSplitOptions.RemoveEmptyEntries)) dir = Path.Combine(dir, SafeName(part));
            string target = Path.Combine(dir, SafeName(item.Name));
            try
            {
                Directory.CreateDirectory(dir);
                target = Unique(target);
                using (var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    if (item.Resident != null) fs.Write(item.Resident, 0, (int)Math.Min(item.Resident.Length, item.Size));
                    else
                    {
                        long remaining = item.Size;
                        foreach (var e in item.Extents)
                        {
                            long left = Math.Min(e.Length, remaining);
                            long off = e.Offset;
                            while (left > 0)
                            {
                                if (ct.IsCancellationRequested) break;
                                int n = (int)Math.Min(buf.Length, left);
                                if (e.IsSparse) Array.Clear(buf, 0, n);
                                else src.ReadAt(off, buf.AsSpan(0, n));
                                fs.Write(buf, 0, n);
                                off += n;
                                left -= n;
                                remaining -= n;
                                p.Add(n);
                            }
                            if (remaining <= 0) break;
                        }
                    }
                }
                try
                {
                    if (item.Modified is DateTime m) File.SetLastWriteTimeUtc(target, m.ToUniversalTime());
                    if (item.Created is DateTime c) File.SetCreationTimeUtc(target, c.ToUniversalTime());
                }
                catch { }
                res.Files++;
                res.Bytes += item.Size;
            }
            catch (Exception ex)
            {
                res.Failed++;
                if (res.Errors.Count < 50) res.Errors.Add($"{item.FullPath}: {ex.Message}");
            }
            p.Found = res.Files;
        }
        return res;
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }
}
