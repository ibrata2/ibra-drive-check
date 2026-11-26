using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using IbraDriveCheck.Core.Model;
using IbraDriveCheck.Core.Recovery;
using IbraDriveCheck.Core.Safety;
using IbraDriveCheck.Core.Tools;

namespace IbraDriveCheck.Core.Devices.Windows;

public sealed class LockerInfo
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string Reason { get; init; } = "";   // مفتاح ترجمة: eject.why.*
}

public sealed class EjectResult
{
    public bool Ejected;
    public string? VetoName;
    public int VetoType;
}

/// <summary>أدوات الحماية على ويندوز: قفل الكتابة، المسح الكامل، الإخراج الآمن الذكي.</summary>
[SupportedOSPlatform("windows")]
public static class SecurityTools
{
    // ───────── قفل الكتابة ─────────
    private const string PolicyKey = @"SYSTEM\CurrentControlSet\Control\StorageDevicePolicies";

    /// <summary>منع الكتابة على كل وحدات USB في هذه الحاسبة (سياسة ويندوز). يتطلب إعادة توصيل الوحدات.</summary>
    public static bool GetUsbWritePolicy()
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(PolicyKey); return k?.GetValue("WriteProtect") is int v && v == 1; }
        catch { return false; }
    }

    public static void SetUsbWritePolicy(bool on)
    {
        using var k = Registry.LocalMachine.CreateSubKey(PolicyKey, true);
        k.SetValue("WriteProtect", on ? 1 : 0, RegistryValueKind.DWord);
    }

    /// <summary>قفل/فتح وحدة معينة (للقراءة فقط). على أقسام GPT يُحفظ القفل داخل الوحدة نفسها.</summary>
    public static string SetDiskReadOnly(DriveSnapshot d, bool readOnly)
    {
        if (d.IsSystemDisk) throw new SafetyException("safety.systemDisk");
        var fresh = DriveEnumerator.Query(d.DiskNumber);
        if (fresh == null || fresh.IdentityKey != d.IdentityKey) throw new IdentityChangedException();
        var sb = new StringBuilder();
        sb.AppendLine($"select disk {d.DiskNumber}");
        sb.AppendLine($"attributes disk {(readOnly ? "set" : "clear")} readonly");
        if (d.PartitionStyle == "GPT")
            foreach (var v in d.Volumes)
            {
                sb.AppendLine($"select volume {v.Letter.TrimEnd(':')}");
                sb.AppendLine($"attributes volume {(readOnly ? "set" : "clear")} readonly noerr");
            }
        return Diskpart(sb.ToString());
    }

    public static string Diskpart(string script)
    {
        string file = Path.Combine(Path.GetTempPath(), $"ibra_dp_{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, script, Encoding.ASCII);
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "diskpart.exe"), $"/s \"{file}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);
            if (p.ExitCode != 0) throw new IOException("diskpart: " + output);
            return output;
        }
        finally { try { File.Delete(file); } catch { } }
    }

    // ───────── المسح الكامل قبل البيع ─────────
    public static SafetyDecision CanErase(DriveSnapshot d)
    {
        if (d.IsSystemDisk) return SafetyDecision.Deny("safety.systemDisk");
        if (d.HasProtectedPartitions) return SafetyDecision.Deny("safety.protectedPartitions");
        if (d.WriteProtected == true) return SafetyDecision.Deny("safety.writeProtected");
        return SafetyDecision.Ok;
    }

    private const uint IOCTL_STORAGE_REINITIALIZE_MEDIA = 0x2D9640;

    /// <summary>
    /// يمسح كل شيء: أمر المسح الداخلي لأقراص NVMe إذا كان مدعوماً (اختياري)، ثم الكتابة فوق كامل الوحدة.
    /// </summary>
    public static EraseResult Erase(DriveSnapshot d, int passes, bool tryBuiltIn, RecoveryProgress p, CancellationToken ct)
    {
        var dec = CanErase(d);
        if (!dec.Allowed) throw new SafetyException(dec.ReasonKey);
        if (!DriveEnumerator.IsElevated) throw new UnauthorizedAccessException("admin");
        var fresh = DriveEnumerator.Query(d.DiskNumber);
        if (fresh == null || fresh.IdentityKey != d.IdentityKey) throw new IdentityChangedException();

        using var locker = VolumeLocker.LockDisk(d.DiskNumber);
        using var dev = RawDiskDevice.Open(d.DiskNumber, write: true);
        bool builtIn = false;
        if (tryBuiltIn && d.BusType == "NVMe")
        {
            p.Phase = "erase.phase.builtin";
            var input = new byte[16];
            BitConverter.GetBytes(16).CopyTo(input, 0);
            BitConverter.GetBytes(16).CopyTo(input, 4);
            BitConverter.GetBytes(600).CopyTo(input, 8);
            BitConverter.GetBytes(2).CopyTo(input, 12);   // Crypto erase
            builtIn = Native.Ioctl(dev.Handle, IOCTL_STORAGE_REINITIALIZE_MEDIA, input, Array.Empty<byte>(), out _);
        }
        var res = SecureEraser.Overwrite(dev, passes, p, ct);
        res.UsedBuiltIn = builtIn;
        try { Native.IoctlNoData(dev.Handle, Native.IOCTL_DISK_UPDATE_PROPERTIES); } catch { }
        return res;
    }

    // ───────── الإخراج الآمن الذكي ─────────
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Request_Device_EjectW(uint devInst, out int vetoType, StringBuilder vetoName, int nameLength, uint flags);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint nFiles, string[] files, uint nApps, IntPtr apps, uint nServices, string[]? services);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? info, out uint reasons);

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess { public int Pid; public System.Runtime.InteropServices.ComTypes.FILETIME Start; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int AppType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    /// <summary>من يمسك الوحدة؟ (برامج تعمل منها، ملفات مفتوحة، نوافذ مستكشف مفتوحة عليها)</summary>
    public static List<LockerInfo> FindLockers(DriveSnapshot d)
    {
        var list = new List<LockerInfo>();
        var letters = d.Volumes.Select(v => v.Letter.ToUpperInvariant()).ToList();
        if (letters.Count == 0) return list;
        bool OnDrive(string? path) => path != null && letters.Any(l => path.StartsWith(l, StringComparison.OrdinalIgnoreCase));

        // 1) برامج تعمل من الوحدة أو تذكرها في سطر الأوامر
        foreach (var row in Wmi.Query(@"root\cimv2", "SELECT ProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process"))
        {
            int pid = (int)(row.Long("ProcessId") ?? 0);
            if (pid == Environment.ProcessId || pid == 0) continue;
            string? exe = row.Str("ExecutablePath"), cmd = row.Str("CommandLine");
            if (OnDrive(exe)) list.Add(new LockerInfo { Pid = pid, Name = row.Str("Name") ?? "?", Reason = "eject.why.runs" });
            else if (cmd != null && letters.Any(l => cmd.Contains(l + "\\", StringComparison.OrdinalIgnoreCase)) && !(row.Str("Name") ?? "").Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                list.Add(new LockerInfo { Pid = pid, Name = row.Str("Name") ?? "?", Reason = "eject.why.cmdline" });
        }

        // 2) ملفات مفتوحة (Restart Manager) — حتى 4000 ملف
        try
        {
            var files = letters.SelectMany(l =>
            {
                try { return Directory.EnumerateFiles(l + "\\", "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).Take(4000); }
                catch { return Enumerable.Empty<string>(); }
            }).Take(4000).ToArray();
            if (files.Length > 0 && RmStartSession(out uint s, 0, new StringBuilder(64)) == 0)
            {
                try
                {
                    for (int i = 0; i < files.Length; i += 500)
                        RmRegisterResources(s, (uint)Math.Min(500, files.Length - i), files.Skip(i).Take(500).ToArray(), 0, IntPtr.Zero, 0, null);
                    uint count = 0;
                    RmGetList(s, out uint needed, ref count, null, out _);
                    if (needed > 0)
                    {
                        var info = new RmProcessInfo[needed];
                        count = needed;
                        if (RmGetList(s, out _, ref count, info, out _) == 0)
                            foreach (var pi in info.Take((int)count))
                                if (list.All(x => x.Pid != pi.Process.Pid))
                                    list.Add(new LockerInfo { Pid = pi.Process.Pid, Name = pi.AppName, Reason = "eject.why.files" });
                    }
                }
                finally { RmEndSession(s); }
            }
        }
        catch { }

        // 3) نوافذ المستكشف المفتوحة على الوحدة
        if (ExplorerWindows(letters).Count > 0)
            list.Add(new LockerInfo { Pid = -1, Name = "Explorer", Reason = "eject.why.explorer" });
        return list;
    }

    private static List<dynamic> ExplorerWindows(List<string> letters)
    {
        var res = new List<dynamic>();
        try
        {
            var t = Type.GetTypeFromProgID("Shell.Application");
            if (t == null) return res;
            dynamic shell = Activator.CreateInstance(t)!;
            foreach (dynamic w in shell.Windows())
            {
                string? url = null;
                try { url = (string)w.LocationURL; } catch { }
                if (url == null) continue;
                string path = Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsFile ? u.LocalPath : url;
                if (letters.Any(l => path.StartsWith(l, StringComparison.OrdinalIgnoreCase))) res.Add(w);
            }
        }
        catch { }
        return res;
    }

    public static int CloseExplorerWindows(DriveSnapshot d)
    {
        int n = 0;
        foreach (var w in ExplorerWindows(d.Volumes.Select(v => v.Letter.ToUpperInvariant()).ToList()))
        {
            try { w.Quit(); n++; } catch { }
        }
        return n;
    }

    /// <summary>إخراج آمن: يطلب من ويندوز فصل الجهاز. إذا رفض يرجع اسم البرنامج الذي يمنعه.</summary>
    public static EjectResult Eject(DriveSnapshot d)
    {
        var res = new EjectResult();
        if (d.PnpDeviceId == null || Native.CM_Locate_DevNodeW(out uint dev, d.PnpDeviceId, 0) != 0)
            throw new IOException("Device not found");
        var veto = new StringBuilder(520);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int r = CM_Request_Device_EjectW(dev, out int vt, veto, veto.Capacity, 0);
            if (r == 0 && vt == 0) { res.Ejected = true; return res; }
            res.VetoType = vt;
            res.VetoName = veto.ToString();
            Thread.Sleep(500);
        }
        return res;
    }
}
