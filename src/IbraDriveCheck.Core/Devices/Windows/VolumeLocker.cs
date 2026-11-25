using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace IbraDriveCheck.Core.Devices.Windows;

/// <summary>
/// يقفل ويفصل كل أقسام القرص قبل الكتابة المباشرة (ويندوز يمنع الكتابة على قسم مركّب)،
/// ثم يفك القفل عند الانتهاء فيعيد ويندوز تركيب الأقسام.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VolumeLocker : IDisposable
{
    private readonly List<SafeFileHandle> _handles = new();
    public IReadOnlyList<string> Locked => _locked;
    private readonly List<string> _locked = new();

    public static VolumeLocker LockDisk(int diskNumber)
    {
        var locker = new VolumeLocker();
        try
        {
            foreach (var v in DriveEnumerator.EnumerateVolumes().Where(v => v.DiskNumbers.Contains(diskNumber)))
            {
                var path = v.Guid.TrimEnd('\\');
                var h = Native.Open(path, Native.GENERIC_READ | Native.GENERIC_WRITE);
                if (h.IsInvalid)
                {
                    int e = Marshal.GetLastWin32Error();
                    h.Dispose();
                    throw new IOException($"Cannot open volume {v.Letter ?? path} (code {e})");
                }
                bool locked = false;
                for (int i = 0; i < 10 && !locked; i++)
                {
                    locked = Native.IoctlNoData(h, Native.FSCTL_LOCK_VOLUME);
                    if (!locked) Thread.Sleep(300);
                }
                if (!locked)
                {
                    h.Dispose();
                    throw new VolumeInUseException(v.Letter ?? path);
                }
                Native.IoctlNoData(h, Native.FSCTL_DISMOUNT_VOLUME);
                locker._handles.Add(h);
                locker._locked.Add(v.Letter ?? path);
            }
        }
        catch
        {
            locker.Dispose();
            throw;
        }
        return locker;
    }

    public void Dispose()
    {
        foreach (var h in _handles)
        {
            try { Native.IoctlNoData(h, Native.FSCTL_UNLOCK_VOLUME); } catch { }
            h.Dispose();
        }
        _handles.Clear();
    }
}

public sealed class VolumeInUseException : IOException
{
    public string Volume { get; }
    public VolumeInUseException(string volume) : base($"Volume {volume} is in use") => Volume = volume;
}
