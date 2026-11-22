using System;
using System.Collections.Generic;
using CryoTray.Services;

namespace CryoTray.Models
{
    public class TelemetrySample
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public double CpuTemp { get; set; }
        public double GpuTemp { get; set; }
        public string PrimaryGpuIdentifier { get; set; } = "";
        public double? GpuHotspotTemp { get; set; }
        public double? GpuVramTemp { get; set; }
        public int? GpuFanRpm { get; set; }
        public double CpuGhz { get; set; }
        public double CpuLoad { get; set; }
        public int? CpuPowerWatts { get; set; }
        public double GpuLoad { get; set; }
        public int? GpuPowerWatts { get; set; }
        public double RamUsedGb { get; set; } = double.NaN;
        public double RamUsagePercent { get; set; } = double.NaN;
        public bool CoolingModeActive { get; set; }
        public KeyValuePair<string, double>[]? DriveTemps { get; set; }
        public KeyValuePair<string, double>[]? GpuTemps { get; set; }
        public KeyValuePair<string, double>[]? GpuLoads { get; set; }
        public KeyValuePair<string, double>[]? GpuPowers { get; set; }

        /// <summary>Stress-test throughput in work iterations per second; NaN outside the CPU test.</summary>
        public double StressIterationsPerSecond { get; set; } = double.NaN;

        /// <summary>Creates a telemetry sample from a full hardware snapshot.</summary>
        public static TelemetrySample FromSnapshot(HardwareSnapshot snapshot)
        {
            KeyValuePair<string, double>[]? driveTemps = null;
            if (snapshot.StorageDrives is { Count: > 0 } drives)
            {
                driveTemps = new KeyValuePair<string, double>[drives.Count];
                for (int i = 0; i < drives.Count; i++)
                {
                    var d = drives[i];
                    driveTemps[i] = new KeyValuePair<string, double>(
                        HardwareMonitorService.GetDriveExclusionKey(d),
                        d.Temperature);
                }
            }

            KeyValuePair<string, double>[]? gpuTemps = null;
            KeyValuePair<string, double>[]? gpuLoads = null;
            KeyValuePair<string, double>[]? gpuPowers = null;
            if (snapshot.Gpus is { Count: > 0 } gpus)
            {
                gpuTemps = new KeyValuePair<string, double>[gpus.Count];
                gpuLoads = new KeyValuePair<string, double>[gpus.Count];
                gpuPowers = new KeyValuePair<string, double>[gpus.Count];
                for (int i = 0; i < gpus.Count; i++)
                {
                    var g = gpus[i];
                    string gpuKey = !string.IsNullOrWhiteSpace(g.Identifier) ? g.Identifier : g.Name;
                    gpuTemps[i] = new KeyValuePair<string, double>(gpuKey, g.Temperature);
                    gpuLoads[i] = new KeyValuePair<string, double>(gpuKey, g.Load);
                    gpuPowers[i] = new KeyValuePair<string, double>(
                        gpuKey, g.PowerWatts.HasValue ? g.PowerWatts.Value : double.NaN);
                }
            }

            return new TelemetrySample
            {
                Timestamp = snapshot.Timestamp,
                CpuTemp = snapshot.CpuTemp,
                GpuTemp = snapshot.GpuTemp,
                PrimaryGpuIdentifier = snapshot.GpuIdentifier,
                GpuHotspotTemp = snapshot.GpuHotspotTemp,
                GpuVramTemp = snapshot.GpuVramTemp,
                GpuFanRpm = snapshot.GpuFanRpm,
                CpuGhz = snapshot.CpuGhz,
                CpuLoad = snapshot.CpuLoad,
                CpuPowerWatts = snapshot.CpuPackageWatts,
                GpuLoad = snapshot.GpuLoad,
                GpuPowerWatts = snapshot.GpuPowerWatts,
                RamUsedGb = snapshot.RamUsedGb,
                RamUsagePercent = snapshot.RamUsagePercent,
                CoolingModeActive = snapshot.CoolingModeActive,
                DriveTemps = driveTemps,
                GpuTemps = gpuTemps,
                GpuLoads = gpuLoads,
                GpuPowers = gpuPowers
            };
        }

        /// <summary>Looks up a stored finite GPU temperature for the given GPU identifier, falling back to GpuTemp.</summary>
        public bool TryGetGpuTemp(string? gpuIdentifier, out double celsius)
        {
            if (!string.IsNullOrWhiteSpace(gpuIdentifier))
            {
                if (GpuTemps is { Length: > 0 } arr)
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        if (string.Equals(arr[i].Key, gpuIdentifier, StringComparison.OrdinalIgnoreCase))
                        {
                            if (double.IsFinite(arr[i].Value) && arr[i].Value > 0)
                            {
                                celsius = arr[i].Value;
                                return true;
                            }

                            celsius = double.NaN;
                            return false;
                        }
                    }

                    celsius = double.NaN;
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(PrimaryGpuIdentifier) &&
                    !string.Equals(PrimaryGpuIdentifier, gpuIdentifier, StringComparison.OrdinalIgnoreCase))
                {
                    celsius = double.NaN;
                    return false;
                }
            }

            if (double.IsFinite(GpuTemp) && GpuTemp > 0)
            {
                celsius = GpuTemp;
                return true;
            }

            celsius = double.NaN;
            return false;
        }

        /// <summary>Looks up a stored finite GPU load for the given GPU identifier, falling back to GpuLoad.</summary>
        public bool TryGetGpuLoad(string? gpuIdentifier, out double load)
        {
            if (!string.IsNullOrWhiteSpace(gpuIdentifier))
            {
                if (GpuLoads is { Length: > 0 } arr)
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        if (string.Equals(arr[i].Key, gpuIdentifier, StringComparison.OrdinalIgnoreCase))
                        {
                            load = arr[i].Value;
                            return double.IsFinite(load) && load >= 0;
                        }
                    }

                    load = double.NaN;
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(PrimaryGpuIdentifier) &&
                    !string.Equals(PrimaryGpuIdentifier, gpuIdentifier, StringComparison.OrdinalIgnoreCase))
                {
                    load = double.NaN;
                    return false;
                }
            }

            load = GpuLoad;
            return double.IsFinite(load) && load >= 0;
        }

        /// <summary>Looks up a stored finite GPU power for the given GPU identifier, falling back to GpuPowerWatts.</summary>
        public bool TryGetGpuPower(string? gpuIdentifier, out double watts)
        {
            if (!string.IsNullOrWhiteSpace(gpuIdentifier))
            {
                if (GpuPowers is { Length: > 0 } arr)
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        if (string.Equals(arr[i].Key, gpuIdentifier, StringComparison.OrdinalIgnoreCase))
                        {
                            watts = arr[i].Value;
                            return double.IsFinite(watts) && watts >= 0;
                        }
                    }

                    watts = double.NaN;
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(PrimaryGpuIdentifier) &&
                    !string.Equals(PrimaryGpuIdentifier, gpuIdentifier, StringComparison.OrdinalIgnoreCase))
                {
                    watts = double.NaN;
                    return false;
                }
            }

            watts = GpuPowerWatts ?? double.NaN;
            return double.IsFinite(watts) && watts >= 0;
        }

        /// <summary>Looks up a stored finite storage temperature for the given drive exclusion key.</summary>
        public bool TryGetDriveTemp(string driveKey, out double celsius)
        {
            if (!string.IsNullOrWhiteSpace(driveKey) && DriveTemps is { Length: > 0 } arr)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    if (string.Equals(arr[i].Key, driveKey, StringComparison.OrdinalIgnoreCase) &&
                        double.IsFinite(arr[i].Value) &&
                        arr[i].Value > 0)
                    {
                        celsius = arr[i].Value;
                        return true;
                    }
                }
            }

            celsius = double.NaN;
            return false;
        }
    }
}
