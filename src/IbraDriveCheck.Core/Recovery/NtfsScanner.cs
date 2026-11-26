namespace IbraDriveCheck.Core.Recovery;

/// <summary>
/// يقرأ جدول MFT مباشرة ويجد السجلات المحذوفة (غير المستخدمة) مع أسمائها ومساراتها ومواقع بياناتها.
/// </summary>
public sealed class NtfsScanner
{
    private readonly IVolumeSource _src;
    private readonly RecoveryProgress _p;
    public int BytesPerSector { get; private set; }
    public int ClusterSize { get; private set; }
    public int RecordSize { get; private set; }
    public long MftLcn { get; private set; }
    public long TotalClusters { get; private set; }
    private byte[]? _bitmap;

    public NtfsScanner(IVolumeSource src, RecoveryProgress? progress = null)
    {
        _src = src;
        _p = progress ?? new RecoveryProgress();
    }

    public static bool Detect(ReadOnlySpan<byte> boot) => boot.Length >= 512 && boot.Slice(3, 8).SequenceEqual("NTFS    "u8);

    private sealed class DataSeg
    {
        public long StartVcn;
        public List<Extent> Runs = new();
        public long RealSize = -1;
        public bool Compressed, Encrypted;
    }

    private sealed class Rec
    {
        public long No;
        public ushort Seq;
        public bool InUse, IsDir, HasAttrList;
        public long BaseRef;
        public string? Name;
        public int NameSpace = 99;
        public long ParentRef;
        public long FnSize;
        public DateTime? Created, Modified;
        public byte[]? Resident;
        public List<DataSeg> Data = new();
    }

    private readonly record struct DirInfo(string Name, long ParentRef, ushort Seq, bool InUse);

    public RecoveryScan Scan(CancellationToken ct)
    {
        var scan = new RecoveryScan { FileSystem = "NTFS" };
        var boot = new byte[512];
        _src.ReadAt(0, boot);
        if (!Detect(boot)) throw new InvalidDataException("Not NTFS");
        BytesPerSector = Le.U16(boot, 11);
        int spcRaw = boot[13];
        int spc = spcRaw <= 0x80 ? spcRaw : 1 << (256 - spcRaw);
        ClusterSize = BytesPerSector * spc;
        TotalClusters = Le.I64(boot, 0x28) / Math.Max(1, spc);
        MftLcn = Le.I64(boot, 0x30);
        int cpr = (sbyte)boot[0x40];
        RecordSize = cpr > 0 ? cpr * ClusterSize : 1 << -cpr;
        if (ClusterSize <= 0 || RecordSize < 512 || RecordSize > 65536) throw new InvalidDataException("Bad NTFS boot sector");
        scan.ClusterSize = ClusterSize;
        scan.MftRecordSize = RecordSize;

        // سجل $MFT نفسه يحدد كل أجزاء الجدول
        var r0 = new byte[RecordSize];
        _src.ReadAt(MftLcn * ClusterSize, r0);
        var mftRec = ParseRecord(r0, 0) ?? throw new InvalidDataException("Cannot read $MFT record");
        var mftData = mftRec.Data.OrderBy(d => d.StartVcn).ToList();
        var mftRuns = mftData.SelectMany(d => d.Runs).ToList();
        long mftSize = mftData.FirstOrDefault()?.RealSize ?? 0;
        long totalRecords = mftSize / RecordSize;
        if (totalRecords <= 0) throw new InvalidDataException("Empty MFT");

        var dirs = new Dictionary<long, DirInfo>();
        var deleted = new List<Rec>();
        var extData = new Dictionary<long, List<DataSeg>>();
        Rec? bitmapRec = null;
        long unused = 0;

        _p.Phase = "rec.phase.mft";
        _p.SetTotal(totalRecords);
        int chunkRecords = Math.Max(1, (4 << 20) / RecordSize);
        var chunk = new byte[chunkRecords * RecordSize];
        long recNo = 0;
        foreach (var run in mftRuns)
        {
            if (recNo >= totalRecords || ct.IsCancellationRequested) break;
            long runBytes = run.Length;
            long pos = 0;
            while (pos < runBytes && recNo < totalRecords)
            {
                if (ct.IsCancellationRequested) { scan.Cancelled = true; break; }
                int n = (int)Math.Min(chunkRecords, Math.Min((runBytes - pos) / RecordSize, totalRecords - recNo));
                if (n <= 0) break;
                var span = chunk.AsSpan(0, n * RecordSize);
                if (run.IsSparse) span.Clear();
                else _src.ReadAt(run.Offset + pos, span);
                for (int i = 0; i < n; i++, recNo++)
                {
                    var buf = span.Slice(i * RecordSize, RecordSize);
                    var rec = ParseRecord(buf, recNo);
                    if (rec == null || !rec.InUse) unused++;
                    if (rec == null) continue;
                    if (recNo == 6) bitmapRec = rec;
                    if (rec.BaseRef != 0)
                    {
                        if (!rec.InUse && rec.Data.Count > 0)
                        {
                            if (!extData.TryGetValue(rec.BaseRef, out var l)) extData[rec.BaseRef] = l = new();
                            l.AddRange(rec.Data);
                        }
                        continue;
                    }
                    if (rec.IsDir && rec.Name != null) dirs[recNo] = new DirInfo(rec.Name, rec.ParentRef, rec.Seq, rec.InUse);
                    if (!rec.InUse && rec.Name != null && recNo >= 24) deleted.Add(rec);
                }
                pos += (long)n * RecordSize;
                _p.Set(recNo);
            }
        }
        if (ct.IsCancellationRequested) scan.Cancelled = true;
        scan.UnusedMftRecords = unused;

        // خريطة الكتل المستخدمة حالياً ($Bitmap)
        if (bitmapRec != null) _bitmap = ReadData(bitmapRec.Data, (TotalClusters + 7) / 8);

        _p.Phase = "rec.phase.build";
        long id = 1;
        foreach (var r in deleted)
        {
            if (!r.IsDir && r.Data.Count == 0 && r.Resident == null && r.HasAttrList && extData.TryGetValue(r.No, out var ext))
                r.Data.AddRange(ext);
            var item = new RecoveryItem
            {
                Id = id++,
                Name = r.Name!,
                Dir = BuildPath(r.ParentRef, dirs),
                IsDirectory = r.IsDir,
                Created = r.Created,
                Modified = r.Modified,
                Source = "NTFS",
            };
            if (!r.IsDir)
            {
                if (r.Resident != null)
                {
                    item.Resident = r.Resident;
                    item.Size = r.Resident.Length;
                    item.Chance = RecoveryChance.Good;
                }
                else
                {
                    var segs = r.Data.OrderBy(d => d.StartVcn).ToList();
                    long size = segs.FirstOrDefault(d => d.RealSize >= 0)?.RealSize ?? r.FnSize;
                    item.Size = Math.Max(0, size);
                    if (segs.Any(d => d.Compressed || d.Encrypted)) item.Chance = RecoveryChance.Unsupported;
                    long remaining = item.Size;
                    foreach (var run in segs.SelectMany(d => d.Runs))
                    {
                        if (remaining <= 0) break;
                        long len = Math.Min(run.Length, remaining);
                        item.Extents.Add(new Extent(run.Offset, len));
                        remaining -= len;
                    }
                    if (item.Chance != RecoveryChance.Unsupported)
                        item.Chance = item.Size == 0 ? RecoveryChance.Good : remaining > 0 && item.Extents.Count == 0 ? RecoveryChance.Unsupported : ChanceFor(item.Extents);
                }
            }
            scan.Items.Add(item);
        }
        _p.Found = scan.Items.Count;
        return scan;
    }

    private RecoveryChance ChanceFor(List<Extent> extents)
    {
        if (_bitmap == null) return RecoveryChance.Partial;
        long total = 0, used = 0;
        foreach (var e in extents)
        {
            if (e.IsSparse) continue;
            long c0 = e.Offset / ClusterSize, c1 = (e.Offset + e.Length + ClusterSize - 1) / ClusterSize;
            for (long c = c0; c < c1; c++)
            {
                total++;
                if (IsAllocated(c)) used++;
            }
        }
        if (total == 0 || used == 0) return RecoveryChance.Good;
        return used < total ? RecoveryChance.Partial : RecoveryChance.Poor;
    }

    public bool IsAllocated(long cluster)
    {
        if (_bitmap == null) return true;
        long i = cluster >> 3;
        return i < 0 || i >= _bitmap.Length || (_bitmap[i] & (1 << (int)(cluster & 7))) != 0;
    }

    /// <summary>هل البايت عند هذا العنوان في كتلة مستخدمة؟ (لاستخراج الملفات من المساحة الفارغة فقط)</summary>
    public bool IsAllocatedOffset(long offset) => IsAllocated(offset / ClusterSize);

    private static string BuildPath(long parentRef, Dictionary<long, DirInfo> dirs)
    {
        var parts = new List<string>();
        long rec = parentRef & 0xFFFFFFFFFFFF;
        ushort seq = (ushort)((ulong)parentRef >> 48);
        var seen = new HashSet<long>();
        while (rec != 5)
        {
            if (!seen.Add(rec) || parts.Count > 64 || !dirs.TryGetValue(rec, out var d)) { parts.Add("$Orphans"); break; }
            // عند حذف السجل يزيد رقم التسلسل بواحد
            if (seq != 0 && d.Seq != seq && d.Seq != (ushort)(seq + 1)) { parts.Add("$Orphans"); break; }
            parts.Add(d.Name);
            rec = d.ParentRef & 0xFFFFFFFFFFFF;
            seq = (ushort)((ulong)d.ParentRef >> 48);
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    private byte[]? ReadData(List<DataSeg> data, long maxBytes)
    {
        var segs = data.OrderBy(d => d.StartVcn).ToList();
        long size = segs.FirstOrDefault()?.RealSize ?? -1;
        if (size <= 0) return null;
        size = Math.Min(size, Math.Max(maxBytes, 1));
        if (size > 1L << 30) return null;
        var outBuf = new byte[size];
        long pos = 0;
        foreach (var run in segs.SelectMany(d => d.Runs))
        {
            if (pos >= size) break;
            int len = (int)Math.Min(run.Length, size - pos);
            if (!run.IsSparse) _src.ReadAt(run.Offset, outBuf.AsSpan((int)pos, len));
            pos += len;
        }
        return outBuf;
    }

    /// <summary>تطبيق تصحيح Update Sequence (كل 512 بايت). يرجع false إذا السجل تالف.</summary>
    public static bool ApplyFixup(Span<byte> rec)
    {
        int usaOfs = Le.U16(rec, 4), usaCount = Le.U16(rec, 6);
        if (usaCount < 2 || usaOfs + usaCount * 2 > rec.Length || (usaCount - 1) * 512 > rec.Length) return false;
        byte a = rec[usaOfs], b = rec[usaOfs + 1];
        for (int i = 1; i < usaCount; i++)
        {
            int p = i * 512 - 2;
            if (rec[p] != a || rec[p + 1] != b) return false;
            rec[p] = rec[usaOfs + i * 2];
            rec[p + 1] = rec[usaOfs + i * 2 + 1];
        }
        return true;
    }

    private Rec? ParseRecord(Span<byte> raw, long no)
    {
        if (raw.Length < 48 || !raw[..4].SequenceEqual("FILE"u8)) return null;
        var buf = raw.ToArray();
        if (!ApplyFixup(buf)) return null;
        var r = new Rec
        {
            No = no,
            Seq = Le.U16(buf, 0x10),
            InUse = (Le.U16(buf, 0x16) & 1) != 0,
            IsDir = (Le.U16(buf, 0x16) & 2) != 0,
            BaseRef = (long)(Le.U64(buf, 0x20) & 0xFFFFFFFFFFFF),
        };
        int pos = Le.U16(buf, 0x14);
        int used = (int)Math.Min(Le.U32(buf, 0x18), (uint)buf.Length);
        while (pos + 16 <= used)
        {
            uint type = Le.U32(buf, pos);
            if (type == 0xFFFFFFFF) break;
            int len = (int)Le.U32(buf, pos + 4);
            if (len < 16 || pos + len > buf.Length) break;
            bool nonResident = buf[pos + 8] != 0;
            int nameLen = buf[pos + 9];
            ushort flags = Le.U16(buf, pos + 12);
            if (!nonResident)
            {
                int vlen = (int)Le.U32(buf, pos + 0x10);
                int vofs = Le.U16(buf, pos + 0x14);
                if (vofs + vlen > len) { pos += len; continue; }
                var v = buf.AsSpan(pos + vofs, vlen);
                switch (type)
                {
                    case 0x10 when vlen >= 16:
                        r.Created = Le.FileTime(Le.I64(v, 0));
                        r.Modified = Le.FileTime(Le.I64(v, 8));
                        break;
                    case 0x20:
                        r.HasAttrList = true;
                        break;
                    case 0x30 when vlen >= 0x42:
                    {
                        int nl = v[0x40], ns = v[0x41];
                        if (0x42 + nl * 2 > vlen) break;
                        int rank = ns switch { 1 => 0, 3 => 0, 0 => 1, _ => 2 };
                        if (rank < r.NameSpace)
                        {
                            r.NameSpace = rank;
                            r.Name = System.Text.Encoding.Unicode.GetString(v.Slice(0x42, nl * 2));
                            r.ParentRef = Le.I64(v, 0);
                            r.FnSize = Le.I64(v, 0x30);
                            r.Created ??= Le.FileTime(Le.I64(v, 8));
                            r.Modified ??= Le.FileTime(Le.I64(v, 16));
                        }
                        break;
                    }
                    case 0x80 when nameLen == 0:
                        r.Resident = v.ToArray();
                        break;
                }
            }
            else if (type == 0x80 && nameLen == 0)
            {
                var seg = new DataSeg
                {
                    StartVcn = Le.I64(buf, pos + 0x10),
                    Compressed = (flags & 0x00FF) != 0,
                    Encrypted = (flags & 0x4000) != 0,
                };
                if (seg.StartVcn == 0) seg.RealSize = Le.I64(buf, pos + 0x30);
                int rlOfs = Le.U16(buf, pos + 0x20);
                if (rlOfs < len) seg.Runs = DecodeRuns(buf.AsSpan(pos + rlOfs, len - rlOfs), ClusterSize);
                r.Data.Add(seg);
            }
            else if (type == 0x20)
            {
                r.HasAttrList = true;
            }
            pos += len;
        }
        return r;
    }

    /// <summary>فك قائمة المقاطع (runlist) إلى مواقع بايت.</summary>
    public static List<Extent> DecodeRuns(ReadOnlySpan<byte> rl, int clusterSize)
    {
        var runs = new List<Extent>();
        long lcn = 0;
        int p = 0;
        while (p < rl.Length && rl[p] != 0)
        {
            int lb = rl[p] & 0xF, ob = rl[p] >> 4;
            if (lb == 0 || lb > 8 || ob > 8 || p + 1 + lb + ob > rl.Length) break;
            long len = 0;
            for (int i = 0; i < lb; i++) len |= (long)rl[p + 1 + i] << (8 * i);
            long off = 0;
            if (ob > 0)
            {
                for (int i = 0; i < ob; i++) off |= (long)rl[p + 1 + lb + i] << (8 * i);
                if ((rl[p + lb + ob] & 0x80) != 0) off |= -1L << (8 * ob);   // إشارة سالبة
            }
            p += 1 + lb + ob;
            if (len <= 0) break;
            if (ob == 0) runs.Add(new Extent(-1, len * clusterSize));
            else
            {
                lcn += off;
                if (lcn < 0) break;
                runs.Add(new Extent(lcn * clusterSize, len * clusterSize));
            }
        }
        return runs;
    }
}
