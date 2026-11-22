using System;
using System.Collections.Generic;

namespace CryoTray.Models
{
    public class DriveTelemetry
    {
        public int Index { get; set; }
        public string Model { get; set; } = "";
        public double Temperature { get; set; } = double.NaN;
        public int? SmartHealthPercent { get; set; }
        public string StatusText { get; set; } = "Unknown";
        public string DriveLetters { get; set; } = "";
        public double CapacityGb { get; set; }
        public int? PhysicalDriveNumber { get; set; }
        public string SerialNumber { get; set; } = "";
        public string HardwareIdentifier { get; set; } = "";
        public bool? IsSolidState { get; set; }
    }

    public class GpuTelemetry
    {
        public string Identifier { get; set; } = "";
        public string Name { get; set; } = "";
        public string Vendor { get; set; } = "";
        public bool IsDiscrete { get; set; }
        public double Temperature { get; set; } = double.NaN;
        public double Load { get; set; } = double.NaN;
        public int? PowerWatts { get; set; }
        public double? HotspotTemp { get; set; }
        public double? VramTemp { get; set; }
        public int? FanRpm { get; set; }
        public double? VramUsedMb { get; set; }
        public double? VramTotalMb { get; set; }
        public double? CoreClockMhz { get; set; }
        public double? MemoryClockMhz { get; set; }
        public string? ThrottleReason { get; set; }
    }

    public class GpuDeviceOption
    {
        public string Identifier { get; set; } = "";
        public string Name { get; set; } = "";
        public string Vendor { get; set; } = "";
        public bool IsDiscrete { get; set; }
        public string DisplayName { get; set; } = "";
    }

    public class FanTelemetry
    {
        public string Name { get; set; } = "";
        public int Rpm { get; set; }
    }

    public class CoreTelemetry
    {
        public int Index { get; set; }
        public string Name { get; set; } = "CPU00";
        public double Temperature { get; set; }
        public int DisplayTemp { get; set; }
        public bool IsHottest { get; set; }
    }

    public class MemoryBreakdown
    {
        public ulong InstalledBytes { get; set; }
        public ulong TotalUsableBytes { get; set; }
        public ulong AvailableBytes { get; set; }
        public ulong InUseBytes { get; set; }
        public ulong HardwareReservedBytes { get; set; }
        public uint MemoryLoadPercent { get; set; }
        public bool HasPageListBreakdown { get; set; }
        public ulong FreeBytes { get; set; }
        public ulong StandbyBytes { get; set; }
        public ulong ModifiedBytes { get; set; }

        public ulong CachedBytes => StandbyBytes + ModifiedBytes;

        public double InUsePercent => TotalUsableBytes > 0
            ? (InUseBytes / (double)TotalUsableBytes) * 100.0
            : double.NaN;

        public double AvailablePercent => TotalUsableBytes > 0
            ? (AvailableBytes / (double)TotalUsableBytes) * 100.0
            : double.NaN;
    }

    public class HardwareSnapshot
    {
        public string CpuName { get; set; } = "";
        public string CpuVendor { get; set; } = "";
        public int CpuCores { get; set; }
        public double CpuTemp { get; set; } = double.NaN;
        public double PeakCpuTemp { get; set; } = double.NaN;
        public double MaxCoreTemp { get; set; } = double.NaN;
        public double CpuGhz { get; set; } = double.NaN;
        public double CpuBaseGhz { get; set; } = double.NaN;
        public double CpuMaxGhz { get; set; } = double.NaN;
        public double CpuLoad { get; set; } = double.NaN;
        public int? CpuPackageWatts { get; set; }
        public List<CoreTelemetry> CpuCoresTelemetry { get; set; } = new();

        public double RamUsedGb { get; set; } = double.NaN;
        public double RamTotalGb { get; set; } = double.NaN;
        public double RamUsagePercent { get; set; } = double.NaN;
        public MemoryBreakdown? Memory { get; set; }

        public string GpuIdentifier { get; set; } = "";
        public string GpuName { get; set; } = "";
        public string GpuVendor { get; set; } = "";
        public double GpuTemp { get; set; } = double.NaN;
        public double GpuLoad { get; set; } = double.NaN;
        public int? GpuPowerWatts { get; set; }
        public double? GpuHotspotTemp { get; set; }
        public double? GpuVramTemp { get; set; }
        public int? GpuFanRpm { get; set; }
        public double? GpuVramUsedMb { get; set; }
        public double? GpuVramTotalMb { get; set; }
        public double? GpuCoreClockMhz { get; set; }
        public double? GpuMemoryClockMhz { get; set; }
        public string? GpuThrottleReason { get; set; }
        public List<GpuTelemetry> Gpus { get; set; } = new();
        public List<FanTelemetry> Fans { get; set; } = new();

        public List<DriveTelemetry> StorageDrives { get; set; } = new();
        public bool PawnIoInstalled { get; set; } = true;
        public bool CoolingModeActive { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}
