using System.Text;

namespace IbraDriveCheck.Core.Recovery;

/// <summary>
/// exFAT: الملف المحذوف تبقى مدخلاته (اسم كامل + الحجم + أول كتلة) لكن بعلامة "غير مستخدم".
/// الملفات المكتوبة بدون سلسلة FAT (NoFatChain) تُستعاد بدقة كاملة.
/// </summary>
public sealed class ExFatScanner
{
    private readonly IVolumeSource _src;
    private readonly RecoveryProgress _p;
    public int BytesPerSector { get; private set; }
    public int ClusterSize { get; private set; }
    public long ClusterCount { get; private set; }
    public long HeapOffset { get; private set; }
    private uint _rootCluster;
    private uint[] _fat = Array.Empty<uint>();
    private byte[]? _bitmap;
    private long _id = 1;
    private readonly HashSet<uint> _visited = new();

    public ExFatScanner(IVolumeSource src, RecoveryProgress? progress = null)
    {
        _src = src;
        _p = progress ?? new RecoveryProgress();
    }

    public static bool Detect(ReadOnlySpan<byte> boot) => boot.Length >= 512 && boot.Slice(3, 8).SequenceEqual("EXFAT   "u8);

    public long ClusterOffset(uint n) => HeapOffset + (long)(n - 2) * ClusterSize;
    private bool Valid(uint c) => c >= 2 && c < ClusterCount + 2;

    public bool IsAllocated(uint c)
    {
        if (_bitmap == null || !Valid(c)) return true;
        long i = c - 2;
        return (_bitmap[i >> 3] & (1 << (int)(i & 7))) != 0;
    }

    public bool IsAllocatedOffset(long offset) => offset < HeapOffset || IsAllocated((uint)((offset - HeapOffset) / ClusterSize + 2));

    public void Open()
    {
        var boot = new byte[512];
        _src.ReadAt(0, boot);
        if (!Detect(boot)) throw new InvalidDataException("Not exFAT");
        BytesPerSector = 1 << boot[108];
        ClusterSize = BytesPerSector << boot[109];
        long fatOffset = Le.U32(boot, 80) * (long)BytesPerSector;
        HeapOffset = Le.U32(boot, 88) * (long)BytesPerSector;
        ClusterCount = Le.U32(boot, 92);
        _rootCluster = Le.U32(boot, 96);
        var raw = new byte[(ClusterCount + 2) * 4];
        _src.ReadAt(fatOffset, raw);
        _fat = new uint[ClusterCount + 2];
        for (long i = 0; i < _fat.Length; i++) _fat[i] = Le.U32(raw, (int)(i * 4));

        // خريطة الكتل من مدخل Allocation Bitmap في الجذر
        var root = ReadChain(_rootCluster, long.MaxValue);
        for (int i = 0; i + 32 <= root.Length; i += 32)
        {
            if (root[i] == 0) break;
            if (root[i] == 0x81)
            {
                uint first = Le.U32(root, i + 20);
                long len = (long)Le.U64(root, i + 24);
                if (len > 0 && len < 1L << 30) _bitmap = ReadContiguousOrChain(first, len, false);
                break;
            }
        }
    }

    public List<uint> Chain(uint start, int max = int.MaxValue)
    {
        var list = new List<uint>();
        var seen = new HashSet<uint>();
        uint c = start;
        while (Valid(c) && seen.Add(c) && list.Count < max)
        {
            list.Add(c);
            uint n = _fat[c];
            if (n >= 0xFFFFFFF7 || n < 2) break;
            c = n;
        }
        return list;
    }

    private byte[] ReadChain(uint start, long maxBytes)
    {
        var chain = Chain(start, (int)Math.Min(int.MaxValue, (maxBytes + ClusterSize - 1) / ClusterSize));
        var buf = new byte[(long)chain.Count * ClusterSize];
        for (int i = 0; i < chain.Count; i++) _src.ReadAt(ClusterOffset(chain[i]), buf.AsSpan(i * ClusterSize, ClusterSize));
        return buf;
    }

    private byte[] ReadContiguousOrChain(uint first, long len, bool noFatChain)
    {
        if (!noFatChain) return ReadChain(first, len);
        var buf = new byte[(len + ClusterSize - 1) / ClusterSize * ClusterSize];
        _src.ReadAt(ClusterOffset(first), buf);
        return buf;
    }

    private IEnumerable<uint> ClustersOf(uint first, long len, bool noFatChain)
    {
        long n = (len + ClusterSize - 1) / ClusterSize;
        if (noFatChain) for (long i = 0; i < n; i++) yield return (uint)(first + i);
        else foreach (var c in Chain(first, (int)Math.Min(int.MaxValue, n))) yield return c;
    }

    public RecoveryScan Scan(CancellationToken ct)
    {
        Open();
        var scan = new RecoveryScan { FileSystem = "exFAT", ClusterSize = ClusterSize };
        _p.Phase = "rec.phase.dirs";
        _p.SetTotal(Math.Max(1, ClusterCount));
        _visited.Clear();
        foreach (var c in Chain(_rootCluster)) _visited.Add(c);
        ParseDir(ReadChain(_rootCluster, long.MaxValue), "", false, 0, scan, ct);
        scan.Cancelled = ct.IsCancellationRequested;
        _p.Found = scan.Items.Count;
        return scan;
    }

    /// <summary>مناطق المجلدات الموجودة (لإزالة آثار الأسماء).</summary>
    public List<Extent> LiveDirectoryRegions()
    {
        if (_fat.Length == 0) Open();
        var regions = new List<Extent>();
        var seen = new HashSet<uint>();
        void Add(uint first, long len, bool nfc)
        {
            foreach (var c in ClustersOf(first, len, nfc)) if (seen.Add(c)) regions.Add(new Extent(ClusterOffset(c), ClusterSize));
        }
        void Walk(byte[] data, int depth)
        {
            if (depth > 64) return;
            for (int i = 0; i + 32 <= data.Length; i += 32)
            {
                if (data[i] == 0) break;
                if (data[i] != 0x85) continue;
                int sec = data[i + 1];
                if ((Le.U16(data, i + 4) & 0x10) == 0 || i + 64 > data.Length || data[i + 32] != 0xC0) { i += sec * 32; continue; }
                int s = i + 32;
                bool nfc = (data[s + 1] & 2) != 0;
                uint first = Le.U32(data, s + 20);
                long len = (long)Le.U64(data, s + 24);
                if (Valid(first) && !seen.Contains(first) && len > 0)
                {
                    Add(first, len, nfc);
                    Walk(ReadContiguousOrChain(first, len, nfc), depth + 1);
                }
                i += sec * 32;
            }
        }
        foreach (var c in Chain(_rootCluster)) if (seen.Add(c)) regions.Add(new Extent(ClusterOffset(c), ClusterSize));
        Walk(ReadChain(_rootCluster, long.MaxValue), 0);
        return regions;
    }

    /// <summary>يمسح المدخلات غير المستخدمة (المحذوفة) مع إبقاء نوعها حتى لا يتغير معنى المجلد.</summary>
    public static int ScrubDeletedEntries(Span<byte> dir)
    {
        int n = 0;
        for (int i = 0; i + 32 <= dir.Length; i += 32)
        {
            byte t = dir[i];
            if (t == 0x00) break;
            if ((t & 0x80) != 0) continue;   // مستخدم
            if (dir.Slice(i + 1, 31).IndexOfAnyExcept((byte)0) < 0) continue;
            dir.Slice(i + 1, 31).Clear();
            n++;
        }
        return n;
    }

    private void ParseDir(byte[] data, string path, bool parentDeleted, int depth, RecoveryScan scan, CancellationToken ct)
    {
        if (depth > 64 || ct.IsCancellationRequested) return;
        for (int i = 0; i + 32 <= data.Length; i += 32)
        {
            byte t = data[i];
            if (t == 0x00) break;
            if ((t & 0x7F) != 0x05) continue;
            int sec = data[i + 1];
            if (sec < 2 || i + 32 * (sec + 1) > data.Length) continue;
            int s = i + 32;
            if ((data[s] & 0x7F) != 0x40) continue;
            bool deleted = (t & 0x80) == 0 || parentDeleted;
            bool isDir = (Le.U16(data, i + 4) & 0x10) != 0;
            bool nfc = (data[s + 1] & 2) != 0;
            int nameLen = data[s + 3];
            uint first = Le.U32(data, s + 20);
            long len = (long)Le.U64(data, s + 24);
            var sb = new StringBuilder(nameLen);
            for (int k = 2; k <= sec && sb.Length < nameLen; k++)
            {
                int e = i + 32 * k;
                if ((data[e] & 0x7F) != 0x41) break;
                for (int c = 0; c < 15 && sb.Length < nameLen; c++) sb.Append((char)Le.U16(data, e + 2 + c * 2));
            }
            string name = sb.Length > 0 ? sb.ToString() : $"file_{first}";
            uint ct32 = Le.U32(data, i + 8), mt32 = Le.U32(data, i + 12);
            int skip = sec * 32;

            if (!deleted)
            {
                if (isDir && Valid(first) && len > 0 && !_visited.Contains(first))
                {
                    foreach (var c in ClustersOf(first, len, nfc)) _visited.Add(c);
                    ParseDir(ReadContiguousOrChain(first, len, nfc), Join(path, name), false, depth + 1, scan, ct);
                }
                i += skip;
                continue;
            }

            var item = new RecoveryItem
            {
                Id = _id++,
                Name = name,
                Dir = path,
                IsDirectory = isDir,
                Size = isDir ? 0 : len,
                Created = Le.Dos((ushort)(ct32 >> 16), (ushort)ct32),
                Modified = Le.Dos((ushort)(mt32 >> 16), (ushort)mt32),
                Source = "exFAT",
            };
            if (!isDir)
            {
                if (len == 0) item.Chance = RecoveryChance.Good;
                else if (!Valid(first)) item.Chance = RecoveryChance.Unsupported;
                else
                {
                    // نحاول سلسلة FAT إذا ما زالت سليمة، وإلا نفترض التتابع
                    long need = (len + ClusterSize - 1) / ClusterSize;
                    List<uint> clusters;
                    if (!nfc)
                    {
                        var ch = Chain(first, (int)Math.Min(int.MaxValue, need));
                        clusters = ch.Count == need ? ch : Enumerable.Range(0, (int)Math.Min(need, ClusterCount)).Select(k => (uint)(first + k)).ToList();
                    }
                    else clusters = Enumerable.Range(0, (int)Math.Min(need, ClusterCount)).Select(k => (uint)(first + k)).ToList();
                    long remaining = len;
                    int used = 0;
                    for (int k = 0; k < clusters.Count && remaining > 0; k++)
                    {
                        uint c = clusters[k];
                        if (!Valid(c)) break;
                        if (IsAllocated(c)) used++;
                        long l = Math.Min(ClusterSize, remaining);
                        long off = ClusterOffset(c);
                        if (item.Extents.Count > 0 && item.Extents[^1].Offset + item.Extents[^1].Length == off)
                            item.Extents[^1] = new Extent(item.Extents[^1].Offset, item.Extents[^1].Length + l);
                        else item.Extents.Add(new Extent(off, l));
                        remaining -= l;
                    }
                    item.Chance = remaining > 0 ? RecoveryChance.Poor
                        : used == 0 ? RecoveryChance.Good : used < clusters.Count ? RecoveryChance.Partial : RecoveryChance.Poor;
                }
            }
            scan.Items.Add(item);
            _p.Found = scan.Items.Count;

            if (isDir && Valid(first) && len > 0 && len < 64L << 20 && !_visited.Contains(first))
            {
                foreach (var c in ClustersOf(first, len, true)) _visited.Add(c);
                ParseDir(ReadContiguousOrChain(first, len, true), Join(path, name), true, depth + 1, scan, ct);
            }
            i += skip;
        }
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : a + "/" + b;
}
