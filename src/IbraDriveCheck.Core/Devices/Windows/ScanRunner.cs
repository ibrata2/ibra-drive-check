using System.Runtime.Versioning;
using IbraDriveCheck.Core.Engine;
using IbraDriveCheck.Core.Model;
using IbraDriveCheck.Core.Safety;
using IbraDriveCheck.Core.Storage;

namespace IbraDriveCheck.Core.Devices.Windows;

public sealed class ScanRequest
{
    public required DriveSnapshot Drive { get; init; }
    public ScanKind Kind { get; init; }
    public bool ReadOnlyFullSurface { get; init; } = true;
    public int SpeedTestMB { get; init; } = 256;
    /// <summary>القسم المستخدم لاختبار السرعة/المساحة الفارغة (مثلاً "E:"). إذا فارغ نأخذ أول قسم.</summary>
    public string? Volume { get; init; }
}

public sealed class SafetyException : Exception
{
    public string ReasonKey { get; }
    public SafetyException(string key) : base(key) => ReasonKey = key;
}

public sealed class IdentityChangedException : Exception
{
    public IdentityChangedException() : base("Drive identity changed") { }
}

/// <summary>
/// ينفذ أي فحص على ويندوز مع كل خطوات الحماية: قواعد السلامة، صلاحيات المسؤول، التحقق من الهوية، قفل الأقسام.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScanRunner
{
    public static ScanResult Run(ScanRequest req, ScanSession session, CancellationToken ct)
    {
        var d = req.Drive;
        var decision = SafetyPolicy.Check(d, req.Kind);
        if (!decision.Allowed) throw new SafetyException(decision.ReasonKey);

        if (req.Kind == ScanKind.Health)
        {
            var started = DateTime.UtcNow;
            var h = HealthReader.Read(d);
            var r = new ScanResult { Kind = ScanKind.Health, Drive = d, DeclaredBytes = d.DeclaredBytes, StartedUtc = started, Health = h, Verdict = Verdict.Inconclusive };
            if (h.PredictFailure == true || h.HealthStatus == "Unhealthy") r.Verdict = Verdict.ReadWriteErrors;
            else if (h.HealthStatus == "Healthy") r.Verdict = Verdict.Genuine;
            foreach (var m in h.Messages) r.Note(m);
            r.FinishedUtc = DateTime.UtcNow;
            return r;
        }

        if (SafetyPolicy.NeedsRawAccess(req.Kind, d) && !DriveEnumerator.IsElevated)
            throw new UnauthorizedAccessException("admin");

        // نتحقق أن القرص هو نفسه قبل أي عملية (قد يكون أزيل أو استبدل بآخر يحمل نفس الرقم)
        var fresh = DriveEnumerator.Query(d.DiskNumber);
        if (fresh == null || fresh.IdentityKey != d.IdentityKey) throw new IdentityChangedException();
        fresh.Kind = d.Kind; fresh.FriendlyName = d.FriendlyName ?? fresh.FriendlyName; fresh.Manufacturer ??= d.Manufacturer;
        fresh.VendorId = d.VendorId; fresh.ProductId = d.ProductId; fresh.PnpDeviceId = d.PnpDeviceId;
        fresh.IsSystemDisk |= d.IsSystemDisk;
        fresh.SerialNumber ??= d.SerialNumber; fresh.Firmware ??= d.Firmware;
        var recheck = SafetyPolicy.Check(fresh, req.Kind);
        if (!recheck.Allowed) throw new SafetyException(recheck.ReasonKey);

        switch (req.Kind)
        {
            case ScanKind.Quick:
            case ScanKind.Deep:
            {
                using var locker = VolumeLocker.LockDisk(d.DiskNumber);
                using var dev = RawDiskDevice.Open(d.DiskNumber, write: true);
                if (dev.Size != d.DeclaredBytes) throw new IdentityChangedException();
                var opt = req.Kind == ScanKind.Quick ? SampledOptions.Quick() : SampledOptions.Deep();
                var journal = new FileJournal(AppPaths.Journals, d.DisplayName);
                return new SampledCapacityTest(dev, fresh, req.Kind, opt, journal, session).Run(ct);
            }
            case ScanKind.FullDestructive:
            {
                using var locker = VolumeLocker.LockDisk(d.DiskNumber);
                using var dev = RawDiskDevice.Open(d.DiskNumber, write: true);
                if (dev.Size != d.DeclaredBytes) throw new IdentityChangedException();
                var r = new FullSurfaceTest(dev, fresh, new FullSurfaceOptions(), session).Run(ct);
                try { dev.UpdateProperties(); } catch { }
                return r;
            }
            case ScanKind.ReadOnly:
            {
                using var dev = RawDiskDevice.Open(d.DiskNumber, write: false);
                return new ReadOnlyScan(dev, fresh, new ReadOnlyOptions { FullSurface = req.ReadOnlyFullSurface }, session).Run(ct);
            }
            case ScanKind.Speed when fresh.Volumes.Count == 0:
            {
                using var dev = RawDiskDevice.Open(d.DiskNumber, write: false);
                return SpeedTest.RunRawRead(dev, fresh, new SpeedOptions { FileBytes = Math.Max(64, req.SpeedTestMB) * 1024L * 1024L }, session, ct);
            }
            case ScanKind.FullFreeSpace:
            case ScanKind.Speed:
            {
                var vol = fresh.Volumes.FirstOrDefault(v => string.Equals(v.Letter, req.Volume, StringComparison.OrdinalIgnoreCase))
                          ?? fresh.Volumes.OrderByDescending(v => v.FreeBytes).First();
                var fs = new RealFileSpace(vol.Letter + "\\", vol.FileSystem);
                return req.Kind == ScanKind.Speed
                    ? new SpeedTest(fs, fresh, new SpeedOptions { FileBytes = Math.Max(16, req.SpeedTestMB) * 1024L * 1024L }, session).Run(ct)
                    : new FreeSpaceFileTest(fs, fresh, new FreeSpaceOptions(), session).Run(ct);
            }
        }
        throw new SafetyException("safety.unknown");
    }

    /// <summary>استعادة بيانات فحص انقطع: يبحث عن نفس الوحدة (بالبصمة) ويعيد القطاعات الأصلية.</summary>
    public static (int restored, int failed) RestorePending(string journalBase, JournalHeader header)
    {
        if (!DriveEnumerator.IsElevated) throw new UnauthorizedAccessException("admin");
        var drive = DriveEnumerator.Enumerate(includeWmi: false).FirstOrDefault(x => x.HardwareKey == header.IdentityKey)
                    ?? throw new IdentityChangedException();
        var decision = SafetyPolicy.Check(drive, ScanKind.Quick);
        if (!decision.Allowed) throw new SafetyException(decision.ReasonKey);
        var blob = FileJournal.LoadBlob(journalBase);
        (int ok, int fail) res;
        using (var locker = VolumeLocker.LockDisk(drive.DiskNumber))
        using (var dev = RawDiskDevice.Open(drive.DiskNumber, write: true))
        {
            res = SampledCapacityTest.RestoreFromJournal(dev, header, blob);
        }
        if (res.fail == 0) FileJournal.Delete(journalBase);
        return res;
    }
}
