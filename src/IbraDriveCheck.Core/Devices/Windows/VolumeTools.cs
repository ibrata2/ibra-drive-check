using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using IbraDriveCheck.Core.Engine;
using IbraDriveCheck.Core.Model;
using IbraDriveCheck.Core.Recovery;
using IbraDriveCheck.Core.Safety;

namespace IbraDriveCheck.Core.Devices.Windows;

public sealed class PartitionResult
{
    public bool Ok { get; set; }
    public string? Letter { get; set; }
    public long PartitionBytes { get; set; }
    public string? FileSystem { get; set; }
    public string Log { get; set; } = "";
}

/// <summary>أدوات ويندوز للأقسام: إعادة التقسيم بحجم محدد، مسح أسماء المحذوفات، حذف سجل USN، الاستعادة من قسم.</summary>
[SupportedOSPlatform("windows")]
public static class VolumeTools
{
    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
    private const uint FSCTL_DELETE_USN_JOURNAL = 0x000900F8;

    public static string[] FileSystemsFor(long partitionBytes) =>
        partitionBytes <= 32L * 1024 * 1024 * 1024 ? new[] { "exFAT", "NTFS", "FAT32" } : new[] { "exFAT", "NTFS" };

    /// <summary>هل يسمح بإعادة تقسيم هذه الوحدة؟ (يمسح كل البيانات)</summary>
    public static SafetyDecision CanRepartition(DriveSnapshot d)
    {
        if (d.IsSystemDisk) return SafetyDecision.Deny("safety.systemDisk");
        if (d.HasProtectedPartitions) return SafetyDecision.Deny("safety.protectedPartitions");
        if (!d.IsExternalBus && !SafetyPolicy.IsBlankInternal(d)) return SafetyDecision.Deny("safety.internalRaw");
        if (d.WriteProtected == true) return SafetyDecision.Deny("safety.writeProtected");
        return SafetyDecision.Ok;
    }

    public static string CleanLabel(string label, string fs)
    {
        var s = new string((label ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '-').ToArray()).Trim();
        if (s.Length == 0) s = "IBRA";
        return s.Length > (fs == "NTFS" ? 32 : 11) ? s[..(fs == "NTFS" ? 32 : 11)] : s;
    }

    /// <summary>
    /// يمسح جدول الأقسام وينشئ قسماً واحداً بنمط MBR بالحجم المطلوب ويهيئه (عبر diskpart المدمج في ويندوز).
    /// MBR وليس GPT: لأن GPT يكتب نسخة احتياطية في آخر القرص — وفي الفلاشة المزيفة آخر القرص يلتف فوق البيانات.
    /// </summary>
    public static PartitionResult Repartition(DriveSnapshot d, long? partitionBytes, string fs, string label, bool bypassSafetyForTests = false, char? letter = null)
    {
        if (!bypassSafetyForTests)
        {
            var dec = CanRepartition(d);
            if (!dec.Allowed) throw new SafetyException(dec.ReasonKey);
        }
        if (!DriveEnumerator.IsElevated) throw new UnauthorizedAccessException("admin");
        if (!FileSystemsFor(partitionBytes ?? d.DeclaredBytes).Contains(fs)) throw new ArgumentException("File system not allowed for this size");

        // التحقق من الهوية مباشرة قبل الكتابة
        var fresh = DriveEnumerator.Query(d.DiskNumber);
        if (fresh == null || fresh.IdentityKey != d.IdentityKey) throw new IdentityChangedException();
        if (!bypassSafetyForTests)
        {
            var dec = CanRepartition(fresh);
            if (!dec.Allowed) throw new SafetyException(dec.ReasonKey);
        }

        long mib = partitionBytes is long b ? b / (1024 * 1024) : 0;
        var script = new StringBuilder();
        script.AppendLine($"select disk {d.DiskNumber}");
        script.AppendLine("attributes disk clear readonly noerr");
        script.AppendLine("clean");
        script.AppendLine("convert mbr noerr");
        script.AppendLine(mib > 0 ? $"create partition primary size={mib} align=1024" : "create partition primary align=1024");
        script.AppendLine($"format fs={fs.ToLowerInvariant()} quick label=\"{CleanLabel(label, fs)}\"");
        script.AppendLine(letter is char l ? $"assign letter={l}" : "assign");
        string file = Path.Combine(Path.GetTempPath(), $"ibra_dp_{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, script.ToString(), Encoding.ASCII);

        var res = new PartitionResult { FileSystem = fs };
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "diskpart.exe"), $"/s \"{file}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15 * 60_000)) { try { p.Kill(); } catch { } }
            res.Log = output;
            res.Ok = p.ExitCode == 0;
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }

        // تحقق من النتيجة
        for (int i = 0; i < 20; i++)
        {
            Thread.Sleep(500);
            var after = DriveEnumerator.Query(d.DiskNumber);
            var vol = after?.Volumes.FirstOrDefault();
            if (vol != null)
            {
                res.Letter = vol.Letter;
                res.PartitionBytes = vol.TotalBytes;
                res.FileSystem = vol.FileSystem;
                break;
            }
        }
        res.Ok &= res.Letter != null;
        return res;
    }

    /// <summary>يفتح قسماً للقراءة المباشرة (للاستعادة).</summary>
    public static IVolumeSource OpenVolumeSource(string letter)
    {
        if (!DriveEnumerator.IsElevated) throw new UnauthorizedAccessException("admin");
        return new BlockVolumeSource(RawDiskDevice.OpenVolume(letter, write: false));
    }

    /// <summary>يفتح قرصاً كاملاً للقراءة (بحث عميق على قرص بدون قسم أو بعد مسح الأقسام).</summary>
    public static IVolumeSource OpenDiskSource(int disk)
    {
        if (!DriveEnumerator.IsElevated) throw new UnauthorizedAccessException("admin");
        return new BlockVolumeSource(RawDiskDevice.Open(disk, write: false));
    }

    /// <summary>
    /// FAT/exFAT: يقفل القسم ويصفّر أسماء ومواقع المدخلات المحذوفة داخل المجلدات ثم يعيد تركيبه.
    /// </summary>
    public static int ScrubDeletedNames(string letter, string fs)
    {
        if (fs is not ("FAT32" or "FAT16" or "exFAT")) throw new NotSupportedException(fs);
        using var dev = RawDiskDevice.OpenVolume(letter, write: true);
        bool locked = false;
        for (int i = 0; i < 10 && !locked; i++)
        {
            locked = Native.IoctlNoData(dev.Handle, Native.FSCTL_LOCK_VOLUME);
            if (!locked) Thread.Sleep(300);
        }
        if (!locked) throw new VolumeInUseException(letter);
        try
        {
            // القفل يفرغ الكاش ويمنع أي برنامج آخر؛ نكتب ثم نفصل القسم حتى يعيد ويندوز قراءة المجلدات من القرص
            using var src = new BlockVolumeSource(dev, ownsDevice: false);
            using var wbuf = new AlignedBuffer(1 << 20, Math.Max(4096, dev.Alignment));
            int n = TraceCleaner.ScrubDirectoryEntries(src, (offset, data) =>
            {
                // عناوين ومقاطع المجلدات محاذاة على القطاع دائماً
                for (int o = 0; o < data.Length; o += wbuf.Length)
                {
                    int len = Math.Min(wbuf.Length, data.Length - o);
                    data.AsSpan(o, len).CopyTo(wbuf.Span);
                    dev.Write(offset + o, wbuf.Slice(0, len));
                }
            }, fs);
            dev.Flush();
            Native.IoctlNoData(dev.Handle, Native.FSCTL_DISMOUNT_VOLUME);
            return n;
        }
        finally
        {
            Native.IoctlNoData(dev.Handle, Native.FSCTL_UNLOCK_VOLUME);
        }
    }

    /// <summary>NTFS: يحذف سجل تغييرات الملفات (USN Journal) الذي يحتفظ بأسماء الملفات المحذوفة. ويندوز يعيد إنشاءه عند الحاجة.</summary>
    public static bool DeleteUsnJournal(string letter)
    {
        using var h = Native.Open($@"\\.\{letter.TrimEnd('\\').TrimEnd(':')}:", Native.GENERIC_READ | Native.GENERIC_WRITE);
        if (h.IsInvalid) return false;
        var data = new byte[80];
        if (!Native.Ioctl(h, FSCTL_QUERY_USN_JOURNAL, null, data, out _)) return false;
        ulong id = BitConverter.ToUInt64(data, 0);
        var input = new byte[16];
        BitConverter.GetBytes(id).CopyTo(input, 0);
        BitConverter.GetBytes(3u).CopyTo(input, 8);   // USN_DELETE_FLAG_DELETE | USN_DELETE_FLAG_NOTIFY
        return Native.Ioctl(h, FSCTL_DELETE_USN_JOURNAL, input, Array.Empty<byte>(), out _);
    }

    /// <summary>هل القسم على قرص النظام؟</summary>
    public static bool IsSystemVolume(string letter)
    {
        string sys = Path.GetPathRoot(Environment.SystemDirectory)?.Substring(0, 2) ?? "C:";
        return string.Equals(letter.TrimEnd('\\'), sys, StringComparison.OrdinalIgnoreCase);
    }
}
