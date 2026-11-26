using IbraDriveCheck.App.Infrastructure;
using IbraDriveCheck.Core.Devices.Windows;
using IbraDriveCheck.Core.Localization;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.App.ViewModels;

/// <summary>قسم (أو قرص كامل) يمكن اختياره للاستعادة أو إزالة الآثار.</summary>
public sealed class VolumeOption
{
    public DriveSnapshot Drive { get; init; } = new();
    public VolumeSnapshot? Volume { get; init; }
    /// <summary>قسم مفقود أو تالف عُثر عليه بالإنقاذ.</summary>
    public IbraDriveCheck.Core.Recovery.FsCandidate? Candidate { get; init; }
    public bool IsWholeDisk => Volume == null && Candidate == null;
    public bool IsRescue => Candidate != null;
    public string? Letter => Volume?.Letter;
    public string FileSystem => Candidate?.FileSystem ?? Volume?.FileSystem ?? "RAW";
    public bool IsSystem => Volume != null && VolumeTools.IsSystemVolume(Volume.Letter) || Volume == null && Drive.IsSystemDisk;

    public IbraDriveCheck.Core.Recovery.IVolumeSource Open()
    {
        if (Candidate != null)
        {
            var disk = VolumeTools.OpenDiskSource(Drive.DiskNumber);
            var inner = IbraDriveCheck.Core.Recovery.RescueEngine.Open(disk, Candidate);
            return new OwnedSource(inner, disk);
        }
        return IsWholeDisk ? VolumeTools.OpenDiskSource(Drive.DiskNumber) : VolumeTools.OpenVolumeSource(Letter!);
    }

    private sealed class OwnedSource : IbraDriveCheck.Core.Recovery.IVolumeSource
    {
        private readonly IbraDriveCheck.Core.Recovery.IVolumeSource _a, _b;
        public OwnedSource(IbraDriveCheck.Core.Recovery.IVolumeSource a, IbraDriveCheck.Core.Recovery.IVolumeSource b) { _a = a; _b = b; }
        public long Size => _a.Size;
        public void ReadAt(long offset, Span<byte> destination) => _a.ReadAt(offset, destination);
        public void Dispose() { _a.Dispose(); _b.Dispose(); }
    }

    public string Display => Candidate != null
        ? $"{Loc.T("rescue.found")}: {Candidate.FileSystem} @ {Fmt.Bytes(Candidate.Offset)} ({Fmt.Bytes(Candidate.Size)}) — {Drive.DisplayName}"
        : Volume != null
        ? $"{Volume.Letter}  {(Volume.Label.Length > 0 ? Volume.Label : "")}  ({Volume.FileSystem}, {Fmt.Bytes(Volume.TotalBytes)})  —  {Drive.DisplayName}"
        : $"{Loc.T("rec.wholeDisk")}: {Drive.DisplayName} ({Fmt.Bytes(Drive.DeclaredBytes)})";

    public static List<VolumeOption> From(IEnumerable<DriveItem> drives, bool includeWholeDisks)
    {
        var list = new List<VolumeOption>();
        foreach (var d in drives.Select(x => x.Drive))
        {
            foreach (var v in d.Volumes) list.Add(new VolumeOption { Drive = d, Volume = v });
            if (includeWholeDisks && !d.IsSystemDisk) list.Add(new VolumeOption { Drive = d });
        }
        return list;
    }
}
