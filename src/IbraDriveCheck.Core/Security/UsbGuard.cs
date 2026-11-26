using System.Text;
using IbraDriveCheck.Core.Storage;

namespace IbraDriveCheck.Core.Security;

public enum ThreatKind
{
    AutorunInf,          // ملف autorun.inf يشغل برنامجاً تلقائياً
    MaliciousShortcut,   // اختصار يشغل سكربت/أوامر (فيروس الاختصارات)
    SuspiciousScript,    // سكربت أو برنامج مخفي في جذر الوحدة
    HiddenUserItem,      // ملفات/مجلدات المستخدم صارت مخفية
}

public sealed class Threat
{
    public string Path { get; init; } = "";
    public ThreatKind Kind { get; init; }
    public string? Detail { get; init; }
    public bool IsDirectory { get; init; }
}

public sealed class GuardResult
{
    public List<Threat> Threats { get; } = new();
    public bool Vaccinated { get; set; }
    public int Quarantined, Unhidden, Errors;
    public string? QuarantineFolder;
}

/// <summary>
/// حماية الفلاشة من فيروسات الاختصارات و autorun: يكشف الملفات الخبيثة ويعزلها (لا يحذفها نهائياً)،
/// ويرجع الملفات والمجلدات المخفية، ويحصن الوحدة بمجلد autorun.inf لا يمكن للفيروس استبداله.
/// </summary>
public static class UsbGuard
{
    private static readonly string[] ScriptExt = { ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".bat", ".cmd", ".ps1", ".hta", ".scr", ".pif", ".com", ".exe", ".dll", ".lnk.vbs" };
    private static readonly string[] BadTokens = { "cmd.exe", "cmd /c", "wscript", "cscript", "rundll32", "powershell", "mshta", "regsvr32", ".vbs", ".vbe", ".jse", ".js ", ".wsf", "start ", "explorer.exe", "certutil", "bitsadmin" };
    private static readonly string[] SystemNames = { "System Volume Information", "$RECYCLE.BIN", "RECYCLER", ".Trashes", ".Spotlight-V100", ".fseventsd", "desktop.ini", "Thumbs.db", "IndexerVolumeGuid", "WPSettings.dat" };

    public static bool IsSystemName(string name) => SystemNames.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>هل محتوى ملف .lnk يشغل أوامر أو سكربتات؟ (نقرأ النصوص ASCII و UTF-16 داخل الاختصار)</summary>
    public static string? ShortcutThreat(byte[] lnk)
    {
        if (lnk.Length < 0x4C || lnk[0] != 0x4C) return null;
        string text = (Encoding.Latin1.GetString(lnk) + " " + Encoding.Unicode.GetString(lnk)).ToLowerInvariant().Replace('\0', ' ');
        var hits = BadTokens.Where(t => text.Contains(t)).ToList();
        // "explorer.exe" وحده ليس خطراً، لكن مع أوامر أخرى يكون فيروس الاختصارات المعروف
        if (hits.Count == 0 || hits.All(h => h is "explorer.exe" or "start ")) return null;
        return string.Join(", ", hits.Distinct().Take(4));
    }

    public static GuardResult Scan(string root)
    {
        var res = new GuardResult();
        root = System.IO.Path.GetFullPath(root);
        var autorun = System.IO.Path.Combine(root, "autorun.inf");
        if (Directory.Exists(autorun)) res.Vaccinated = true;
        else if (File.Exists(autorun))
        {
            string content = "";
            try { content = File.ReadAllText(autorun); } catch { }
            var line = content.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("open", StringComparison.OrdinalIgnoreCase) || l.StartsWith("shellexecute", StringComparison.OrdinalIgnoreCase) || l.StartsWith("shell", StringComparison.OrdinalIgnoreCase));
            res.Threats.Add(new Threat { Path = autorun, Kind = ThreatKind.AutorunInf, Detail = line });
        }

        var hiddenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(root).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }).ToList(); }
        catch { return res; }

        foreach (var e in entries)
        {
            if (IsSystemName(e.Name) || string.Equals(e.Name, "autorun.inf", StringComparison.OrdinalIgnoreCase)) continue;
            bool hidden = (e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
            bool isDir = e is DirectoryInfo;
            string ext = System.IO.Path.GetExtension(e.Name).ToLowerInvariant();

            if (!isDir && ext == ".lnk")
            {
                byte[] data;
                try { data = File.ReadAllBytes(e.FullName); } catch { continue; }
                var why = ShortcutThreat(data);
                if (why != null) res.Threats.Add(new Threat { Path = e.FullName, Kind = ThreatKind.MaliciousShortcut, Detail = why });
                continue;
            }
            if (!isDir && hidden && ScriptExt.Contains(ext))
            {
                res.Threats.Add(new Threat { Path = e.FullName, Kind = ThreatKind.SuspiciousScript, Detail = ext });
                continue;
            }
            if (hidden)
            {
                res.Threats.Add(new Threat { Path = e.FullName, Kind = ThreatKind.HiddenUserItem, IsDirectory = isDir });
                if (isDir) hiddenDirs.Add(e.Name);
            }
        }

        // ملفات خبيثة داخل المجلد الفارغ الاسم الذي يصنعه الفيروس (مثل " " أو "_")
        foreach (var d in entries.OfType<DirectoryInfo>().Where(d => d.Name.Trim().Length == 0 || d.Name.Trim('_', ' ', ' ').Length == 0))
        {
            try
            {
                foreach (var f in d.EnumerateFiles("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
                    if (ScriptExt.Contains(f.Extension.ToLowerInvariant()))
                        res.Threats.Add(new Threat { Path = f.FullName, Kind = ThreatKind.SuspiciousScript, Detail = f.Extension });
            }
            catch { }
        }
        return res;
    }

    /// <summary>ينظف: يعزل الخبيث في مجلد حجر (يمكن إرجاعه)، يظهر المخفي، ويحصن الوحدة اختيارياً.</summary>
    public static GuardResult Clean(string root, GuardResult scan, bool vaccinate)
    {
        var res = scan;
        string q = System.IO.Path.Combine(AppPaths.Root, "quarantine", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        foreach (var t in scan.Threats)
        {
            try
            {
                if (t.Kind == ThreatKind.HiddenUserItem)
                {
                    if (t.IsDirectory)
                    {
                        var di = new DirectoryInfo(t.Path);
                        di.Attributes &= ~(FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly);
                    }
                    else File.SetAttributes(t.Path, File.GetAttributes(t.Path) & ~(FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly));
                    res.Unhidden++;
                }
                else
                {
                    Directory.CreateDirectory(q);
                    File.SetAttributes(t.Path, FileAttributes.Normal);
                    string dest = System.IO.Path.Combine(q, System.IO.Path.GetFileName(t.Path) + ".quarantine");
                    int k = 1;
                    while (File.Exists(dest)) dest = System.IO.Path.Combine(q, $"{System.IO.Path.GetFileName(t.Path)}.{k++}.quarantine");
                    File.Move(t.Path, dest);
                    res.Quarantined++;
                    res.QuarantineFolder = q;
                }
            }
            catch { res.Errors++; }
        }
        if (vaccinate) res.Vaccinated = Vaccinate(root) || res.Vaccinated;
        return res;
    }

    /// <summary>ينشئ مجلداً باسم autorun.inf (مخفي وللقراءة فقط) فلا يستطيع الفيروس إنشاء ملف بنفس الاسم.</summary>
    public static bool Vaccinate(string root)
    {
        string p = System.IO.Path.Combine(root, "autorun.inf");
        try
        {
            if (File.Exists(p)) return false;
            var d = Directory.CreateDirectory(p);
            File.WriteAllText(System.IO.Path.Combine(p, "IBRA_PROTECTED.txt"), "IBRA DRIVE CHECK — autorun protection");
            d.Attributes |= FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly;
            return true;
        }
        catch { return false; }
    }
}
