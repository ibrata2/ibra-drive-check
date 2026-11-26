namespace IbraDriveCheck.Core.Recovery;

/// <summary>
/// البحث العميق: يستخرج الملفات من المساحة الفارغة (أو القرص كاملاً بعد التهيئة) بتوقيعها الداخلي
/// ويحدد نهايتها من بنية الملف نفسه. يفترض أن الملف متصل (غير مجزأ).
/// الأنواع: JPG، PNG، GIF، PDF، ZIP/DOCX/XLSX/PPTX/APK، MP4/MOV/3GP/M4A، WAV/AVI/WEBP.
/// </summary>
public sealed class Carver
{
    private readonly IVolumeSource _src;
    private readonly RecoveryProgress _p;
    private readonly WindowReader _r;

    public Carver(IVolumeSource src, RecoveryProgress? progress = null)
    {
        _src = src;
        _p = progress ?? new RecoveryProgress();
        _r = new WindowReader(src);
    }

    /// <param name="start">بداية منطقة البيانات</param>
    /// <param name="unit">حجم الكتلة (الملفات تبدأ دائماً عند بداية كتلة)</param>
    /// <param name="isAllocated">يتخطى الكتل المستخدمة حالياً (null = يفحص الكل)</param>
    public List<RecoveryItem> Carve(long start, int unit, Func<long, bool>? isAllocated, long firstId, CancellationToken ct)
    {
        var items = new List<RecoveryItem>();
        long end = _src.Size;
        unit = Math.Max(512, unit);
        _p.Phase = "rec.phase.carve";
        _p.SetTotal(Math.Max(1, end - start));
        const int chunk = 8 << 20;
        var buf = new byte[chunk];
        long pos = start;
        long id = firstId;
        int counter = 1;
        while (pos < end)
        {
            if (ct.IsCancellationRequested) break;
            int len = (int)Math.Min(chunk, end - pos);
            len = len / unit * unit;
            if (len <= 0) break;
            _src.ReadAt(pos, buf.AsSpan(0, len));
            long next = pos + len;
            for (int o = 0; o + 16 <= len; o += unit)
            {
                long at = pos + o;
                if (isAllocated != null && isAllocated(at)) continue;
                var kind = Sniff(buf.AsSpan(o, 16));
                if (kind == null) continue;
                long size;
                try { size = Measure(kind, at, end); }
                catch { size = -1; }
                if (size <= 0) continue;
                string ext = kind == "zip" ? ZipKind(at, size) : kind == "mp4" ? Mp4Kind(at) : kind == "riff" ? RiffKind(at) : kind;
                items.Add(new RecoveryItem
                {
                    Id = id++,
                    Name = $"{counter++:D5}.{ext}",
                    Dir = "$Carved/" + Folder(ext),
                    Size = size,
                    Chance = RecoveryChance.Carved,
                    Extents = { new Extent(at, size) },
                    Source = "Carved",
                });
                _p.Found++;
                long after = at + size;
                long skipTo = (after - start + unit - 1) / unit * unit + start;
                if (skipTo > pos + o + unit)
                {
                    if (skipTo >= next) { next = skipTo; break; }
                    o = (int)(skipTo - pos) - unit;
                }
            }
            _p.Set(next - start);
            pos = next;
        }
        return items;
    }

    public static string Folder(string ext) => ext switch
    {
        "jpg" or "png" or "gif" or "webp" => "Images",
        "mp4" or "mov" or "3gp" or "avi" => "Videos",
        "m4a" or "wav" => "Audio",
        "pdf" or "docx" or "xlsx" or "pptx" => "Documents",
        _ => "Archives",
    };

    public static string? Sniff(ReadOnlySpan<byte> h)
    {
        if (h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF && (h[3] & 0xF0) == 0xE0 || h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF && h[3] == 0xDB) return "jpg";
        if (h[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "png";
        if (h[..6].SequenceEqual("GIF89a"u8) || h[..6].SequenceEqual("GIF87a"u8)) return "gif";
        if (h[..5].SequenceEqual("%PDF-"u8)) return "pdf";
        if (h[0] == 0x50 && h[1] == 0x4B && h[2] == 0x03 && h[3] == 0x04) return "zip";
        if (h.Slice(4, 4).SequenceEqual("ftyp"u8)) { uint s = Le.BE32(h, 0); if (s >= 8 && s <= 512) return "mp4"; }
        if (h[..4].SequenceEqual("RIFF"u8)) return "riff";
        return null;
    }

    /// <summary>طول الملف من بنيته (أو -1 إذا كانت البنية تالفة). يستخدم أيضاً لفحص الملفات التالفة.</summary>
    public long Measure(string kind, long at, long end) => kind switch
    {
        "jpg" => Jpeg(at, Math.Min(end, at + (128L << 20))),
        "png" => Png(at, Math.Min(end, at + (512L << 20))),
        "gif" => Gif(at, Math.Min(end, at + (128L << 20))),
        "pdf" => Pdf(at, Math.Min(end, at + (512L << 20))),
        "zip" => Zip(at, Math.Min(end, at + (2L << 30))),
        "mp4" => Mp4(at, end),
        "riff" => Riff(at, end),
        _ => -1,
    };

    private long Jpeg(long at, long max)
    {
        long p = at + 2;
        while (p + 2 <= max)
        {
            if (_r[p] != 0xFF) return -1;
            byte m = _r[p + 1];
            if (m == 0xFF) { p++; continue; }
            if (m == 0xD9) return p + 2 - at;
            if (m is >= 0xD0 and <= 0xD7 or 0x01) { p += 2; continue; }
            if (p + 4 > max) return -1;
            int len = _r[p + 2] << 8 | _r[p + 3];
            if (len < 2) return -1;
            p += 2 + len;
            if (m == 0xDA)
            {
                // بيانات الصورة المضغوطة حتى أول علامة حقيقية
                while (p + 1 < max)
                {
                    if (_r[p] != 0xFF) { p++; continue; }
                    byte n = _r[p + 1];
                    if (n == 0x00 || n is >= 0xD0 and <= 0xD7) { p += 2; continue; }
                    if (n == 0xFF) { p++; continue; }
                    break;
                }
            }
        }
        return -1;
    }

    private long Png(long at, long max)
    {
        long p = at + 8;
        while (p + 12 <= max)
        {
            long len = _r.BE32(p);
            if (len > 1L << 30) return -1;
            for (int k = 4; k < 8; k++) { byte c = _r[p + k]; if (!(c is >= 65 and <= 90 or >= 97 and <= 122)) return -1; }
            bool iend = _r[p + 4] == 'I' && _r[p + 5] == 'E' && _r[p + 6] == 'N' && _r[p + 7] == 'D';
            p += 12 + len;
            if (iend) return p - at;
        }
        return -1;
    }

    private long Gif(long at, long max)
    {
        long p = at + 13;
        byte flags = _r[at + 10];
        if ((flags & 0x80) != 0) p += 3L << ((flags & 7) + 1);
        while (p < max)
        {
            byte b = _r[p];
            if (b == 0x3B) return p + 1 - at;
            if (b == 0x21) { p += 2; if (!SkipSubBlocks(ref p, max)) return -1; }
            else if (b == 0x2C)
            {
                byte f = _r[p + 9];
                p += 10;
                if ((f & 0x80) != 0) p += 3L << ((f & 7) + 1);
                p++; // LZW min code size
                if (!SkipSubBlocks(ref p, max)) return -1;
            }
            else return -1;
        }
        return -1;
    }

    private bool SkipSubBlocks(ref long p, long max)
    {
        while (p < max)
        {
            int n = _r[p];
            p += 1 + n;
            if (n == 0) return true;
        }
        return false;
    }

    private long Pdf(long at, long max)
    {
        long eof = _r.Find("%%EOF"u8, at + 5, max);
        if (eof < 0) return -1;
        // التحديثات الإضافية تضيف %%EOF آخر قريب
        while (true)
        {
            long more = _r.Find("%%EOF"u8, eof + 5, Math.Min(max, eof + 5 + (256 << 10)));
            if (more < 0) break;
            eof = more;
        }
        long e = eof + 5;
        if (_r[e] == 0x0D) e++;
        if (_r[e] == 0x0A) e++;
        return e - at;
    }

    private long Zip(long at, long max)
    {
        long p = at + 4;
        while (true)
        {
            long eocd = _r.Find(new byte[] { 0x50, 0x4B, 0x05, 0x06 }, p, max);
            if (eocd < 0) return -1;
            long cdSize = _r.LE32(eocd + 12), cdOfs = _r.LE32(eocd + 16);
            int comment = _r[eocd + 20] | _r[eocd + 21] << 8;
            if (cdOfs + cdSize == eocd - at) return eocd + 22 + comment - at;
            p = eocd + 4;
        }
    }

    private string ZipKind(long at, long size)
    {
        long lim = at + Math.Min(size, 1 << 20);
        if (_r.Find("word/"u8, at, lim) >= 0) return "docx";
        if (_r.Find("xl/"u8, at, lim) >= 0) return "xlsx";
        if (_r.Find("ppt/"u8, at, lim) >= 0) return "pptx";
        if (_r.Find("AndroidManifest.xml"u8, at, lim) >= 0) return "apk";
        return "zip";
    }

    private static readonly HashSet<string> Mp4Boxes = new()
    {
        "ftyp", "moov", "mdat", "free", "skip", "wide", "uuid", "meta", "pdin", "moof", "mfra", "sidx", "styp", "prft", "udta", "PICT", "pnot",
    };

    private long Mp4(long at, long end)
    {
        long p = at;
        bool moov = false, mdat = false;
        int boxes = 0;
        while (p + 8 <= end && boxes < 10000)
        {
            long size = _r.BE32(p);
            string type = _r.Ascii(p + 4, 4);
            if (!Mp4Boxes.Contains(type)) break;
            if (size == 1) size = (long)_r.BE32(p + 8) << 32 | _r.BE32(p + 12);
            if (size < 8) break;
            if (type == "moov") moov = true;
            if (type == "mdat") mdat = true;
            p += size;
            boxes++;
        }
        return moov && mdat && p <= end ? p - at : -1;
    }

    private string Mp4Kind(long at)
    {
        string brand = _r.Ascii(at + 8, 4);
        return brand switch
        {
            "qt  " => "mov",
            "M4A " or "M4B " => "m4a",
            _ when brand.StartsWith("3g") => "3gp",
            _ => "mp4",
        };
    }

    private long Riff(long at, long end)
    {
        long size = _r.LE32(at + 4) + 8L;
        string form = _r.Ascii(at + 8, 4);
        if (form is not ("WAVE" or "AVI " or "WEBP") || size < 12 || at + size > end) return -1;
        return size;
    }

    private string RiffKind(long at) => _r.Ascii(at + 8, 4) switch { "WAVE" => "wav", "AVI " => "avi", _ => "webp" };

    /// <summary>قراءة بايت بايت مع ذاكرة مؤقتة (نافذة 1MB).</summary>
    private sealed class WindowReader
    {
        private readonly IVolumeSource _s;
        private readonly byte[] _buf = new byte[1 << 20];
        private long _start = -1;
        public WindowReader(IVolumeSource s) => _s = s;

        public byte this[long pos]
        {
            get
            {
                if (pos < 0 || pos >= _s.Size) return 0;
                if (_start < 0 || pos < _start || pos >= _start + _buf.Length)
                {
                    _start = pos / 65536 * 65536;
                    _s.ReadAt(_start, _buf);
                }
                return _buf[pos - _start];
            }
        }

        public uint BE32(long p) => (uint)(this[p] << 24 | this[p + 1] << 16 | this[p + 2] << 8 | this[p + 3]);
        public uint LE32(long p) => (uint)(this[p] | this[p + 1] << 8 | this[p + 2] << 16 | this[p + 3] << 24);
        public string Ascii(long p, int n) { var c = new char[n]; for (int i = 0; i < n; i++) c[i] = (char)this[p + i]; return new string(c); }

        public long Find(ReadOnlySpan<byte> pat, long from, long to)
        {
            for (long p = from; p + pat.Length <= to; p++)
            {
                if (this[p] != pat[0]) continue;
                int k = 1;
                while (k < pat.Length && this[p + k] == pat[k]) k++;
                if (k == pat.Length) return p;
            }
            return -1;
        }
    }
}
