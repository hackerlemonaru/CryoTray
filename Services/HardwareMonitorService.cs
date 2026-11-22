using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CryoTray.Models;
using LibreHardwareMonitor.Hardware;

namespace CryoTray.Services
{
    public class HardwareMonitorService : IDisposable
    {
        private static readonly Regex CoreSensorRegex = new(
            @"^(?:CPU\s*)?(P-Core|E-Core|Core)\s*#\s*(\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CcdTdieRegex = new(
            @"^CCD\s*\d+\s*\(Tdie\)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DiscreteAmdApuNameRegex = new(
            @"^AMD Radeon(?:\(TM\))?\s+\d+M$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CpuCoreSuffixRegex = new(
            @"\b\d+-Core\s+Processor\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MultiSpaceRegex = new(
            @"\s{2,}",
            RegexOptions.Compiled);

        private static HardwareMonitorService? _instance;
        public static HardwareMonitorService Instance => _instance ??= new HardwareMonitorService();

        private Computer? _computer;
        private readonly TelemetryRingBuffer _ringBuffer = new(3600);
        private readonly ProcessorPerformanceReader _processorPerformance = new();
        private readonly Dictionary<IHardware, CachedDriveMetadata> _driveMetadata = new();
        private readonly object _gpuListLock = new();
        private List<GpuDeviceOption> _availableGpus = new();
        private CancellationTokenSource? _cts;
        private bool _isInitialized;
        private bool _openedWithPawnIo;

        public TelemetryRingBuffer RingBuffer => _ringBuffer;
        public HardwareSnapshot CurrentSnapshot { get; private set; } = new();

        public event Action<HardwareSnapshot>? TelemetryUpdated;

        private string _cpuName = "";
        private string _cpuVendor = "";
        private int _cpuCores;
        private double _cpuBaseGhz;
        private double _cpuMaxGhz;

        private sealed class CachedDriveMetadata
        {
            public int? PhysicalDriveNumber { get; init; }
            public string SerialNumber { get; init; } = "";
            public bool? IsSolidState { get; init; }
            public string DriveLetters { get; init; } = "";
        }

        private sealed class CpuSensorSet
        {
            public List<(int KindOrder, int Index, double Temp)> PerCoreTemps { get; } = new();
            public List<double> CcdTdieTemps { get; } = new();
            public List<double> AllTemps { get; } = new();
            public List<(int KindOrder, int Index, double Value)> Loads { get; } = new();
            public List<(int KindOrder, int Index, double Value)> Clocks { get; } = new();
            public double? TctlTdie { get; set; }
            public double? TotalLoad { get; set; }
            public double? PackagePowerWatts { get; set; }
            public double? CorePowerWatts { get; set; }
        }

        public HardwareMonitorService()
        {
            ReadCpuRegistryInfo();
            ReadCpuPhysicalCoreCount();
        }

        private void ReadCpuPhysicalCoreCount()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT NumberOfCores, MaxClockSpeed, Manufacturer FROM Win32_Processor");
                foreach (ManagementObject processor in searcher.Get())
                {
                    using (processor)
                    {
                        int cores = Convert.ToInt32(processor["NumberOfCores"]);
                        int maxMhz = Convert.ToInt32(processor["MaxClockSpeed"]);
                        string? manufacturer = processor["Manufacturer"]?.ToString()?.Trim();
                        if (string.IsNullOrWhiteSpace(_cpuVendor))
                        {
                            _cpuVendor = ResolveCpuVendor(manufacturer, _cpuName);
                        }

                        if (cores > 0)
                        {
                            _cpuCores = cores;
                        }

                        if (maxMhz > 0)
                        {
                            _cpuMaxGhz = Math.Round(maxMhz / 1000.0, 2);
                        }

                        if (cores > 0)
                        {
                            return;
                        }
                    }
                }
            }
            catch
            {
                // Fall back to the registry and logical processor count below.
            }

            _cpuCores = Math.Max(1, Environment.ProcessorCount);
            if (_cpuMaxGhz <= 0)
            {
                _cpuMaxGhz = _cpuBaseGhz;
            }
        }

        private void ReadCpuRegistryInfo()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key == null)
                {
                    return;
                }

                string rawName = key.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "";
                string rawVendor = key.GetValue("VendorIdentifier")?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(rawName))
                {
                    _cpuName = CleanCpuName(rawName);
                }

                _cpuVendor = ResolveCpuVendor(rawVendor, _cpuName);

                if (key.GetValue("~MHz") is int mhz && mhz > 0)
                {
                    _cpuBaseGhz = Math.Round(mhz / 1000.0, 2);
                }
                else if (double.TryParse(key.GetValue("~MHz")?.ToString(), NumberStyles.Number,
                             CultureInfo.InvariantCulture, out double mhzValue) && mhzValue > 0)
                {
                    _cpuBaseGhz = Math.Round(mhzValue / 1000.0, 2);
                }
            }
            catch
            {
                // Keep LHM defaults when the registry value is unavailable.
            }
        }

        internal static string CleanCpuName(string? rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return "";
            }

            string name = CpuCoreSuffixRegex.Replace(rawName.Trim(), "").Trim();
            if (name.EndsWith("Processor", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - "Processor".Length).Trim();
            }

            name = MultiSpaceRegex.Replace(name, " ").Trim();
            return string.IsNullOrWhiteSpace(name) ? "" : name;
        }

        public static string ResolveCpuVendor(string? vendorIdentifier, string? cpuName)
        {
            if (!string.IsNullOrWhiteSpace(vendorIdentifier))
            {
                string id = vendorIdentifier.Trim();
                if (id.Contains("AuthenticAMD", StringComparison.OrdinalIgnoreCase) ||
                    id.Contains("AMD", StringComparison.OrdinalIgnoreCase))
                {
                    return "AMD";
                }
                if (id.Contains("GenuineIntel", StringComparison.OrdinalIgnoreCase) ||
                    id.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                {
                    return "Intel";
                }
                if (id.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase))
                {
                    return "Qualcomm";
                }
                if (id.Contains("Apple", StringComparison.OrdinalIgnoreCase))
                {
                    return "Apple";
                }
                if (id.Contains("ARM", StringComparison.OrdinalIgnoreCase))
                {
                    return "ARM";
                }
                if (id.Contains("CentaurHauls", StringComparison.OrdinalIgnoreCase) ||
                    id.Contains("VIA", StringComparison.OrdinalIgnoreCase))
                {
                    return "VIA";
                }
                if (id.Contains("Shanghai", StringComparison.OrdinalIgnoreCase) ||
                    id.Contains("Zhaoxin", StringComparison.OrdinalIgnoreCase))
                {
                    return "Zhaoxin";
                }
                if (id.Contains("Hygon", StringComparison.OrdinalIgnoreCase))
                {
                    return "Hygon";
                }
            }

            if (!string.IsNullOrWhiteSpace(cpuName))
            {
                string name = cpuName.Trim();
                if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("EPYC", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Threadripper", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Athlon", StringComparison.OrdinalIgnoreCase))
                {
                    return "AMD";
                }
                if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Xeon", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Pentium", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Celeron", StringComparison.OrdinalIgnoreCase))
                {
                    return "Intel";
                }
                if (name.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Snapdragon", StringComparison.OrdinalIgnoreCase))
                {
                    return "Qualcomm";
                }
                if (name.Contains("Apple", StringComparison.OrdinalIgnoreCase))
                {
                    return "Apple";
                }
                if (name.Contains("Zhaoxin", StringComparison.OrdinalIgnoreCase))
                {
                    return "Zhaoxin";
                }
                if (name.Contains("VIA", StringComparison.OrdinalIgnoreCase))
                {
                    return "VIA";
                }
                if (name.Contains("ARM", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Cortex", StringComparison.OrdinalIgnoreCase))
                {
                    return "ARM";
                }
            }

            return "";
        }

        internal static string ResolveGpuVendor(HardwareType type, string? name)
        {
            if (type == HardwareType.GpuNvidia)
            {
                return "Nvidia";
            }
            if (type == HardwareType.GpuAmd)
            {
                return "AMD";
            }
            if (type == HardwareType.GpuIntel)
            {
                return "Intel";
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return "";
            }

            string trimmed = name.Trim();
            if (trimmed.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("GTX", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Quadro", StringComparison.OrdinalIgnoreCase))
            {
                return "Nvidia";
            }
            if (trimmed.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            {
                return "AMD";
            }
            if (trimmed.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Iris", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("UHD", StringComparison.OrdinalIgnoreCase))
            {
                return "Intel";
            }
            if (trimmed.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Adreno", StringComparison.OrdinalIgnoreCase))
            {
                return "Qualcomm";
            }
            if (trimmed.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
            {
                return "Microsoft";
            }

            return "";
        }

        public static bool IsPawnIoInstalled()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\PawnIO");
                return key != null;
            }
            catch
            {
                return false;
            }
        }

        public static void InstallOrOpenPawnIo()
        {
            try
            {
                string[] candidates =
                {
                    Path.Combine(AppContext.BaseDirectory, "PawnIO_setup.exe"),
                    Path.Combine(AppContext.BaseDirectory, "Resources", "PawnIO_setup.exe")
                };

                foreach (string path in candidates)
                {
                    if (File.Exists(path))
                    {
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        return;
                    }
                }

                Process.Start(new ProcessStartInfo("https://pawnio.eu/") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PawnIO launch error: {ex.Message}");
            }
        }

        public static bool IsDiscreteGpu(HardwareType type, string? name)
        {
            if (type == HardwareType.GpuNvidia)
            {
                return true;
            }

            string trimmed = name?.Trim() ?? "";
            if (type == HardwareType.GpuAmd)
            {
                if (trimmed.Equals("AMD Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals("AMD Radeon Graphics", StringComparison.OrdinalIgnoreCase) ||
                    DiscreteAmdApuNameRegex.IsMatch(trimmed))
                {
                    return false;
                }

                return true;
            }

            if (type == HardwareType.GpuIntel)
            {
                return trimmed.Contains("Arc", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        public static GpuTelemetry? SelectPrimaryGpu(IReadOnlyList<GpuTelemetry> gpus, string? preferredIdentifier)
        {
            if (gpus.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(preferredIdentifier))
            {
                string target = preferredIdentifier.Trim();
                var byId = gpus.FirstOrDefault(gpu =>
                    string.Equals(gpu.Identifier, target, StringComparison.OrdinalIgnoreCase));
                if (byId != null)
                {
                    return byId;
                }

                var byName = gpus.FirstOrDefault(gpu =>
                    string.Equals(gpu.Name, target, StringComparison.OrdinalIgnoreCase));
                if (byName != null)
                {
                    return byName;
                }
            }

            return gpus.FirstOrDefault(gpu =>
                       gpu.IsDiscrete && double.IsFinite(gpu.Temperature) && gpu.Temperature > 0)
                ?? gpus.FirstOrDefault(gpu => gpu.IsDiscrete)
                ?? gpus.FirstOrDefault(gpu => double.IsFinite(gpu.Temperature) && gpu.Temperature > 0)
                ?? gpus[0];
        }

        public IReadOnlyList<GpuDeviceOption> GetAvailableGpus()
        {
            lock (_gpuListLock)
            {
                return _availableGpus.ToList();
            }
        }

        /// <summary>Builds a stable drive key for exclusion preferences and alert state tracking.</summary>
        public static string GetDriveExclusionKey(DriveTelemetry drive)
        {
            if (!string.IsNullOrWhiteSpace(drive.SerialNumber))
            {
                return $"serial:{drive.SerialNumber.Trim()}";
            }

            if (!string.IsNullOrWhiteSpace(drive.HardwareIdentifier))
            {
                return $"id:{drive.HardwareIdentifier.Trim()}";
            }

            string model = string.IsNullOrWhiteSpace(drive.Model) ? "drive" : drive.Model.Trim();
            int number = drive.PhysicalDriveNumber ?? drive.Index;
            return $"model:{model}#{number}";
        }

        public static bool IsDriveExcluded(DriveTelemetry drive, IReadOnlyCollection<string>? excludedDrives)
        {
            if (excludedDrives == null || excludedDrives.Count == 0)
            {
                return false;
            }

            string key = GetDriveExclusionKey(drive);
            foreach (string entry in excludedDrives)
            {
                if (string.Equals(entry?.Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Reads system RAM totals and active usage via MemoryTelemetryService without WMI.</summary>
        public static bool TryReadMemoryMetrics(out double usedGb, out double totalGb, out double usagePercent)
        {
            usedGb = double.NaN;
            totalGb = double.NaN;
            usagePercent = double.NaN;

            if (!MemoryTelemetryService.TryReadBreakdown(out var breakdown) || breakdown.TotalUsableBytes == 0)
            {
                return false;
            }

            const double bytesPerGiB = 1024.0 * 1024.0 * 1024.0;
            double total = breakdown.TotalUsableBytes / bytesPerGiB;
            double used = breakdown.InUseBytes / bytesPerGiB;
            double percent = breakdown.InUsePercent;

            usedGb = Math.Round(used, 2);
            totalGb = Math.Round(total, 2);
            usagePercent = Math.Round(Math.Clamp(percent, 0, 100), 1);
            return true;
        }

        private static void PopulateMemoryTelemetry(HardwareSnapshot snapshot)
        {
            if (MemoryTelemetryService.TryReadBreakdown(out var breakdown) && breakdown.TotalUsableBytes > 0)
            {
                const double bytesPerGiB = 1024.0 * 1024.0 * 1024.0;
                snapshot.Memory = breakdown;
                snapshot.RamTotalGb = Math.Round(breakdown.TotalUsableBytes / bytesPerGiB, 2);
                snapshot.RamUsedGb = Math.Round(breakdown.InUseBytes / bytesPerGiB, 2);
                snapshot.RamUsagePercent = Math.Round(Math.Clamp(breakdown.InUsePercent, 0, 100), 1);
            }
        }

        /// <summary>Immediately refreshes physical RAM telemetry in CurrentSnapshot and notifies listeners.</summary>
        public void RefreshMemorySnapshotNow()
        {
            if (!_isInitialized)
            {
                return;
            }

            HardwareSnapshot updated = CloneSnapshot(CurrentSnapshot);
            PopulateMemoryTelemetry(updated);
            updated.Timestamp = DateTime.Now;
            CurrentSnapshot = updated;
            TelemetryUpdated?.Invoke(updated);
        }

        internal static HardwareSnapshot CloneSnapshot(HardwareSnapshot source)
        {
            return new HardwareSnapshot
            {
                CpuName = source.CpuName,
                CpuVendor = source.CpuVendor,
                CpuCores = source.CpuCores,
                CpuTemp = source.CpuTemp,
                PeakCpuTemp = source.PeakCpuTemp,
                MaxCoreTemp = source.MaxCoreTemp,
                CpuGhz = source.CpuGhz,
                CpuBaseGhz = source.CpuBaseGhz,
                CpuMaxGhz = source.CpuMaxGhz,
                CpuLoad = source.CpuLoad,
                CpuPackageWatts = source.CpuPackageWatts,
                CpuCoresTelemetry = source.CpuCoresTelemetry,
                RamUsedGb = source.RamUsedGb,
                RamTotalGb = source.RamTotalGb,
                RamUsagePercent = source.RamUsagePercent,
                Memory = source.Memory,
                GpuIdentifier = source.GpuIdentifier,
                GpuName = source.GpuName,
                GpuVendor = source.GpuVendor,
                GpuTemp = source.GpuTemp,
                GpuLoad = source.GpuLoad,
                GpuPowerWatts = source.GpuPowerWatts,
                GpuHotspotTemp = source.GpuHotspotTemp,
                GpuVramTemp = source.GpuVramTemp,
                GpuFanRpm = source.GpuFanRpm,
                GpuVramUsedMb = source.GpuVramUsedMb,
                GpuVramTotalMb = source.GpuVramTotalMb,
                GpuCoreClockMhz = source.GpuCoreClockMhz,
                GpuMemoryClockMhz = source.GpuMemoryClockMhz,
                GpuThrottleReason = source.GpuThrottleReason,
                Gpus = source.Gpus,
                Fans = source.Fans,
                StorageDrives = source.StorageDrives,
                PawnIoInstalled = source.PawnIoInstalled,
                CoolingModeActive = source.CoolingModeActive,
                Timestamp = source.Timestamp
            };
        }

        private void ReadHardwareNamesFromLhm()
        {
            if (_computer == null)
            {
                return;
            }

            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.HardwareType == HardwareType.Cpu && !string.IsNullOrWhiteSpace(hardware.Name))
                {
                    _cpuName = CleanCpuName(hardware.Name);
                    if (string.IsNullOrWhiteSpace(_cpuVendor))
                    {
                        _cpuVendor = ResolveCpuVendor(null, _cpuName);
                    }
                }
            }
        }

        public void Start()
        {
            if (_isInitialized) return;
            _isInitialized = true;
            _openedWithPawnIo = IsPawnIoInstalled();
            SettingsService.Instance.SettingsChanged += OnSettingsChanged;

            try
            {
                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsStorageEnabled = true,
                    IsMotherboardEnabled = true,
                    IsControllerEnabled = true
                };
                _computer.Open();
                ReadHardwareNamesFromLhm();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LibreHardwareMonitor Open error: {ex.Message}");
            }

            _cts = new CancellationTokenSource();
            Task.Run(() => PollingLoop(_cts.Token));
        }

        private void OnSettingsChanged()
        {
            if (!_isInitialized)
            {
                return;
            }

            HardwareSnapshot current = CurrentSnapshot;
            if (current.Gpus.Count == 0)
            {
                return;
            }

            var updated = CloneSnapshot(current);

            GpuTelemetry? primary = SelectPrimaryGpu(
                updated.Gpus,
                SettingsService.Instance.Current.PreferredGpuIdentifier);
            ApplyPrimaryGpu(updated, primary);
            CurrentSnapshot = updated;
            TelemetryUpdated?.Invoke(updated);
        }

        private async Task PollingLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var snapshot = ReadHardware();
                    CurrentSnapshot = snapshot;

                    // Add to RingBuffer
                    _ringBuffer.AddSample(TelemetrySample.FromSnapshot(snapshot));

                    // Notify subscribers
                    TelemetryUpdated?.Invoke(snapshot);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Hardware poll error: {ex.Message}");
                }

                try
                {
                    await Task.Delay(1000, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private HardwareSnapshot ReadHardware()
        {
            bool pawnIoInstalled = IsPawnIoInstalled();
            if (pawnIoInstalled && !_openedWithPawnIo && _computer != null)
            {
                try
                {
                    _computer.Close();
                    _computer.Open();
                    _openedWithPawnIo = true;
                    _driveMetadata.Clear();
                    ReadHardwareNamesFromLhm();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"LibreHardwareMonitor reopen error: {ex.Message}");
                }
            }

            var snapshot = new HardwareSnapshot
            {
                CpuName = _cpuName,
                CpuCores = _cpuCores,
                CpuBaseGhz = _cpuBaseGhz > 0 ? _cpuBaseGhz : double.NaN,
                CpuMaxGhz = _cpuMaxGhz > 0 ? _cpuMaxGhz : (_cpuBaseGhz > 0 ? _cpuBaseGhz : double.NaN),
                PawnIoInstalled = pawnIoInstalled,
                CoolingModeActive = PowerPlanService.Instance.IsCoolingModeActive,
                Timestamp = DateTime.Now
            };

            var cpuSensors = new CpuSensorSet();
            double? motherboardCpuTemp = null;
            var gpus = new List<GpuTelemetry>();
            var fans = new List<FanTelemetry>();
            var fanKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var drives = new List<DriveTelemetry>();
            int driveIndex = 0;

            if (_computer != null)
            {
                foreach (var hardware in _computer.Hardware)
                {
                    hardware.Update();

                    if (hardware.HardwareType != HardwareType.Cpu &&
                        hardware.HardwareType != HardwareType.GpuNvidia &&
                        hardware.HardwareType != HardwareType.GpuAmd &&
                        hardware.HardwareType != HardwareType.GpuIntel &&
                        hardware.HardwareType != HardwareType.Storage)
                    {
                        CollectFanSensors(hardware, fans, fanKeys);
                    }

                    if (hardware.HardwareType == HardwareType.Cpu)
                    {
                        CollectCpuSensors(hardware.Sensors, cpuSensors);

                        foreach (var sub in hardware.SubHardware)
                        {
                            sub.Update();
                            CollectCpuSensors(sub.Sensors, cpuSensors);
                        }
                    }
                    else if (hardware.HardwareType == HardwareType.Motherboard)
                    {
                        if (!motherboardCpuTemp.HasValue)
                        {
                            foreach (var sensor in hardware.Sensors)
                            {
                                if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                                {
                                    if (sensor.Name.ToLowerInvariant().Contains("cpu") &&
                                        sensor.Value.Value > 0 &&
                                        sensor.Value.Value < 125)
                                    {
                                        motherboardCpuTemp = sensor.Value.Value;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    else if (hardware.HardwareType == HardwareType.GpuNvidia ||
                             hardware.HardwareType == HardwareType.GpuAmd ||
                             hardware.HardwareType == HardwareType.GpuIntel)
                    {
                        gpus.Add(ReadGpuTelemetry(hardware));
                    }
                    else if (hardware.HardwareType == HardwareType.Storage)
                    {
                        drives.Add(CreateDriveTelemetry(hardware, driveIndex++));
                    }
                }
            }

            int detectedCores = cpuSensors.PerCoreTemps.Select(c => (c.KindOrder, c.Index))
                .Concat(cpuSensors.Loads.Select(c => (c.KindOrder, c.Index)))
                .Concat(cpuSensors.Clocks.Select(c => (c.KindOrder, c.Index)))
                .Distinct()
                .Count();
            if (_cpuCores <= 0 && detectedCores > 0)
            {
                _cpuCores = detectedCores;
            }

            snapshot.CpuName = _cpuName;
            snapshot.CpuVendor = _cpuVendor;
            snapshot.CpuCores = _cpuCores;

            double? rawLoad = cpuSensors.TotalLoad
                ?? (cpuSensors.Loads.Count > 0 ? cpuSensors.Loads.Average(l => l.Value) : null);
            snapshot.CpuLoad = rawLoad.HasValue && double.IsFinite(rawLoad.Value)
                ? Math.Round(Math.Clamp(rawLoad.Value, 0, 100), 1)
                : double.NaN;

            double? packagePowerWatts = cpuSensors.PackagePowerWatts
                ?? cpuSensors.CorePowerWatts;
            snapshot.CpuPackageWatts = packagePowerWatts.HasValue
                ? (int)Math.Round(packagePowerWatts.Value)
                : null;

            double? peak = ResolvePeak(cpuSensors, motherboardCpuTemp);
            if (peak.HasValue && double.IsFinite(peak.Value) && peak.Value > 0)
            {
                double roundedPeak = Math.Round(peak.Value, 1);
                int physicalCores = Math.Max(1, _cpuCores > 0 ? _cpuCores : Environment.ProcessorCount);
                var cores = BuildCoreTemperatures(cpuSensors, roundedPeak, physicalCores);
                int maxDisplayed = cores.Max(c => c.DisplayTemp);

                foreach (var core in cores)
                {
                    core.IsHottest = core.DisplayTemp == maxDisplayed;
                }

                snapshot.PeakCpuTemp = roundedPeak;
                snapshot.MaxCoreTemp = maxDisplayed;
                snapshot.CpuTemp = maxDisplayed;
                snapshot.CpuCoresTelemetry = cores;
            }
            else
            {
                snapshot.PeakCpuTemp = double.NaN;
                snapshot.MaxCoreTemp = double.NaN;
                snapshot.CpuTemp = double.NaN;
                snapshot.CpuCoresTelemetry = new List<CoreTelemetry>();
            }

            var clockValues = cpuSensors.Clocks
                .Select(c => c.Value)
                .Where(value => double.IsFinite(value) && value > 100)
                .ToList();
            double? currentClock = clockValues.Count > 0 ? clockValues.Average() : null;

            if (_processorPerformance.TryRead(out double performancePercent) &&
                performancePercent > 0 &&
                _cpuBaseGhz > 0)
            {
                double effectiveClock = _cpuBaseGhz * 1000.0 *
                    (Math.Clamp(performancePercent, 0, 200) / 100.0);
                if (effectiveClock > 0)
                {
                    currentClock = effectiveClock;
                }
            }

            if (_cpuBaseGhz <= 0 && currentClock.HasValue && currentClock.Value > 0)
            {
                _cpuBaseGhz = Math.Round(currentClock.Value / 1000.0, 2);
            }

            snapshot.CpuBaseGhz = _cpuBaseGhz > 0 ? _cpuBaseGhz : double.NaN;
            snapshot.CpuMaxGhz = _cpuMaxGhz > 0
                ? _cpuMaxGhz
                : (_cpuBaseGhz > 0 ? _cpuBaseGhz : double.NaN);
            snapshot.CpuGhz = currentClock.HasValue && double.IsFinite(currentClock.Value) && currentClock.Value > 0
                ? Math.Round(currentClock.Value / 1000.0, 2)
                : double.NaN;

            snapshot.Gpus = gpus;
            UpdateAvailableGpus(gpus);
            GpuTelemetry? primaryGpu = SelectPrimaryGpu(
                gpus,
                SettingsService.Instance.Current.PreferredGpuIdentifier);
            ApplyPrimaryGpu(snapshot, primaryGpu);

            PopulateMemoryTelemetry(snapshot);

            snapshot.StorageDrives = drives;
            snapshot.Fans = fans;
            return snapshot;
        }

        private void UpdateAvailableGpus(IReadOnlyList<GpuTelemetry> gpus)
        {
            var duplicateCounts = gpus
                .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var options = new List<GpuDeviceOption>(gpus.Count);
            foreach (var gpu in gpus)
            {
                string displayName = !string.IsNullOrWhiteSpace(gpu.Name)
                    ? gpu.Name
                    : gpu.Identifier;
                if (!string.IsNullOrWhiteSpace(gpu.Name) &&
                    duplicateCounts.TryGetValue(gpu.Name, out int count) &&
                    count > 1 &&
                    !string.IsNullOrWhiteSpace(gpu.Identifier))
                {
                    displayName = $"{gpu.Name} ({gpu.Identifier})";
                }

                options.Add(new GpuDeviceOption
                {
                    Identifier = gpu.Identifier,
                    Name = gpu.Name,
                    Vendor = gpu.Vendor,
                    IsDiscrete = gpu.IsDiscrete,
                    DisplayName = displayName
                });
            }

            lock (_gpuListLock)
            {
                _availableGpus = options;
            }
        }

        private static void ApplyPrimaryGpu(HardwareSnapshot snapshot, GpuTelemetry? primary)
        {
            if (primary == null)
            {
                snapshot.GpuIdentifier = "";
                snapshot.GpuName = "";
                snapshot.GpuVendor = "";
                snapshot.GpuTemp = double.NaN;
                snapshot.GpuLoad = double.NaN;
                snapshot.GpuPowerWatts = null;
                snapshot.GpuHotspotTemp = null;
                snapshot.GpuVramTemp = null;
                snapshot.GpuFanRpm = null;
                snapshot.GpuVramUsedMb = null;
                snapshot.GpuVramTotalMb = null;
                snapshot.GpuCoreClockMhz = null;
                snapshot.GpuMemoryClockMhz = null;
                snapshot.GpuThrottleReason = null;
                return;
            }

            snapshot.GpuIdentifier = primary.Identifier;
            snapshot.GpuName = primary.Name;
            snapshot.GpuVendor = primary.Vendor;
            snapshot.GpuTemp = primary.Temperature;
            snapshot.GpuLoad = primary.Load;
            snapshot.GpuPowerWatts = primary.PowerWatts;
            snapshot.GpuHotspotTemp = primary.HotspotTemp;
            snapshot.GpuVramTemp = primary.VramTemp;
            snapshot.GpuFanRpm = primary.FanRpm;
            snapshot.GpuVramUsedMb = primary.VramUsedMb;
            snapshot.GpuVramTotalMb = primary.VramTotalMb;
            snapshot.GpuCoreClockMhz = primary.CoreClockMhz;
            snapshot.GpuMemoryClockMhz = primary.MemoryClockMhz;
            snapshot.GpuThrottleReason = primary.ThrottleReason;
        }

        private static void CollectFanSensors(
            IHardware hardware,
            List<FanTelemetry> fans,
            HashSet<string> seen)
        {
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Fan || !sensor.Value.HasValue)
                {
                    continue;
                }

                double rpm = sensor.Value.Value;
                if (!double.IsFinite(rpm) || rpm < 0 || rpm > 20000)
                {
                    continue;
                }

                string name = string.IsNullOrWhiteSpace(sensor.Name)
                    ? hardware.Name?.Trim() ?? ""
                    : sensor.Name.Trim();
                if (name.Length == 0)
                {
                    name = "Fan";
                }

                string key = $"{hardware.Identifier}|{name}";
                if (!seen.Add(key))
                {
                    continue;
                }

                fans.Add(new FanTelemetry
                {
                    Name = name,
                    Rpm = (int)Math.Round(rpm)
                });
            }

            foreach (var sub in hardware.SubHardware)
            {
                sub.Update();
                CollectFanSensors(sub, fans, seen);
            }
        }

        internal static int ScoreGpuLoadSensor(string sensorName)
        {
            string lower = sensorName.ToLowerInvariant();
            if (lower.Contains("gpu core") || lower == "core")
            {
                return 4;
            }

            if (lower.Contains("core") && !lower.Contains("memory"))
            {
                return 3;
            }

            if (lower.Contains("d3d 3d") || lower.Contains("3d"))
            {
                return 2;
            }

            if (lower.Contains("memory") || lower.Contains("video") || lower.Contains("copy") || lower.Contains("bus"))
            {
                return 0;
            }

            return 1;
        }

        private static GpuTelemetry ReadGpuTelemetry(IHardware hardware)
        {
            string name = hardware.Name?.Trim() ?? "";
            string vendor = ResolveGpuVendor(hardware.HardwareType, name);

            double? gpuTemp = null;
            double? gpuTempFallback = null;
            double? gpuHotspotTemp = null;
            double? gpuVramTemp = null;
            int? gpuFanRpm = null;
            double? gpuLoad = null;
            int gpuLoadPriority = -1;
            double? gpuPower = null;
            double? gpuPowerFallback = null;
            double? gpuVramUsedMb = null;
            double? gpuVramTotalMb = null;
            double? gpuCoreClockMhz = null;
            double? gpuMemoryClockMhz = null;
            string? gpuThrottleReason = null;

            void CollectSensors(IReadOnlyList<ISensor> sensors)
            {
                foreach (var sensor in sensors)
                {
                    if (!sensor.Value.HasValue)
                    {
                        continue;
                    }

                    string sensorName = sensor.Name?.ToLowerInvariant() ?? "";
                    if (sensor.SensorType == SensorType.Temperature)
                    {
                        double value = sensor.Value.Value;
                        if (value <= 0 || value >= 150 || !double.IsFinite(value))
                        {
                            continue;
                        }

                        if (sensorName.Contains("hot spot") || sensorName.Contains("hotspot"))
                        {
                            gpuHotspotTemp ??= Math.Round(value, 1);
                        }
                        else if (sensorName.Contains("memory") || sensorName.Contains("vram"))
                        {
                            gpuVramTemp ??= Math.Round(value, 1);
                        }
                        else if (sensorName.Contains("core"))
                        {
                            gpuTemp ??= Math.Round(value, 1);
                        }
                        else
                        {
                            gpuTempFallback ??= Math.Round(value, 1);
                        }
                    }
                    else if (sensor.SensorType == SensorType.Load)
                    {
                        double value = sensor.Value.Value;
                        if (double.IsFinite(value) && value >= 0 && value <= 100)
                        {
                            int priority = ScoreGpuLoadSensor(sensorName);
                            if (priority > gpuLoadPriority)
                            {
                                gpuLoadPriority = priority;
                                gpuLoad = value;
                            }
                        }
                    }
                    else if (sensor.SensorType == SensorType.Power)
                    {
                        double watts = sensor.Value.Value;
                        if (watts <= 0 || !double.IsFinite(watts))
                        {
                            continue;
                        }

                        bool preferred = sensorName.Contains("gpu power") ||
                            sensorName.Contains("package") ||
                            sensorName.Contains("board") ||
                            sensorName.Contains("total") ||
                            (sensorName.Contains("gpu") && !sensorName.Contains("memory"));

                        if (preferred)
                        {
                            gpuPower ??= watts;
                        }
                        else
                        {
                            gpuPowerFallback = gpuPowerFallback.HasValue
                                ? Math.Max(gpuPowerFallback.Value, watts)
                                : watts;
                        }
                    }
                    else if (sensor.SensorType == SensorType.Fan)
                    {
                        double rpm = sensor.Value.Value;
                        if (rpm >= 0 && double.IsFinite(rpm))
                        {
                            gpuFanRpm ??= (int)Math.Round(rpm);
                        }
                    }
                    else if (sensor.SensorType == SensorType.Clock)
                    {
                        double megahertz = sensor.Value.Value;
                        if (megahertz <= 0 || megahertz > 20000 || !double.IsFinite(megahertz))
                        {
                            continue;
                        }

                        if (sensorName.Contains("memory"))
                        {
                            gpuMemoryClockMhz ??= Math.Round(megahertz, 0);
                        }
                        else if (sensorName.Contains("core") || sensorName.Contains("graphics"))
                        {
                            gpuCoreClockMhz ??= Math.Round(megahertz, 0);
                        }
                    }
                    else if (sensor.SensorType == SensorType.SmallData || sensor.SensorType == SensorType.Data)
                    {
                        double raw = sensor.Value.Value;
                        if (!double.IsFinite(raw) || raw < 0)
                        {
                            continue;
                        }

                        double megabytes = sensor.SensorType == SensorType.Data ? raw * 1024.0 : raw;
                        if (sensorName.Contains("memory used") || sensorName.Contains("dedicated memory used"))
                        {
                            gpuVramUsedMb ??= Math.Round(megabytes, 0);
                        }
                        else if (sensorName.Contains("memory total") || sensorName.Contains("dedicated memory total"))
                        {
                            gpuVramTotalMb ??= Math.Round(megabytes, 0);
                        }
                    }
                    else if (sensor.SensorType == SensorType.Factor)
                    {
                        double state = sensor.Value.Value;
                        if (state > 0.5 &&
                            (sensorName.Contains("performance limit") ||
                             sensorName.Contains("throttle")))
                        {
                            gpuThrottleReason ??= sensor.Name?.Trim();
                        }
                    }
                }
            }

            CollectSensors(hardware.Sensors);
            foreach (var sub in hardware.SubHardware)
            {
                sub.Update();
                CollectSensors(sub.Sensors);
            }

            double? effectiveTemp = gpuTemp ?? gpuTempFallback;
            double? effectivePower = gpuPower ?? gpuPowerFallback;

            return new GpuTelemetry
            {
                Identifier = hardware.Identifier.ToString(),
                Name = name,
                Vendor = vendor,
                IsDiscrete = IsDiscreteGpu(hardware.HardwareType, name),
                Temperature = effectiveTemp.HasValue && effectiveTemp.Value > 0
                    ? Math.Round(effectiveTemp.Value, 1)
                    : double.NaN,
                Load = gpuLoad.HasValue && double.IsFinite(gpuLoad.Value)
                    ? Math.Round(gpuLoad.Value, 1)
                    : double.NaN,
                PowerWatts = effectivePower.HasValue ? (int)Math.Round(effectivePower.Value) : null,
                HotspotTemp = gpuHotspotTemp,
                VramTemp = gpuVramTemp,
                FanRpm = gpuFanRpm,
                VramUsedMb = gpuVramUsedMb,
                VramTotalMb = gpuVramTotalMb,
                CoreClockMhz = gpuCoreClockMhz,
                MemoryClockMhz = gpuMemoryClockMhz,
                ThrottleReason = gpuThrottleReason
            };
        }

        private CachedDriveMetadata ReadDriveMetadata(IHardware hardware)
        {
            if (_driveMetadata.TryGetValue(hardware, out var cached))
            {
                return cached;
            }

            int? driveNumber = null;
            string serial = "";
            bool? isSolidState = null;
            string driveLetters = "";

            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                object? storage = hardware.GetType().GetProperty("Storage", flags)?.GetValue(hardware);
                object?[] targets = storage != null ? new[] { storage, hardware } : new object?[] { hardware };

                foreach (object? target in targets)
                {
                    if (target == null)
                    {
                        continue;
                    }

                    var type = target.GetType();

                    if (!driveNumber.HasValue)
                    {
                        object? rawNum = type.GetProperty("DriveNumber", flags)?.GetValue(target);
                        if (rawNum != null)
                        {
                            try
                            {
                                int num = Convert.ToInt32(rawNum, CultureInfo.InvariantCulture);
                                if (num >= 0)
                                {
                                    driveNumber = num;
                                }
                            }
                            catch
                            {
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(serial))
                    {
                        string? rawSerial = type.GetProperty("SerialNumber", flags)?.GetValue(target)?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(rawSerial))
                        {
                            serial = rawSerial;
                        }
                    }

                    if (!isSolidState.HasValue)
                    {
                        if (type.GetProperty("IsSSD", flags)?.GetValue(target) is bool isSsd)
                        {
                            isSolidState = isSsd;
                        }
                        else if (type.GetProperty("IsNVMe", flags)?.GetValue(target) is bool isNvme && isNvme)
                        {
                            isSolidState = true;
                        }
                    }

                    if (string.IsNullOrEmpty(driveLetters) &&
                        type.GetProperty("Partitions", flags)?.GetValue(target) is IEnumerable partitions)
                    {
                        var letters = new List<string>();
                        foreach (object? partition in partitions)
                        {
                            if (partition == null)
                            {
                                continue;
                            }

                            object? rawLetter = partition.GetType()
                                .GetProperty("DriveLetter", flags)
                                ?.GetValue(partition);
                            string letter = rawLetter?.ToString()?.Trim().TrimEnd(':') ?? "";
                            if (letter.Length == 1 && char.IsLetter(letter[0]))
                            {
                                string upper = letter.ToUpperInvariant();
                                if (!letters.Contains(upper, StringComparer.OrdinalIgnoreCase))
                                {
                                    letters.Add(upper);
                                }
                            }
                        }

                        if (letters.Count > 0)
                        {
                            driveLetters = string.Join(", ", letters);
                        }
                    }
                }
            }
            catch
            {
            }

            cached = new CachedDriveMetadata
            {
                PhysicalDriveNumber = driveNumber,
                SerialNumber = serial,
                IsSolidState = isSolidState,
                DriveLetters = driveLetters
            };
            _driveMetadata[hardware] = cached;
            return cached;
        }

        private DriveTelemetry CreateDriveTelemetry(IHardware hardware, int index)
        {
            double? compositeTemp = null;
            double? firstTemp = null;
            int? lifePercent = null;
            int? percentageUsed = null;
            double? totalSpace = null;

            foreach (var sensor in hardware.Sensors)
            {
                if (!sensor.Value.HasValue)
                {
                    continue;
                }

                double value = sensor.Value.Value;
                string name = sensor.Name.Trim();

                if (sensor.SensorType == SensorType.Temperature)
                {
                    if (!IsValidDriveTemperature(value))
                    {
                        continue;
                    }

                    firstTemp ??= value;
                    if (name.Contains("composite", StringComparison.OrdinalIgnoreCase))
                    {
                        compositeTemp ??= value;
                    }
                }
                else if (sensor.SensorType == SensorType.Level &&
                         name.Equals("Life", StringComparison.OrdinalIgnoreCase))
                {
                    lifePercent = Math.Clamp((int)Math.Round(value), 0, 100);
                }
                else if (sensor.SensorType == SensorType.Level &&
                         name.Equals("Percentage Used", StringComparison.OrdinalIgnoreCase))
                {
                    percentageUsed = Math.Clamp((int)Math.Round(value), 0, 100);
                }
                else if (sensor.SensorType == SensorType.Data &&
                         name.Equals("Total Space", StringComparison.OrdinalIgnoreCase))
                {
                    totalSpace = value;
                }
            }

            int? healthPercent = lifePercent
                ?? (percentageUsed.HasValue ? 100 - percentageUsed.Value : null);
            CachedDriveMetadata metadata = ReadDriveMetadata(hardware);

            return new DriveTelemetry
            {
                Index = index,
                Model = hardware.Name,
                Temperature = compositeTemp ?? firstTemp ?? double.NaN,
                SmartHealthPercent = healthPercent,
                StatusText = DescribeDriveHealth(healthPercent),
                DriveLetters = metadata.DriveLetters,
                CapacityGb = totalSpace.HasValue ? Math.Round(totalSpace.Value, 1) : 0,
                PhysicalDriveNumber = metadata.PhysicalDriveNumber,
                SerialNumber = metadata.SerialNumber,
                HardwareIdentifier = hardware.Identifier.ToString(),
                IsSolidState = metadata.IsSolidState
            };
        }

        internal static bool IsValidDriveTemperature(double celsius)
        {
            return double.IsFinite(celsius) && celsius > 0 && celsius < 150;
        }

        private static string DescribeDriveHealth(int? healthPercent)
        {
            if (!healthPercent.HasValue)
            {
                return "Unknown";
            }

            if (healthPercent.Value >= 90)
            {
                return "Good";
            }

            if (healthPercent.Value >= 70)
            {
                return "Fair";
            }

            if (healthPercent.Value >= 50)
            {
                return "Worn";
            }

            return "Replace";
        }

        private static void CollectCpuSensors(IReadOnlyList<ISensor> sensors, CpuSensorSet set)
        {
            foreach (var sensor in sensors)
            {
                if (!sensor.Value.HasValue)
                {
                    continue;
                }

                double value = sensor.Value.Value;
                string name = sensor.Name.Trim();
                string lower = name.ToLowerInvariant();

                if (sensor.SensorType == SensorType.Temperature)
                {
                    if (lower.Contains("distance to tjmax") || lower.Contains("soc"))
                    {
                        continue;
                    }

                    if (value <= 0 || value >= 125)
                    {
                        continue;
                    }

                    set.AllTemps.Add(value);

                    var coreMatch = CoreSensorRegex.Match(name);
                    if (coreMatch.Success)
                    {
                        if (lower.Contains("tctl/tdie") || lower.Contains("core max") || lower.Contains("core average"))
                        {
                            continue;
                        }

                        if (!int.TryParse(coreMatch.Groups[2].Value, out int coreIndex))
                        {
                            continue;
                        }

                        UpsertCoreTemperature(set.PerCoreTemps, GetKindOrder(coreMatch.Groups[1].Value), coreIndex, value);
                        continue;
                    }

                    if (CcdTdieRegex.IsMatch(name))
                    {
                        set.CcdTdieTemps.Add(value);
                        continue;
                    }

                    if (lower.Contains("tctl/tdie"))
                    {
                        set.TctlTdie ??= value;
                    }
                }
                else if (sensor.SensorType == SensorType.Load)
                {
                    if (lower.Contains("total"))
                    {
                        set.TotalLoad = value;
                        continue;
                    }

                    var coreMatch = CoreSensorRegex.Match(name);
                    if (coreMatch.Success && int.TryParse(coreMatch.Groups[2].Value, out int coreIndex))
                    {
                        UpsertCoreValue(set.Loads, GetKindOrder(coreMatch.Groups[1].Value), coreIndex, value);
                    }
                }
                else if (sensor.SensorType == SensorType.Clock)
                {
                    if (lower.Contains("bus speed") || value <= 100)
                    {
                        continue;
                    }

                    var coreMatch = CoreSensorRegex.Match(name);
                    if (coreMatch.Success && int.TryParse(coreMatch.Groups[2].Value, out int coreIndex))
                    {
                        UpsertCoreValue(set.Clocks, GetKindOrder(coreMatch.Groups[1].Value), coreIndex, value);
                    }
                }
                else if (sensor.SensorType == SensorType.Power && value > 0)
                {
                    if (lower == "package" || lower.Contains("cpu package"))
                    {
                        set.PackagePowerWatts ??= value;
                    }
                    else if (lower.Contains("core"))
                    {
                        set.CorePowerWatts ??= value;
                    }
                }
            }
        }

        private static int GetKindOrder(string kind)
        {
            if (kind.Equals("P-Core", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (kind.Equals("E-Core", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            return 2;
        }

        private static void UpsertCoreTemperature(List<(int KindOrder, int Index, double Temp)> values, int kindOrder, int index, double temp)
        {
            for (int i = 0; i < values.Count; i++)
            {
                var existing = values[i];
                if (existing.KindOrder == kindOrder && existing.Index == index)
                {
                    if (temp > existing.Temp)
                    {
                        values[i] = (kindOrder, index, temp);
                    }

                    return;
                }
            }

            values.Add((kindOrder, index, temp));
        }

        private static void UpsertCoreValue(List<(int KindOrder, int Index, double Value)> values, int kindOrder, int index, double value)
        {
            for (int i = 0; i < values.Count; i++)
            {
                var existing = values[i];
                if (existing.KindOrder == kindOrder && existing.Index == index)
                {
                    if (value > existing.Value)
                    {
                        values[i] = (kindOrder, index, value);
                    }

                    return;
                }
            }

            values.Add((kindOrder, index, value));
        }

        private static double? ResolvePeak(CpuSensorSet set, double? motherboardCpuTemp)
        {
            // AMD's Tctl/Tdie carries a control offset; the CCD diode matches the core
            // temperature displayed in CryoTray.
            if (set.CcdTdieTemps.Count > 0)
            {
                return set.CcdTdieTemps.Max();
            }

            if (set.TctlTdie.HasValue)
            {
                return set.TctlTdie.Value;
            }

            if (set.AllTemps.Count > 0)
            {
                return set.AllTemps.Max();
            }

            return motherboardCpuTemp;
        }

        private static double ResolveFloor(double peak)
        {
            // Keep the popup inside a 3.5 C span instead of letting CCD lows stretch it.
            return Math.Clamp(peak - 3.5, 0, peak);
        }

        private List<CoreTelemetry> BuildCoreTemperatures(CpuSensorSet set, double peak, int physicalCores)
        {
            var cores = new List<CoreTelemetry>();

            var perCore = set.PerCoreTemps
                .OrderBy(t => t.KindOrder)
                .ThenBy(t => t.Index)
                .ToList();

            // AMD exposes a package-level Tctl/Tdie reading, and its per-core sensors are
            // not true thermal diodes. Keep direct core mapping for Intel-style sensors only.
            bool useDirectPerCore = perCore.Count > 0 &&
                !set.TctlTdie.HasValue &&
                perCore.Count <= physicalCores;
            if (useDirectPerCore)
            {
                foreach (var sensor in perCore)
                {
                    cores.Add(CreateCoreTelemetry(cores.Count, sensor.Temp));
                }
            }

            int targetCount = Math.Max(physicalCores, cores.Count);
            if (cores.Count < targetCount)
            {
                double floor = ResolveFloor(peak);
                double[] weightedTemps = BuildWeightedTemperatures(set, peak, floor, targetCount);

                for (int i = cores.Count; i < weightedTemps.Length; i++)
                {
                    cores.Add(CreateCoreTelemetry(i, weightedTemps[i]));
                }
            }

            if (cores.Count == 0)
            {
                cores.Add(CreateCoreTelemetry(0, peak));
            }

            return cores;
        }

        private double[] BuildWeightedTemperatures(CpuSensorSet set, double peak, double floor, int physicalCores)
        {
            var temps = new double[physicalCores];

            var loads = set.Loads
                .OrderBy(v => v.KindOrder)
                .ThenBy(v => v.Index)
                .Select(v => v.Value)
                .ToList();

            var clocks = set.Clocks
                .OrderBy(v => v.KindOrder)
                .ThenBy(v => v.Index)
                .Select(v => v.Value)
                .ToList();

            if (loads.Count == 0 && clocks.Count == 0)
            {
                for (int i = 0; i < physicalCores; i++)
                {
                    temps[i] = peak;
                }

                return temps;
            }

            double maxClock = clocks.Count > 0
                ? clocks.Max()
                : Math.Max(1.0, _cpuBaseGhz * 1000.0);

            var activity = new double[physicalCores];
            for (int i = 0; i < physicalCores; i++)
            {
                double load = SampleMax(loads, i, physicalCores);
                double clock = SampleMax(clocks, i, physicalCores);
                double normalizedLoad = Math.Clamp(load, 0, 100) / 100.0;
                double normalizedClock = Math.Clamp(clock, 0, maxClock) / maxClock;
                activity[i] = (0.70 * normalizedLoad) + (0.30 * normalizedClock);
            }

            double minActivity = activity.Min();
            double maxActivity = activity.Max();
            double spread = maxActivity - minActivity;
            double range = Math.Max(0, peak - floor);

            for (int i = 0; i < physicalCores; i++)
            {
                double weight = spread < 0.02
                    ? (maxActivity > 0 ? activity[i] / maxActivity : 1.0)
                    : (activity[i] - minActivity) / spread;

                weight = Math.Clamp(weight, 0, 1);
                temps[i] = floor + (weight * range);
            }

            return temps;
        }

        private static double SampleMax(IReadOnlyList<double> values, int coreIndex, int coreCount)
        {
            if (values.Count == 0)
            {
                return 0;
            }

            int start = (int)((long)coreIndex * values.Count / coreCount);
            int end = (int)((long)(coreIndex + 1) * values.Count / coreCount);

            if (end <= start)
            {
                end = start + 1;
            }

            if (start >= values.Count)
            {
                start = values.Count - 1;
            }

            if (end > values.Count)
            {
                end = values.Count;
            }

            double max = values[start];
            for (int i = start + 1; i < end; i++)
            {
                max = Math.Max(max, values[i]);
            }

            return max;
        }

        private static CoreTelemetry CreateCoreTelemetry(int index, double temperature)
        {
            double clamped = Math.Clamp(temperature, 0, 125);
            return new CoreTelemetry
            {
                Index = index,
                Name = $"CPU{index:D2}",
                Temperature = Math.Round(clamped, 1),
                DisplayTemp = (int)Math.Round(clamped)
            };
        }

        public void Stop()
        {
            SettingsService.Instance.SettingsChanged -= OnSettingsChanged;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            try
            {
                _computer?.Close();
            }
            catch
            {
                // Ignore
            }
            _computer = null;
            _driveMetadata.Clear();
            _openedWithPawnIo = false;
            _isInitialized = false;
        }

        public void Dispose()
        {
            Stop();
            _processorPerformance.Dispose();
        }
    }
}
