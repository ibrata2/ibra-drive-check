using System.Runtime.Versioning;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.Core.Devices.Windows;

/// <summary>
/// يقرأ معلومات الصحة المتاحة من ويندوز (MSFT_PhysicalDisk + StorageReliabilityCounter + تنبؤ فشل SMART).
/// أغلب الفلاشات وقارئات البطاقات لا تدعم SMART — عندها نعرض "غير متوفر" ولا نختلق أي رقم.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HealthReader
{
    public static HealthInfo Read(DriveSnapshot d)
    {
        var h = new HealthInfo();
        string id = d.DiskNumber.ToString();
        const string ns = @"root\Microsoft\Windows\Storage";

        var pd = Wmi.Query(ns, $"SELECT DeviceId, HealthStatus, OperationalStatus, MediaType, FirmwareVersion FROM MSFT_PhysicalDisk WHERE DeviceId = '{id}'").FirstOrDefault();
        if (pd != null)
        {
            h.HealthStatus = pd.Long("HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => null };
            h.MediaType = pd.Long("MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => null };
            if (pd.TryGetValue("OperationalStatus", out var op) && op is Array arr)
            {
                var codes = arr.Cast<object>().Select(o => Convert.ToInt32(o)).ToList();
                h.OperationalStatus = string.Join(", ", codes.Select(OpName));
            }
            if (d.Firmware == null) d.Firmware = pd.Str("FirmwareVersion");
        }

        var rc = Wmi.Query(ns, $"SELECT * FROM MSFT_StorageReliabilityCounter WHERE DeviceId = '{id}'").FirstOrDefault();
        if (rc != null)
        {
            h.TemperatureC = Positive(rc.Long("Temperature"));
            h.TemperatureMaxC = Positive(rc.Long("TemperatureMax"));
            h.PowerOnHours = rc.Long("PowerOnHours");
            h.StartStopCycles = rc.Long("StartStopCycleCount");
            h.ReadErrorsTotal = rc.Long("ReadErrorsTotal");
            h.ReadErrorsCorrected = rc.Long("ReadErrorsCorrected");
            h.ReadErrorsUncorrected = rc.Long("ReadErrorsUncorrected");
            h.WriteErrorsTotal = rc.Long("WriteErrorsTotal");
            h.WriteErrorsUncorrected = rc.Long("WriteErrorsUncorrected");
            var wear = rc.Long("Wear");
            if (wear is >= 0 and <= 100) h.WearPercent = (int)wear.Value;
            h.SmartSupported = h.TemperatureC != null || h.PowerOnHours != null || h.ReadErrorsTotal != null || h.WearPercent != null;
        }
        else if (!DriveEnumerator.IsElevated)
        {
            h.Messages.Add("health.needAdmin");
        }

        if (d.PnpDeviceId != null)
        {
            var fp = Wmi.Query(@"root\wmi", "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus")
                .FirstOrDefault(x => x.Str("InstanceName")?.StartsWith(d.PnpDeviceId, StringComparison.OrdinalIgnoreCase) == true);
            if (fp != null)
            {
                h.PredictFailure = fp.Bool("PredictFailure");
                h.SmartSupported = true;
            }
        }

        ReadNvmeLog(d.DiskNumber, h);

        if (!h.SmartSupported) h.Messages.Add("health.noSmart");
        if (h.PredictFailure == true) h.Messages.Add("health.predictFailure");
        if (h.TemperatureC is double t && t >= 60) h.Messages.Add("health.hot");
        return h;
    }

    /// <summary>
    /// سجل الصحة الخاص بـ NVMe (Log Page 0x02) عبر IOCTL_STORAGE_QUERY_PROPERTY: البيانات المكتوبة، ساعات التشغيل،
    /// نسبة الاستهلاك، الأخطاء. يعمل على أقراص NVMe الداخلية، وغالباً لا يمر عبر علب USB.
    /// </summary>
    private static void ReadNvmeLog(int disk, HealthInfo h)
    {
        const int specSize = 40, dataLen = 512;
        foreach (uint access in new[] { Native.GENERIC_READ | Native.GENERIC_WRITE, Native.GENERIC_READ, 0u })
        {
            try
            {
                using var handle = Native.Open($@"\\.\PhysicalDrive{disk}", access);
                if (handle.IsInvalid) continue;
                var input = new byte[8 + specSize + dataLen];
                BitConverter.GetBytes(50).CopyTo(input, 0);          // StorageDeviceProtocolSpecificProperty
                BitConverter.GetBytes(0).CopyTo(input, 4);           // PropertyStandardQuery
                BitConverter.GetBytes(3).CopyTo(input, 8);           // ProtocolTypeNvme
                BitConverter.GetBytes(2).CopyTo(input, 12);          // NVMeDataTypeLogPage
                BitConverter.GetBytes(2).CopyTo(input, 16);          // SMART / Health Information log
                BitConverter.GetBytes(0).CopyTo(input, 20);
                BitConverter.GetBytes(specSize).CopyTo(input, 24);   // ProtocolDataOffset
                BitConverter.GetBytes(dataLen).CopyTo(input, 28);    // ProtocolDataLength
                var output = new byte[input.Length];
                if (!Native.Ioctl(handle, Native.IOCTL_STORAGE_QUERY_PROPERTY, input, output, out int ret) || ret < 8 + specSize + 200) continue;
                int off = BitConverter.ToInt32(output, 8 + 16);
                int len = BitConverter.ToInt32(output, 8 + 20);
                int start = 8 + off;
                if (len < 192 || start + 192 > output.Length) continue;
                var log = output.AsSpan(start, Math.Min(len, dataLen));
                if (log.IndexOfAnyExcept((byte)0) < 0) continue;

                h.NvmeLogAvailable = true;
                h.SmartSupported = true;
                h.CriticalWarning = log[0];
                int kelvin = BitConverter.ToUInt16(log.Slice(1, 2));
                if (kelvin > 273 && kelvin < 273 + 150) h.TemperatureC ??= kelvin - 273;
                h.AvailableSpare = log[3];
                h.PercentUsed = log[5];
                h.WearPercent ??= Math.Min(100, (int)log[5]);
                h.DataReadBytes = SafeMul(Le64(log, 32), 512_000);
                h.DataWrittenBytes = SafeMul(Le64(log, 48), 512_000);
                h.PowerCycles = Le64(log, 112);
                h.PowerOnHours = Le64(log, 128);
                h.UnsafeShutdowns = Le64(log, 144);
                h.MediaErrors = Le64(log, 160);
                return;
            }
            catch { }
        }
    }

    private static long Le64(ReadOnlySpan<byte> s, int o)
    {
        ulong v = BitConverter.ToUInt64(s.Slice(o, 8));
        return v > long.MaxValue ? long.MaxValue : (long)v;
    }

    private static long SafeMul(long a, long b) => a > long.MaxValue / b ? long.MaxValue : a * b;

    private static double? Positive(long? v) => v is > 0 and < 150 ? v : null;

    private static string OpName(int c) => c switch
    {
        2 => "OK",
        3 => "Degraded",
        5 => "Predictive Failure",
        6 => "Error",
        10 => "Stopped",
        11 => "In Service",
        13 => "Lost Communication",
        53264 => "Online",
        53265 => "Not Ready",
        53266 => "No Media",
        53267 => "Offline",
        53268 => "Failed",
        _ => c.ToString(),
    };
}
