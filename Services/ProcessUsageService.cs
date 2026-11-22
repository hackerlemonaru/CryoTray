using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Threading.Tasks;

namespace CryoTray.Services
{
    public sealed class ProcessUsageEntry
    {
        public string Name { get; init; } = "";
        public int ProcessId { get; init; }
        public double CpuPercent { get; init; }
        public double GpuPercent { get; init; }
        public double MemoryMb { get; init; }
    }

    public sealed class ProcessUsageSnapshot
    {
        public IReadOnlyList<ProcessUsageEntry> Entries { get; init; } = Array.Empty<ProcessUsageEntry>();
        public bool CpuMeasured { get; init; }
        public bool GpuAvailable { get; init; }
    }

    public static class ProcessUsageService
    {
        private const int MaxEntries = 6;
        private const string GpuEngineQuery =
            "SELECT Name, UtilizationPercentage " +
            "FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine";

        private static readonly object Gate = new();
        private static readonly Dictionary<int, CpuBaseline> Baselines = new();
        private static DateTime _lastSampleUtc = DateTime.MinValue;

        public static Task<ProcessUsageSnapshot> CaptureAsync()
        {
            return Task.Run(Capture);
        }

        private static ProcessUsageSnapshot Capture()
        {
            DateTime nowUtc = DateTime.UtcNow;
            double elapsedMs;
            lock (Gate)
            {
                elapsedMs = _lastSampleUtc == DateTime.MinValue
                    ? 0
                    : (nowUtc - _lastSampleUtc).TotalMilliseconds;
                if (elapsedMs < 200 || elapsedMs > 15000)
                {
                    elapsedMs = 0;
                }
            }

            bool cpuMeasured = elapsedMs > 0;
            var current = new Dictionary<int, CpuBaseline>();
            var cpuByPid = new Dictionary<int, (string Name, double Percent, double MemoryMb)>();

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    int pid = process.Id;
                    long startTicks = 0;
                    try
                    {
                        startTicks = process.StartTime.Ticks;
                    }
                    catch
                    {
                    }

                    TimeSpan total = process.TotalProcessorTime;
                    double memoryMb = process.WorkingSet64 / (1024.0 * 1024.0);
                    string name = process.ProcessName;
                    current[pid] = new CpuBaseline
                    {
                        TotalProcessorTime = total,
                        StartTimeTicks = startTicks
                    };

                    double percent = 0;
                    if (cpuMeasured)
                    {
                        lock (Gate)
                        {
                            if (Baselines.TryGetValue(pid, out var baseline) &&
                                baseline.StartTimeTicks == startTicks)
                            {
                                double deltaMs = (total - baseline.TotalProcessorTime).TotalMilliseconds;
                                percent = deltaMs / (elapsedMs * Environment.ProcessorCount) * 100.0;
                            }
                        }

                        percent = Math.Clamp(percent, 0, 100);
                    }

                    cpuByPid[pid] = (name, percent, memoryMb);
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            lock (Gate)
            {
                Baselines.Clear();
                foreach (var pair in current)
                {
                    Baselines[pair.Key] = pair.Value;
                }

                _lastSampleUtc = nowUtc;
            }

            var gpuByPid = new Dictionary<int, double>();
            bool gpuAvailable = TryReadGpuUsage(gpuByPid);

            var processIds = new HashSet<int>(cpuByPid.Keys);
            processIds.UnionWith(gpuByPid.Keys);

            var entries = new List<ProcessUsageEntry>(processIds.Count);
            foreach (int pid in processIds)
            {
                cpuByPid.TryGetValue(pid, out var cpu);
                gpuByPid.TryGetValue(pid, out double gpu);
                string name = cpu.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        name = process.ProcessName;
                    }
                    catch
                    {
                        name = $"PID {pid}";
                    }
                }

                entries.Add(new ProcessUsageEntry
                {
                    ProcessId = pid,
                    Name = name,
                    CpuPercent = cpu.Percent,
                    GpuPercent = Math.Clamp(gpu, 0, 100),
                    MemoryMb = cpu.MemoryMb
                });
            }

            entries.Sort((left, right) =>
                Math.Max(right.CpuPercent, right.GpuPercent)
                    .CompareTo(Math.Max(left.CpuPercent, left.GpuPercent)));
            if (entries.Count > MaxEntries)
            {
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
            }

            return new ProcessUsageSnapshot
            {
                Entries = entries,
                CpuMeasured = cpuMeasured,
                GpuAvailable = gpuAvailable
            };
        }

        private static bool TryReadGpuUsage(Dictionary<int, double> usage)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(GpuEngineQuery);
                foreach (ManagementObject item in searcher.Get())
                {
                    using (item)
                    {
                        string name = item["Name"] as string ?? "";
                        if (!name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        int pid = ParseProcessId(name);
                        if (pid <= 0)
                        {
                            continue;
                        }

                        double percent;
                        try
                        {
                            percent = Convert.ToDouble(item["UtilizationPercentage"]);
                        }
                        catch
                        {
                            continue;
                        }

                        if (!double.IsFinite(percent) || percent <= 0)
                        {
                            continue;
                        }

                        usage[pid] = usage.TryGetValue(pid, out double existing)
                            ? existing + percent
                            : percent;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int ParseProcessId(string instanceName)
        {
            const string marker = "pid_";
            int index = instanceName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return 0;
            }

            index += marker.Length;
            int end = index;
            while (end < instanceName.Length && char.IsDigit(instanceName[end]))
            {
                end++;
            }

            return int.TryParse(instanceName[index..end], out int pid) ? pid : 0;
        }

        private sealed class CpuBaseline
        {
            public TimeSpan TotalProcessorTime { get; init; }
            public long StartTimeTicks { get; init; }
        }
    }
}
