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
    public bool IsWholeDisk => Volume == null;
    public string? Letter => Volume?.Letter;
    public string FileSystem => Volume?.FileSystem ?? "RAW";
    public bool IsSystem => Volume != null && VolumeTools.IsSystemVolume(Volume.Letter) || Volume == null && Drive.IsSystemDisk;

    public string Display => Volume != null
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
