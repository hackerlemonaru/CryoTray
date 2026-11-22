using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Text;
using System.Xml.Linq;
using CryoTray.Models;

namespace CryoTray.Services
{
    public class SystemInfoQueryService
    {
        private static SystemInfoQueryService? _instance;
        public static SystemInfoQueryService Instance => _instance ??= new SystemInfoQueryService();

        public SystemInfoData QuerySystemInfo()
        {
            var info = new SystemInfoData
            {
                PcName = Environment.MachineName
            };

            // 1. Processor
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores FROM Win32_Processor");
                using var results = searcher.Get();
                foreach (ManagementObject obj in results)
                {
                    using var row = obj;
                    string? processorName = obj["Name"]?.ToString()?.Trim();
                    info.ProcessorName = string.IsNullOrWhiteSpace(processorName)
                        ? LocalizationService.Get("Common_NotReported")
                        : processorName;
                    if (int.TryParse(obj["NumberOfCores"]?.ToString(), out int cores))
                    {
                        info.ProcessorCores = LocalizationService.Format("Main_CpuProcessor", cores);
                    }
                    break;
                }
            }
            catch
            {
                info.ProcessorName = LocalizationService.Get("Common_NotReported");
                info.ProcessorCores = string.Empty;
            }

            if (string.IsNullOrEmpty(info.ProcessorCores))
            {
                info.ProcessorCores = LocalizationService.Format("Main_CpuProcessor", Environment.ProcessorCount);
            }

            // 2. Memory
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                using var results = searcher.Get();
                foreach (ManagementObject obj in results)
                {
                    using var row = obj;
                    if (ulong.TryParse(obj["TotalPhysicalMemory"]?.ToString(), out ulong bytes))
                    {
                        double gb = bytes / (1024.0 * 1024.0 * 1024.0);
                        info.Memory = $"{gb:0.00} GB";
                    }
                    break;
                }
            }
            catch
            {
                info.Memory = LocalizationService.Get("Common_NotReported");
            }

            // 3. Graphics card
            try
            {
                var gpuNames = new List<string>();
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                using var results = searcher.Get();
                foreach (ManagementObject obj in results)
                {
                    using var row = obj;
                    string name = obj["Name"]?.ToString()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(name))
                    {
                        gpuNames.Add(name);
                    }
                }

                info.GraphicsCard = SelectGraphicsCardSummary(
                    gpuNames, HardwareMonitorService.Instance.CurrentSnapshot.GpuName);
            }
            catch
            {
                info.GraphicsCard = SelectGraphicsCardSummary(
                    Array.Empty<string>(), HardwareMonitorService.Instance.CurrentSnapshot.GpuName);
            }

            // 4. Storage devices
            var telemetryDrives = HardwareMonitorService.Instance.CurrentSnapshot.StorageDrives;
            try
            {
                var drives = new List<StorageInfoItem>();
                var driveInfos = DriveInfo.GetDrives();
                var consumedTelemetry = new HashSet<DriveTelemetry>();
                using var driveSearcher = new ManagementObjectSearcher("SELECT Model, Size, Index, DeviceID, SerialNumber FROM Win32_DiskDrive");
                using var driveResults = driveSearcher.Get();
                foreach (ManagementObject drive in driveResults)
                {
                    using var driveRow = drive;
                    string rawModel = drive["Model"]?.ToString()?.Trim() ?? "";
                    string rawSerial = drive["SerialNumber"]?.ToString()?.Trim() ?? "";
                    int? diskIndex = int.TryParse(
                        drive["Index"]?.ToString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int parsedIndex)
                        ? parsedIndex
                        : null;
                    ulong sizeBytes = 0;
                    if (ulong.TryParse(drive["Size"]?.ToString(), out ulong sz)) sizeBytes = sz;
                    double sizeGb = Math.Round(sizeBytes / (1024.0 * 1024.0 * 1024.0), 2);

                    DriveTelemetry? driveTelemetry = MatchDriveTelemetry(
                        rawModel,
                        rawSerial,
                        diskIndex,
                        sizeGb,
                        telemetryDrives,
                        consumedTelemetry);

                    string type = driveTelemetry?.IsSolidState switch
                    {
                        true => "SSD",
                        false => "HDD",
                        _ => rawModel.ToUpperInvariant().Contains("NVME") || rawModel.ToUpperInvariant().Contains("SSD")
                            ? "SSD"
                            : "HDD"
                    };

                    int? health = driveTelemetry?.SmartHealthPercent;
                    string healthText = health.HasValue
                        ? LocalizationService.Format(
                            "System_HealthFormat",
                            health.Value,
                            LocalizationService.Health(driveTelemetry?.StatusText))
                        : LocalizationService.Get("Common_Unknown");
                    string letters = "";
                    var lettersList = new List<string>();

                    try
                    {
                        string devId = EscapeWqlValue(drive["DeviceID"]?.ToString());
                        string q = $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{devId}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition";
                        using var partSearcher = new ManagementObjectSearcher(q);
                        using var partResults = partSearcher.Get();

                        foreach (ManagementObject part in partResults)
                        {
                            using var partRow = part;
                            string partDevId = EscapeWqlValue(part["DeviceID"]?.ToString());
                            string qLogical = $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partDevId}'}} WHERE AssocClass = Win32_LogicalDiskToPartition";
                            using var logSearcher = new ManagementObjectSearcher(qLogical);
                            using var logResults = logSearcher.Get();
                            foreach (ManagementObject log in logResults)
                            {
                                using var logRow = log;
                                string letter = log["DeviceID"]?.ToString()?.TrimEnd(':') ?? "";
                                if (!string.IsNullOrEmpty(letter) && !lettersList.Contains(letter))
                                {
                                    lettersList.Add(letter);
                                }
                            }
                        }

                        if (lettersList.Count > 0)
                        {
                            letters = string.Join(", ", lettersList);
                        }
                    }
                    catch
                    {
                        // Fallback handled below using telemetry metadata when available.
                    }

                    if (string.IsNullOrEmpty(letters) &&
                        !string.IsNullOrWhiteSpace(driveTelemetry?.DriveLetters))
                    {
                        letters = driveTelemetry.DriveLetters;
                        lettersList.AddRange(letters.Split(',')
                            .Select(letter => letter.Trim().TrimEnd(':'))
                            .Where(letter => letter.Length > 0));
                    }

                    if (string.IsNullOrEmpty(letters))
                    {
                        letters = LocalizationService.Get("Common_NotReported");
                    }

                    double freeGb = 0;
                    bool hasFreeSpace = false;
                    foreach (string letter in lettersList)
                    {
                        try
                        {
                            var logicalDrive = driveInfos.FirstOrDefault(info =>
                                info.Name.StartsWith(letter + ":", StringComparison.OrdinalIgnoreCase));
                            if (logicalDrive != null && logicalDrive.IsReady)
                            {
                                freeGb += logicalDrive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                                hasFreeSpace = true;
                            }
                        }
                        catch
                        {
                            // Ignore a drive that disappears during the query.
                        }
                    }

                    drives.Add(new StorageInfoItem
                    {
                        Header = LocalizationService.Get("System_StorageDevice"),
                        Title = LocalizationService.Format("System_StorageTitle", type, healthText),
                        Subtitle = LocalizationService.Format("System_StorageSubtitle", letters, $"{sizeGb:0.00}"),
                        FreeSpace = hasFreeSpace
                            ? LocalizationService.Format("System_FreeSpace", $"{freeGb:0.00}")
                            : ""
                    });
                }

                if (drives.Count > 0)
                {
                    info.StorageDevices = drives;
                }
            }
            catch
            {
                // Fallback
            }

            if (info.StorageDevices.Count == 0 && telemetryDrives.Count > 0)
            {
                info.StorageDevices = telemetryDrives.Select((drive, index) =>
                {
                    string type = drive.IsSolidState == false ? "HDD" : "SSD";
                    string letters = !string.IsNullOrWhiteSpace(drive.DriveLetters)
                        ? drive.DriveLetters
                        : LocalizationService.Format("System_DriveFallback", index + 1);
                    return new StorageInfoItem
                    {
                        Header = LocalizationService.Get("System_StorageDevice"),
                        Title = drive.SmartHealthPercent.HasValue
                            ? LocalizationService.Format(
                                "System_StorageTitle",
                                type,
                                LocalizationService.Format(
                                    "System_HealthFormat",
                                    drive.SmartHealthPercent.Value,
                                    LocalizationService.Health(drive.StatusText)))
                            : LocalizationService.Format(
                                "System_StorageTitle",
                                type,
                                LocalizationService.Get("Common_Unknown")),
                        Subtitle = LocalizationService.Format(
                            "System_StorageSubtitle",
                            letters,
                            $"{drive.CapacityGb:0.00}")
                    };
                }).ToList();
            }

            // 5. Operating System
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Caption, OSArchitecture FROM Win32_OperatingSystem");
                using var results = searcher.Get();
                foreach (ManagementObject obj in results)
                {
                    using var row = obj;
                    string? caption = obj["Caption"]?.ToString()?.Replace("Microsoft", "")?.Trim();
                    string arch = FormatOsArchitecture(obj["OSArchitecture"]?.ToString());
                    info.OperatingSystem = string.IsNullOrWhiteSpace(caption)
                        ? LocalizationService.Get("Common_NotReported")
                        : $"{caption} {arch}".Trim();
                    break;
                }
            }
            catch
            {
                info.OperatingSystem = LocalizationService.Get("Common_NotReported");
            }

            // 6. Windows Uptime
            info.WindowsUptime = FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64));

            // 7. Startup Speed
            info.StartupSpeed = QueryBootTime();

            // 8. Temp Stats
            var (min, avg, max) = HardwareMonitorService.Instance.RingBuffer.GetCpuStats(coolingOnly: true);
            if (min > 0 && max > 0)
            {
                bool isFahrenheit = SettingsService.Instance.Current.TemperatureUnit == "Fahrenheit";
                string formatKey = isFahrenheit ? "System_TempStatsFormatF" : "System_TempStatsFormat";
                info.TempStats = LocalizationService.Format(
                    formatKey,
                    Math.Round(isFahrenheit ? (min * 9 / 5) + 32 : min),
                    Math.Round(isFahrenheit ? (avg * 9 / 5) + 32 : avg),
                    Math.Round(isFahrenheit ? (max * 9 / 5) + 32 : max));
            }
            else if (HardwareMonitorService.Instance.RingBuffer.GetCpuStats(coolingOnly: false).MinCpu > 0)
            {
                info.TempStats = LocalizationService.Get("System_TempStatsNoCooling");
            }
            else
            {
                info.TempStats = LocalizationService.Get("Common_NotReported");
            }

            // 9. Monitors
            info.Monitors = QueryMonitorNames();

            // 10. Network
            info.Network = QueryActiveNetwork();

            // 11. System Errors (BSOD Count)
            info.SystemErrors = QueryBsodCount();

            info.FanSpeeds = SettingsService.Instance.Current.ShowFanSpeeds
                ? FormatFanSpeeds(HardwareMonitorService.Instance.CurrentSnapshot.Fans)
                : "";

            return info;
        }

        public static string FormatUptime(TimeSpan uptime)
        {
            return LocalizationService.Format(
                "System_UptimeFormat",
                uptime.Days,
                uptime.Hours,
                uptime.Minutes,
                uptime.Seconds);
        }

        public static DriveTelemetry? MatchDriveTelemetry(
            string? wmiModel,
            string? wmiSerial,
            int? wmiDiskIndex,
            double wmiSizeGb,
            IReadOnlyList<DriveTelemetry> candidates,
            ISet<DriveTelemetry> consumed)
        {
            if (candidates.Count == 0)
            {
                return null;
            }

            var available = candidates.Where(candidate => !consumed.Contains(candidate)).ToList();
            if (available.Count == 0)
            {
                return null;
            }

            DriveTelemetry? matched = null;
            string normSerial = NormalizeAlphaNumeric(wmiSerial);
            if (!string.IsNullOrEmpty(normSerial))
            {
                matched = available.FirstOrDefault(candidate =>
                {
                    string candSerial = NormalizeAlphaNumeric(candidate.SerialNumber);
                    return !string.IsNullOrEmpty(candSerial) &&
                        string.Equals(candSerial, normSerial, StringComparison.Ordinal);
                });

                if (matched == null && normSerial.Length >= 6)
                {
                    matched = available.FirstOrDefault(candidate =>
                    {
                        string candSerial = NormalizeAlphaNumeric(candidate.SerialNumber);
                        return candSerial.Length >= 6 &&
                            (normSerial.Contains(candSerial, StringComparison.Ordinal) ||
                             candSerial.Contains(normSerial, StringComparison.Ordinal));
                    });
                }
            }

            if (matched == null && wmiDiskIndex.HasValue)
            {
                matched = available.FirstOrDefault(candidate =>
                    candidate.PhysicalDriveNumber == wmiDiskIndex.Value);
            }

            string normModel = NormalizeAlphaNumeric(wmiModel);
            if (matched == null && !string.IsNullOrEmpty(normModel))
            {
                var exactModelMatches = available
                    .Where(candidate => string.Equals(
                        NormalizeAlphaNumeric(candidate.Model),
                        normModel,
                        StringComparison.Ordinal))
                    .ToList();
                if (exactModelMatches.Count > 0)
                {
                    matched = SelectClosestByCapacity(exactModelMatches, wmiSizeGb);
                }

                if (matched == null && normModel.Length >= 4)
                {
                    var partialMatches = available
                        .Where(candidate =>
                        {
                            string candModel = NormalizeAlphaNumeric(candidate.Model);
                            return candModel.Length >= 4 &&
                                (normModel.Contains(candModel, StringComparison.Ordinal) ||
                                 candModel.Contains(normModel, StringComparison.Ordinal));
                        })
                        .ToList();
                    if (partialMatches.Count > 0)
                    {
                        matched = SelectClosestByCapacity(partialMatches, wmiSizeGb);
                    }
                }
            }

            if (matched != null)
            {
                consumed.Add(matched);
            }

            return matched;
        }

        private static DriveTelemetry SelectClosestByCapacity(IReadOnlyList<DriveTelemetry> matches, double targetGb)
        {
            if (matches.Count == 1 || targetGb <= 0)
            {
                return matches[0];
            }

            return matches
                .OrderBy(candidate => candidate.CapacityGb > 0
                    ? Math.Abs(candidate.CapacityGb - targetGb)
                    : double.MaxValue)
                .First();
        }

        private static string NormalizeAlphaNumeric(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var sb = new StringBuilder(value.Length);
            foreach (char ch in value)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToUpperInvariant(ch));
                }
            }

            return sb.ToString();
        }

        private string QueryBootTime()
        {
            try
            {
                string query = "*[System[(EventID=100)]]";
                var eventQuery = new EventLogQuery(
                    "Microsoft-Windows-Diagnostics-Performance/Operational",
                    PathType.LogName, query)
                {
                    ReverseDirection = true
                };

                using var reader = new EventLogReader(eventQuery);
                using EventRecord? record = reader.ReadEvent();
                if (record == null)
                {
                    return LocalizationService.Get("Common_NotReported");
                }

                // Event 100 stores the total boot duration in milliseconds.
                var document = XDocument.Parse(record.ToXml());
                XElement? bootTimeData = document.Descendants()
                    .FirstOrDefault(element =>
                        element.Name.LocalName == "Data" &&
                        string.Equals((string?)element.Attribute("Name"), "BootTime", StringComparison.Ordinal));
                if (bootTimeData == null ||
                    !double.TryParse(bootTimeData.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double milliseconds))
                {
                    return LocalizationService.Get("Common_NotReported");
                }

                return LocalizationService.Format("System_SecondsFormat", $"{milliseconds / 1000.0:0.0}");
            }
            catch
            {
                return LocalizationService.Get("Common_NotReported");
            }
        }

        internal static string SelectGraphicsCardSummary(IEnumerable<string?> wmiNames, string? primaryGpuName)
        {
            var distinct = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? raw in wmiNames)
            {
                string trimmed = raw?.Trim() ?? "";
                if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
                {
                    distinct.Add(trimmed);
                }
            }

            var physical = distinct.Where(n => !IsVirtualDisplayAdapter(n)).ToList();
            List<string> candidates = physical.Count > 0 ? physical : distinct;

            string primary = primaryGpuName?.Trim() ?? "";
            if (!string.IsNullOrEmpty(primary))
            {
                if (candidates.Count == 0)
                {
                    return primary;
                }

                int matchIndex = candidates.FindIndex(c =>
                    string.Equals(c, primary, StringComparison.OrdinalIgnoreCase) ||
                    c.Contains(primary, StringComparison.OrdinalIgnoreCase) ||
                    primary.Contains(c, StringComparison.OrdinalIgnoreCase));
                if (matchIndex > 0)
                {
                    string matched = candidates[matchIndex];
                    candidates.RemoveAt(matchIndex);
                    candidates.Insert(0, matched);
                }
            }

            return candidates.Count > 0
                ? string.Join(", ", candidates)
                : LocalizationService.Get("Common_NotReported");
        }

        private static bool IsVirtualDisplayAdapter(string name)
        {
            return name.Contains("Microsoft Basic Display", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Microsoft Remote Display", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Microsoft Hyper-V Video", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Parsec Virtual", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Citrix Indirect", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("spacedesk", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("LuminonCore", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Virtual Desktop Monitor", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Indirect Display", StringComparison.OrdinalIgnoreCase);
        }

        private string QueryMonitorNames()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT UserFriendlyName FROM WmiMonitorID");
                using var results = searcher.Get();
                var names = new List<string>();
                foreach (ManagementObject obj in results)
                {
                    using var row = obj;
                    if (obj["UserFriendlyName"] is ushort[] chars)
                    {
                        var sb = new StringBuilder();
                        foreach (ushort c in chars)
                        {
                            if (c == 0) break;
                            sb.Append((char)c);
                        }
                        string name = sb.ToString().Trim();
                        if (!string.IsNullOrEmpty(name))
                        {
                            names.Add(name);
                        }
                    }
                }

                if (names.Count > 0)
                {
                    return string.Join(", ", names);
                }
            }
            catch
            {
                // Ignore
            }

            return LocalizationService.Get("Common_NotReported");
        }

        internal static string EscapeWqlValue(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            return value.Replace("\\", "\\\\").Replace("'", "\\'");
        }

        internal static string FormatOsArchitecture(string? rawArchitecture)
        {
            if (string.IsNullOrWhiteSpace(rawArchitecture))
            {
                return Environment.Is64BitOperatingSystem ? "x64" : "x86";
            }

            string lower = rawArchitecture.ToLowerInvariant();
            if (lower.Contains("arm"))
            {
                return lower.Contains("64") ? "ARM64" : "ARM";
            }

            return lower.Contains("64") ? "x64" : "x86";
        }

        private string QueryActiveNetwork()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT NetConnectionID, Name, NetConnectionStatus FROM Win32_NetworkAdapter WHERE NetConnectionStatus = 2");
                using var results = searcher.Get();
                string? virtualFallback = null;
                foreach (ManagementObject obj in results)
                {
                    using var row = obj;
                    string id = obj["NetConnectionID"]?.ToString()?.Trim() ?? "Ethernet";
                    string name = obj["Name"]?.ToString()?.Trim() ?? "";
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    // Ignore virtual/tunnel adapters if physical is available
                    if (name.Contains("WARP", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("VPN", StringComparison.OrdinalIgnoreCase))
                    {
                        virtualFallback ??= LocalizationService.Format("System_NetworkFormat", id, name);
                        continue;
                    }

                    return LocalizationService.Format("System_NetworkFormat", id, name);
                }

                if (!string.IsNullOrEmpty(virtualFallback))
                {
                    return virtualFallback;
                }
            }
            catch
            {
                // Ignore
            }

            return LocalizationService.Get("Common_NotReported");
        }

        private string QueryBsodCount()
        {
            const int MaxEvents = 10000;
            int count = 0;
            try
            {
                string query = "*[System[(EventID=1001) and (Provider[@Name='BugCheck' or @Name='Microsoft-Windows-WER-SystemErrorReporting'])]]";
                var eventQuery = new EventLogQuery("System", PathType.LogName, query);
                using var reader = new EventLogReader(eventQuery);
                while (count < MaxEvents)
                {
                    using var record = reader.ReadEvent();
                    if (record == null)
                    {
                        break;
                    }

                    count++;
                }
            }
            catch
            {
                return LocalizationService.Get("Common_NotReported");
            }

            return LocalizationService.Format("System_BsodCount", count);
        }

        internal static string FormatFanSpeeds(IReadOnlyList<FanTelemetry> fans)
        {
            if (fans.Count == 0)
            {
                return "--";
            }

            var sb = new StringBuilder();
            foreach (var fan in fans)
            {
                if (string.IsNullOrWhiteSpace(fan.Name) || fan.Rpm <= 0)
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }

                string fanName = FanHealthService.GetDisplayName(
                    $"fan:{fan.Name}", fan.Name, false);
                sb.Append(LocalizationService.Format(
                    "System_FanLine",
                    fanName,
                    fan.Rpm,
                    LocalizationService.Get("Main_RpmUnit")));
            }

            return sb.Length > 0 ? sb.ToString() : "--";
        }

        public string GenerateDiagnosticsSnapshot(
            SystemInfoData data,
            HardwareSnapshot snapshot,
            IReadOnlyList<FanHealthStatus>? fanStatuses,
            bool isFahrenheit)
        {
            var sb = new StringBuilder();
            string unit = isFahrenheit ? "F" : "C";

            string FormatTemp(double celsius)
            {
                if (!double.IsFinite(celsius) || celsius <= 0)
                {
                    return "--";
                }

                double value = isFahrenheit ? (celsius * 9 / 5) + 32 : celsius;
                return $"{Math.Round(value).ToString(CultureInfo.InvariantCulture)}°{unit}";
            }

            string FormatPercent(double percent)
            {
                return double.IsFinite(percent) && percent >= 0
                    ? $"{Math.Round(percent).ToString(CultureInfo.InvariantCulture)}%"
                    : "--";
            }

            string FormatWatts(int? watts)
            {
                return watts.HasValue
                    ? $"{watts.Value.ToString(CultureInfo.InvariantCulture)}W"
                    : "--";
            }

            sb.AppendLine($"CryoTray {UpdateService.CurrentVersion}");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"OS: {data.OperatingSystem}");
            sb.AppendLine($"CPU: {(string.IsNullOrWhiteSpace(data.ProcessorName) ? snapshot.CpuName : data.ProcessorName)}");
            sb.AppendLine(
                $"  Temp: {FormatTemp(snapshot.CpuTemp)} | Load: {FormatPercent(snapshot.CpuLoad)} | Power: {FormatWatts(snapshot.CpuPackageWatts)}");
            sb.AppendLine($"GPU: {(string.IsNullOrWhiteSpace(data.GraphicsCard) ? snapshot.GpuName : data.GraphicsCard)}");
            var gpuMetrics = new List<string>
            {
                $"Temp: {FormatTemp(snapshot.GpuTemp)}",
                $"Load: {FormatPercent(snapshot.GpuLoad)}",
                $"Power: {FormatWatts(snapshot.GpuPowerWatts)}",
                $"Fan: {(snapshot.GpuFanRpm.HasValue ? $"{snapshot.GpuFanRpm.Value} RPM" : "--")}"
            };
            if (snapshot.GpuHotspotTemp.HasValue && double.IsFinite(snapshot.GpuHotspotTemp.Value) && snapshot.GpuHotspotTemp.Value > 0)
            {
                gpuMetrics.Add($"Hotspot: {FormatTemp(snapshot.GpuHotspotTemp.Value)}");
            }
            if (snapshot.GpuVramTemp.HasValue && double.IsFinite(snapshot.GpuVramTemp.Value) && snapshot.GpuVramTemp.Value > 0)
            {
                gpuMetrics.Add($"VRAM Temp: {FormatTemp(snapshot.GpuVramTemp.Value)}");
            }
            if (snapshot.GpuCoreClockMhz.HasValue && double.IsFinite(snapshot.GpuCoreClockMhz.Value) && snapshot.GpuCoreClockMhz.Value > 0)
            {
                gpuMetrics.Add($"Clock: {Math.Round(snapshot.GpuCoreClockMhz.Value).ToString(CultureInfo.InvariantCulture)} MHz");
            }
            if (snapshot.GpuVramUsedMb.HasValue && snapshot.GpuVramTotalMb.HasValue && snapshot.GpuVramTotalMb.Value > 0)
            {
                gpuMetrics.Add(
                    $"VRAM: {Math.Round(snapshot.GpuVramUsedMb.Value).ToString(CultureInfo.InvariantCulture)} / {Math.Round(snapshot.GpuVramTotalMb.Value).ToString(CultureInfo.InvariantCulture)} MB");
            }
            sb.AppendLine($"  {string.Join(" | ", gpuMetrics)}");
            string ramUsed = double.IsFinite(snapshot.RamUsedGb) && snapshot.RamUsedGb > 0
                ? snapshot.RamUsedGb.ToString("0.0", CultureInfo.InvariantCulture)
                : "--";
            string ramTotal = double.IsFinite(snapshot.RamTotalGb) && snapshot.RamTotalGb > 0
                ? snapshot.RamTotalGb.ToString("0.0", CultureInfo.InvariantCulture)
                : "--";
            sb.AppendLine($"RAM: {ramUsed} / {ramTotal} GB ({FormatPercent(snapshot.RamUsagePercent)})");

            sb.AppendLine("Fans:");
            if (fanStatuses == null || fanStatuses.Count == 0)
            {
                sb.AppendLine("  --");
            }
            else
            {
                foreach (var fan in fanStatuses)
                {
                    string fanName = FanHealthService.GetDisplayName(fan);
                    string state = fan.State switch
                    {
                        FanHealthState.Armed => "Armed",
                        FanHealthState.IdleAtZero => "Idle at 0 RPM",
                        FanHealthState.Stopped => "Stopped",
                        _ => "Watching"
                    };
                    sb.AppendLine($"  {fanName}: {fan.Rpm} RPM, {state}");
                }
            }

            sb.AppendLine("Drives:");
            if (snapshot.StorageDrives.Count == 0)
            {
                sb.AppendLine("  --");
            }
            else
            {
                foreach (var drive in snapshot.StorageDrives)
                {
                    string driveName = string.IsNullOrWhiteSpace(drive.DriveLetters)
                        ? $"Drive {drive.Index + 1}"
                        : drive.DriveLetters.Trim();
                    string health = drive.SmartHealthPercent.HasValue
                        ? $"{drive.SmartHealthPercent.Value}%"
                        : "--";
                    string status = string.IsNullOrWhiteSpace(drive.StatusText)
                        ? "Unknown"
                        : drive.StatusText.Trim();
                    string separator = driveName.EndsWith(":", StringComparison.Ordinal) ? " " : ": ";
                    sb.AppendLine($"  {driveName}{separator}{health} ({status})");
                }
            }

            return sb.ToString().TrimEnd();
        }

        public string GenerateTextReport(SystemInfoData data)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=================================================");
            sb.AppendLine(LocalizationService.Get("System_ReportTitle"));
            sb.AppendLine(LocalizationService.Format(
                "System_ReportGenerated",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            sb.AppendLine("=================================================");
            sb.AppendLine();
            sb.AppendLine(LocalizationService.Format("System_ReportLine", LocalizationService.Get("System_Pc"), data.PcName));
            sb.AppendLine(LocalizationService.Format(
                "System_ReportLine",
                LocalizationService.Get("System_Processor"),
                $"{data.ProcessorName} ({data.ProcessorCores})"));
            sb.AppendLine(LocalizationService.Format("System_ReportLine", LocalizationService.Get("System_Memory"), data.Memory));
            sb.AppendLine(LocalizationService.Format("System_ReportLine", LocalizationService.Get("System_GraphicsCard"), data.GraphicsCard));
            sb.AppendLine();
            sb.AppendLine(LocalizationService.Get("System_ReportStorage"));
            foreach (var d in data.StorageDevices)
            {
                string free = string.IsNullOrWhiteSpace(d.FreeSpace) ? "" : $" ({d.FreeSpace})";
                sb.AppendLine($"  - {LocalizationService.Format("System_ReportLine", d.Title, d.Subtitle)}{free}");
            }
            sb.AppendLine();
            sb.AppendLine(LocalizationService.Format("System_ReportLine", LocalizationService.Get("System_OperatingSystem"), data.OperatingSystem));
            sb.AppendLine(LocalizationService.Format("System_ReportLine", LocalizationService.Get("System_Uptime"), data.WindowsUptime));
            sb.AppendLine(LocalizationService.Format("System_ReportLine", LocalizationService.Get("System_StartupSpeed"), data.StartupSpeed));
            sb.AppendLine(LocalizationService.Format("System_ReportLine", LocalizationService.Get("System_ReportTempStats"), data.TempStats));
            if (!string.IsNullOrEmpty(data.FanSpeeds))
            {
                sb.AppendLine(LocalizationService.Format(
                    "System_ReportLine",
                    LocalizationService.Get("System_FanSpeeds"),
                    data.FanSpeeds));
            }
            sb.AppendLine(LocalizationService.Format("System_ReportLine", LocalizationService.Get("System_Monitors"), data.Monitors));
            sb.AppendLine(LocalizationService.Format("System_ReportLine", LocalizationService.Get("System_Network"), data.Network));
            sb.AppendLine(LocalizationService.Format("System_ReportLine", LocalizationService.Get("System_ReportSystemErrors"), data.SystemErrors));
            sb.AppendLine();
            sb.AppendLine("=================================================");
            return sb.ToString();
        }
    }
}
