using System.Text.Json;

namespace IbraDriveCheck.Core.Safety;

/// <summary>
/// سجل النسخ الاحتياطي للقطاعات الأصلية قبل الكتابة المؤقتة عليها.
/// يُحفظ على قرص النظام قبل أي كتابة، فإذا انقطع التيار أو أزيلت الفلاشة يمكن استعادة البيانات لاحقاً.
/// </summary>
public interface IBackupJournal
{
    void Begin(string identityKey, int blockSize, IReadOnlyList<long> offsets);
    void Save(int index, ReadOnlySpan<byte> original);
    /// <summary>يثبت السجل على القرص (يُستدعى قبل أول كتابة).</summary>
    void Commit();
    bool TryLoad(int index, Span<byte> destination);
    /// <summary>يحذف السجل بعد نجاح الاستعادة.</summary>
    void Complete();
    string? Location { get; }
}

public sealed class MemoryJournal : IBackupJournal
{
    private readonly Dictionary<int, byte[]> _data = new();
    public bool Committed { get; private set; }
    public bool Completed { get; private set; }
    public string? Location => null;
    public void Begin(string identityKey, int blockSize, IReadOnlyList<long> offsets) { _data.Clear(); Committed = Completed = false; }
    public void Save(int index, ReadOnlySpan<byte> original) => _data[index] = original.ToArray();
    public void Commit() => Committed = true;
    public bool TryLoad(int index, Span<byte> destination)
    {
        if (!_data.TryGetValue(index, out var d)) return false;
        d.CopyTo(destination);
        return true;
    }
    public void Complete() => Completed = true;
    public int Count => _data.Count;
}

public sealed class JournalHeader
{
    public int Version { get; set; } = 1;
    public string IdentityKey { get; set; } = "";
    public string DriveName { get; set; } = "";
    public int BlockSize { get; set; }
    public List<long> Offsets { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>سجل على القرص: ملف .json للوصف + ملف .bin للبيانات الأصلية بالترتيب.</summary>
public sealed class FileJournal : IBackupJournal
{
    private readonly string _dir;
    private readonly string _driveName;
    private JournalHeader? _header;
    private byte[]? _blob;
    private string? _base;

    public FileJournal(string directory, string driveName)
    {
        _dir = directory;
        _driveName = driveName;
    }

    public string? Location => _base;

    public void Begin(string identityKey, int blockSize, IReadOnlyList<long> offsets)
    {
        Directory.CreateDirectory(_dir);
        _header = new JournalHeader { IdentityKey = identityKey, DriveName = _driveName, BlockSize = blockSize, Offsets = offsets.ToList() };
        _blob = new byte[(long)blockSize * offsets.Count];
        _base = Path.Combine(_dir, $"restore_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}");
    }

    public void Save(int index, ReadOnlySpan<byte> original)
    {
        if (_header == null || _blob == null) throw new InvalidOperationException("Begin first");
        original.CopyTo(_blob.AsSpan(index * _header.BlockSize, _header.BlockSize));
    }

    public void Commit()
    {
        if (_header == null || _blob == null || _base == null) throw new InvalidOperationException("Begin first");
        using (var fs = new FileStream(_base + ".bin", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
        {
            fs.Write(_blob);
            fs.Flush(true);
        }
        File.WriteAllText(_base + ".json", JsonSerializer.Serialize(_header));
    }

    public bool TryLoad(int index, Span<byte> destination)
    {
        if (_header == null || _blob == null) return false;
        _blob.AsSpan(index * _header.BlockSize, _header.BlockSize).CopyTo(destination);
        return true;
    }

    public void Complete()
    {
        if (_base == null) return;
        TryDelete(_base + ".json");
        TryDelete(_base + ".bin");
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { /* يبقى للاستعادة اليدوية */ }
    }

    /// <summary>سجلات استعادة معلقة من فحوصات انقطعت سابقاً.</summary>
    public static List<(string BasePath, JournalHeader Header)> FindPending(string directory)
    {
        var list = new List<(string, JournalHeader)>();
        if (!Directory.Exists(directory)) return list;
        foreach (var json in Directory.GetFiles(directory, "restore_*.json"))
        {
            try
            {
                var h = JsonSerializer.Deserialize<JournalHeader>(File.ReadAllText(json));
                var b = Path.ChangeExtension(json, null);
                if (h != null && File.Exists(b + ".bin")) list.Add((b, h));
            }
            catch { }
        }
        return list;
    }

    public static byte[] LoadBlob(string basePath) => File.ReadAllBytes(basePath + ".bin");

    public static void Delete(string basePath)
    {
        TryDelete(basePath + ".json");
        TryDelete(basePath + ".bin");
    }
}
