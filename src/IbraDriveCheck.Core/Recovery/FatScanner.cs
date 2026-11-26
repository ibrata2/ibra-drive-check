using System.Text;

namespace IbraDriveCheck.Core.Recovery;

/// <summary>
/// FAT16 / FAT32: يمر على كل المجلدات (الموجودة والمحذوفة) ويجد المدخلات المحذوفة (0xE5) مع أسمائها الطويلة.
/// الملف المحذوف تُمسح سلسلة كتله من جدول FAT، لذلك نفترض أن كتله متتالية (يصح لأغلب الملفات).
/// </summary>
public sealed class FatScanner
{
    private readonly IVolumeSource _src;
    private readonly RecoveryProgress _p;
    public int BytesPerSector { get; private set; }
    public int ClusterSize { get; private set; }
    public bool IsFat32 { get; private set; }
    public long ClusterCount { get; private set; }
    public long DataStart { get; private set; }
    private long _rootOffset, _rootSize;
    private uint _rootCluster;
    private uint[] _fat = Array.Empty<uint>();
    private long _id = 1;
    private readonly HashSet<uint> _visited = new();

    private readonly bool _live;

    public FatScanner(IVolumeSource src, RecoveryProgress? progress = null, bool includeLive = false)
    {
        _src = src;
        _p = progress ?? new RecoveryProgress();
        _live = includeLive;
    }

    /// <summary>يرجع "FAT32" أو "FAT16" أو "FAT12" أو null.</summary>
    public static string? Detect(ReadOnlySpan<byte> boot)
    {
        if (boot.Length < 512 || boot[510] != 0x55 || boot[511] != 0xAA) return null;
        int bps = Le.U16(boot, 11), spc = boot[13], rsv = Le.U16(boot, 14), nfat = boot[16], rootEnt = Le.U16(boot, 17);
        if (bps is not (512 or 1024 or 2048 or 4096) || spc == 0 || (spc & (spc - 1)) != 0 || nfat == 0 || rsv == 0) return null;
        long fatSz = Le.U16(boot, 22) != 0 ? Le.U16(boot, 22) : Le.U32(boot, 36);
        long tot = Le.U16(boot, 19) != 0 ? Le.U16(boot, 19) : Le.U32(boot, 32);
        long rootSec = (rootEnt * 32 + bps - 1) / bps;
        long dataSec = tot - (rsv + nfat * fatSz + rootSec);
        if (fatSz == 0 || dataSec <= 0) return null;
        long clusters = dataSec / spc;
        return clusters < 4085 ? "FAT12" : clusters < 65525 ? "FAT16" : "FAT32";
    }

    public long ClusterOffset(uint n) => DataStart + (long)(n - 2) * ClusterSize;

    public bool IsAllocated(uint cluster) => cluster < 2 || cluster >= _fat.Length || _fat[cluster] != 0;

    public bool IsAllocatedOffset(long offset)
    {
        if (offset < DataStart) return true;
        return IsAllocated((uint)((offset - DataStart) / ClusterSize + 2));
    }

    private bool IsEoc(uint v) => IsFat32 ? v >= 0x0FFFFFF8 : v >= 0xFFF8;
    private bool ValidCluster(uint c) => c >= 2 && c < ClusterCount + 2;

    public void Open()
    {
        var boot = new byte[512];
        _src.ReadAt(0, boot);
        var type = Detect(boot) ?? throw new InvalidDataException("Not FAT");
        if (type == "FAT12") throw new NotSupportedException("FAT12");
        IsFat32 = type == "FAT32";
        BytesPerSector = Le.U16(boot, 11);
        int spc = boot[13], rsv = Le.U16(boot, 14), nfat = boot[16], rootEnt = Le.U16(boot, 17);
        long fatSz = Le.U16(boot, 22) != 0 ? Le.U16(boot, 22) : Le.U32(boot, 36);
        long tot = Le.U16(boot, 19) != 0 ? Le.U16(boot, 19) : Le.U32(boot, 32);
        ClusterSize = BytesPerSector * spc;
        long rootSec = (rootEnt * 32 + BytesPerSector - 1) / BytesPerSector;
        long firstData = rsv + nfat * fatSz + rootSec;
        DataStart = firstData * BytesPerSector;
        ClusterCount = (tot - firstData) / spc;
        _rootOffset = (rsv + nfat * fatSz) * (long)BytesPerSector;
        _rootSize = rootEnt * 32L;
        _rootCluster = IsFat32 ? Le.U32(boot, 44) : 0;

        long fatBytes = fatSz * BytesPerSector;
        long entries = Math.Min(ClusterCount + 2, fatBytes / (IsFat32 ? 4 : 2));
        var raw = new byte[entries * (IsFat32 ? 4 : 2)];
        _src.ReadAt(rsv * (long)BytesPerSector, raw);
        _fat = new uint[entries];
        for (long i = 0; i < entries; i++)
            _fat[i] = IsFat32 ? Le.U32(raw, (int)(i * 4)) & 0x0FFFFFFF : Le.U16(raw, (int)(i * 2));
    }

    public List<uint> Chain(uint start, int max = int.MaxValue)
    {
        var list = new List<uint>();
        var seen = new HashSet<uint>();
        uint c = start;
        while (ValidCluster(c) && seen.Add(c) && list.Count < max)
        {
            list.Add(c);
            uint next = _fat[c];
            if (IsEoc(next) || next < 2) break;
            c = next;
        }
        return list;
    }

    private byte[] ReadClusters(List<uint> clusters)
    {
        var buf = new byte[(long)clusters.Count * ClusterSize];
        for (int i = 0; i < clusters.Count; i++) _src.ReadAt(ClusterOffset(clusters[i]), buf.AsSpan(i * ClusterSize, ClusterSize));
        return buf;
    }

    public RecoveryScan Scan(CancellationToken ct)
    {
        Open();
        var scan = new RecoveryScan { FileSystem = IsFat32 ? "FAT32" : "FAT16", ClusterSize = ClusterSize };
        _p.Phase = "rec.phase.dirs";
        _p.SetTotal(Math.Max(1, ClusterCount));
        _visited.Clear();
        if (IsFat32)
        {
            var chain = Chain(_rootCluster);
            foreach (var c in chain) _visited.Add(c);
            ParseDir(ReadClusters(chain), "", false, 0, scan, ct);
        }
        else
        {
            var root = new byte[_rootSize];
            _src.ReadAt(_rootOffset, root);
            ParseDir(root, "", false, 0, scan, ct);
        }
        scan.Cancelled = ct.IsCancellationRequested;
        _p.Found = scan.Items.Count;
        return scan;
    }

    /// <summary>كل مناطق المجلدات الموجودة (لإزالة آثار الأسماء): (عنوان، طول).</summary>
    public List<Extent> LiveDirectoryRegions()
    {
        if (_fat.Length == 0) Open();
        var regions = new List<Extent>();
        var seen = new HashSet<uint>();
        void Walk(byte[] data, int depth)
        {
            if (depth > 64) return;
            for (int i = 0; i + 32 <= data.Length; i += 32)
            {
                byte b0 = data[i];
                if (b0 == 0x00) break;
                if (b0 == 0xE5 || b0 == 0x2E) continue;
                byte attr = data[i + 11];
                if (attr == 0x0F || (attr & 0x08) != 0 || (attr & 0x10) == 0) continue;
                uint start = (uint)(IsFat32 ? Le.U16(data, i + 20) << 16 : 0) | Le.U16(data, i + 26);
                if (!ValidCluster(start) || seen.Contains(start)) continue;
                var chain = Chain(start);
                foreach (var c in chain) { seen.Add(c); regions.Add(new Extent(ClusterOffset(c), ClusterSize)); }
                Walk(ReadClusters(chain), depth + 1);
            }
        }
        if (IsFat32)
        {
            var chain = Chain(_rootCluster);
            foreach (var c in chain) { seen.Add(c); regions.Add(new Extent(ClusterOffset(c), ClusterSize)); }
            Walk(ReadClusters(chain), 0);
        }
        else
        {
            regions.Add(new Extent(_rootOffset, _rootSize));
            var root = new byte[_rootSize];
            _src.ReadAt(_rootOffset, root);
            Walk(root, 0);
        }
        return regions;
    }

    /// <summary>
    /// يمسح أسماء ومواقع المدخلات المحذوفة داخل مقطع مجلد (يبقي 0xE5 حتى تبقى "محذوفة").
    /// يرجع عدد المدخلات التي تم مسحها.
    /// </summary>
    public static int ScrubDeletedEntries(Span<byte> dir)
    {
        int n = 0;
        for (int i = 0; i + 32 <= dir.Length; i += 32)
        {
            if (dir[i] == 0x00) break;
            if (dir[i] != 0xE5) continue;
            if (dir.Slice(i + 1, 31).IndexOfAnyExcept((byte)0) < 0) continue;
            dir.Slice(i + 1, 31).Clear();
            n++;
        }
        return n;
    }

    private void ParseDir(byte[] data, string path, bool parentDeleted, int depth, RecoveryScan scan, CancellationToken ct)
    {
        if (depth > 64 || ct.IsCancellationRequested) return;
        var lfn = new List<(int ord, byte checksum, string part)>();
        for (int i = 0; i + 32 <= data.Length; i += 32)
        {
            byte b0 = data[i];
            if (b0 == 0x00) break;
            byte attr = data[i + 11];
            if (attr == 0x0F)
            {
                lfn.Add((b0, data[i + 13], LfnPart(data, i)));
                continue;
            }
            var lfnParts = lfn.ToList();
            lfn.Clear();
            if (b0 == 0x2E || (attr & 0x08) != 0) continue;   // . و .. واسم القرص
            bool deleted = b0 == 0xE5 || parentDeleted;
            bool isDir = (attr & 0x10) != 0;
            uint start = (uint)(IsFat32 ? Le.U16(data, i + 20) << 16 : 0) | Le.U16(data, i + 26);
            long size = Le.U32(data, i + 28);
            string name = BuildName(data, i, lfnParts, b0 == 0xE5);

            if (!deleted)
            {
                if (_live && !isDir) scan.Items.Add(LiveItem(name, path, start, size, data, i));
                if (isDir && ValidCluster(start) && !_visited.Contains(start))
                {
                    var chain = Chain(start);
                    foreach (var c in chain) _visited.Add(c);
                    _p.Add(chain.Count);
                    ParseDir(ReadClusters(chain), Join(path, name), false, depth + 1, scan, ct);
                }
                continue;
            }

            var item = new RecoveryItem
            {
                Id = _id++,
                Name = name,
                Dir = path,
                IsDirectory = isDir,
                Size = isDir ? 0 : size,
                Modified = Le.Dos(Le.U16(data, i + 24), Le.U16(data, i + 22)),
                Created = Le.Dos(Le.U16(data, i + 16), Le.U16(data, i + 14)),
                Source = IsFat32 ? "FAT32" : "FAT16",
            };
            if (!isDir)
            {
                if (size == 0) item.Chance = RecoveryChance.Good;
                else if (!ValidCluster(start)) item.Chance = RecoveryChance.Unsupported;
                else
                {
                    long clusters = (size + ClusterSize - 1) / ClusterSize;
                    long off = ClusterOffset(start);
                    if (start + clusters > ClusterCount + 2) item.Chance = RecoveryChance.Poor;
                    item.Extents.Add(new Extent(off, Math.Min(size, _src.Size - off)));
                    if (item.Chance != RecoveryChance.Poor)
                    {
                        long used = 0;
                        for (long c = 0; c < clusters; c++) if (IsAllocated((uint)(start + c))) used++;
                        item.Chance = used == 0 ? RecoveryChance.Good : used < clusters ? RecoveryChance.Partial : RecoveryChance.Poor;
                    }
                }
            }
            scan.Items.Add(item);
            _p.Found = scan.Items.Count;

            // مجلد محذوف: نقرأ كتله المتتالية ما دامت تبدو كمجلد
            if (isDir && ValidCluster(start) && !_visited.Contains(start))
            {
                var buf = new List<byte>();
                var one = new byte[ClusterSize];
                for (uint c = start; c < start + 64 && ValidCluster(c) && !_visited.Contains(c); c++)
                {
                    if (c != start && IsAllocated(c)) break;
                    _src.ReadAt(ClusterOffset(c), one);
                    if (c == start && !(one[0] == 0x2E && one[1] == 0x20)) break;   // لم يعد مجلداً (كُتب فوقه)
                    _visited.Add(c);
                    buf.AddRange(one);
                    if (HasEnd(one)) break;
                }
                if (buf.Count > 0) ParseDir(buf.ToArray(), Join(path, name), true, depth + 1, scan, ct);
            }
        }
    }

    private RecoveryItem LiveItem(string name, string path, uint start, long size, byte[] data, int i)
    {
        var item = new RecoveryItem
        {
            Id = _id++, Name = name, Dir = path, Size = size, Deleted = false, Chance = RecoveryChance.Good,
            Modified = Le.Dos(Le.U16(data, i + 24), Le.U16(data, i + 22)),
            Created = Le.Dos(Le.U16(data, i + 16), Le.U16(data, i + 14)),
            Source = IsFat32 ? "FAT32" : "FAT16",
        };
        long remaining = size;
        foreach (var c in size > 0 ? Chain(start) : new List<uint>())
        {
            if (remaining <= 0) break;
            long len = Math.Min(ClusterSize, remaining);
            long off = ClusterOffset(c);
            if (item.Extents.Count > 0 && item.Extents[^1].Offset + item.Extents[^1].Length == off)
                item.Extents[^1] = new Extent(item.Extents[^1].Offset, item.Extents[^1].Length + len);
            else item.Extents.Add(new Extent(off, len));
            remaining -= len;
        }
        if (remaining > 0) item.Chance = RecoveryChance.Partial;
        return item;
    }

    private static bool HasEnd(byte[] d)
    {
        for (int i = 0; i + 32 <= d.Length; i += 32) if (d[i] == 0) return true;
        return false;
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : a + "/" + b;

    private static string LfnPart(byte[] d, int i)
    {
        var sb = new StringBuilder(13);
        foreach (var (o, n) in new[] { (1, 5), (14, 6), (28, 2) })
            for (int k = 0; k < n; k++)
            {
                char ch = (char)Le.U16(d, i + o + k * 2);
                if (ch == '\0' || ch == '￿') return sb.ToString();
                sb.Append(ch);
            }
        return sb.ToString();
    }

    private static byte SfnChecksum(ReadOnlySpan<byte> name11)
    {
        byte sum = 0;
        for (int i = 0; i < 11; i++) sum = (byte)(((sum & 1) << 7) + (sum >> 1) + name11[i]);
        return sum;
    }

    private static string BuildName(byte[] d, int i, List<(int ord, byte checksum, string part)> lfn, bool deleted)
    {
        if (lfn.Count > 0)
        {
            bool ok;
            if (!deleted) ok = lfn.All(l => l.checksum == SfnChecksum(d.AsSpan(i, 11)));
            else ok = lfn.All(l => l.checksum == lfn[0].checksum);   // الحرف الأول من الاسم القصير ضاع، فنتحقق من تطابق المجموعة
            if (ok)
            {
                var sb = new StringBuilder();
                for (int k = lfn.Count - 1; k >= 0; k--) sb.Append(lfn[k].part);
                var s = sb.ToString();
                if (s.Length > 0) return s;
            }
        }
        string bas = Encoding.Latin1.GetString(d, i, 8).TrimEnd();
        string ext = Encoding.Latin1.GetString(d, i + 8, 3).TrimEnd();
        // علامات الأحرف الصغيرة التي يكتبها ويندوز بدل الاسم الطويل
        byte nt = d[i + 12];
        if ((nt & 0x08) != 0) bas = bas.ToLowerInvariant();
        if ((nt & 0x10) != 0) ext = ext.ToLowerInvariant();
        if (deleted && bas.Length > 0) bas = "_" + bas[1..];
        return ext.Length > 0 ? $"{bas}.{ext}" : bas;
    }
}
