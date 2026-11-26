using System.Text.Json;
using System.Text.Json.Serialization;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.Core.Storage;

/// <summary>مسارات بيانات البرنامج المحلية (لا يُرسل أي شيء للإنترنت).</summary>
public static class AppPaths
{
    public static string Root { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IBRA DRIVE CHECK");

    public static string History => Path.Combine(Root, "history");
    public static string Journals => Path.Combine(Root, "restore");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>سجل الفحوصات: ملف JSON لكل فحص داخل مجلد البرنامج المحلي.</summary>
public sealed class HistoryStore
{
    private readonly string _dir;
    public HistoryStore(string? dir = null) => _dir = dir ?? AppPaths.History;

    public string Directory => _dir;

    public void Save(ScanResult r)
    {
        System.IO.Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, $"{r.StartedUtc:yyyyMMdd_HHmmss}_{r.Id:N}.json");
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(r, Json.Options));
        File.Move(tmp, path, overwrite: true);
    }

    public List<ScanResult> LoadAll()
    {
        var list = new List<ScanResult>();
        if (!System.IO.Directory.Exists(_dir)) return list;
        foreach (var f in System.IO.Directory.GetFiles(_dir, "*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<ScanResult>(File.ReadAllText(f), Json.Options);
                if (r != null) list.Add(r);
            }
            catch { /* ملف تالف — نتجاهله */ }
        }
        return list.OrderByDescending(r => r.StartedUtc).ToList();
    }

    public bool Delete(Guid id)
    {
        if (!System.IO.Directory.Exists(_dir)) return false;
        foreach (var f in System.IO.Directory.GetFiles(_dir, $"*_{id:N}.json"))
        {
            File.Delete(f);
            return true;
        }
        return false;
    }

    public int DeleteAll()
    {
        if (!System.IO.Directory.Exists(_dir)) return 0;
        int n = 0;
        foreach (var f in System.IO.Directory.GetFiles(_dir, "*.json")) { File.Delete(f); n++; }
        return n;
    }
}

public sealed class AppSettings
{
    public string Language { get; set; } = "ar";
    public string Theme { get; set; } = "light";          // light / dark / system
    public bool NotifyNewDrive { get; set; } = false;
    public bool NotifyScanDone { get; set; } = true;
    public bool NotifyFake { get; set; } = true;
    public bool NotifyErrors { get; set; } = true;
    public bool NotifyHot { get; set; } = true;
    public bool NotifyDisconnect { get; set; } = true;
    public int SpeedTestMB { get; set; } = 256;
    public bool ReadOnlyFullSurface { get; set; } = true;
    public bool MonitorEnabled { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public int TempLimit { get; set; } = 60;
    public int MonitorMinutes { get; set; } = 5;
    public bool TrayHintShown { get; set; } = false;

    public static AppSettings Load(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json.Options) ?? new();
        }
        catch { }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json.Options));
    }
}
