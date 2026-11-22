using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CryoTray.Models;

namespace CryoTray.Services
{
    /// <summary>Exports telemetry history or streams 1 Hz CSV rows for thermal benchmarking.</summary>
    public sealed class TelemetryCsvService : IDisposable
    {
        private static TelemetryCsvService? _instance;
        public static TelemetryCsvService Instance => _instance ??= new TelemetryCsvService();

        private const string CsvHeader =
            "Timestamp,CoolingMode,CpuTempC,CpuClockGhz,CpuLoadPct,CpuPowerW,GpuTempC,GpuLoadPct,GpuPowerW,GpuHotspotC,GpuVramTempC,GpuFanRpm,RamUsedGb,RamLoadPct";

        private readonly object _lock = new();
        private StreamWriter? _writer;
        private string _currentFilePath = "";
        private bool _disposed;

        public event Action<bool>? RecordingStateChanged;
        public event Action<string>? RecordingError;

        public bool IsRecording
        {
            get
            {
                lock (_lock)
                {
                    return _writer != null;
                }
            }
        }

        public string CurrentFilePath
        {
            get
            {
                lock (_lock)
                {
                    return _currentFilePath;
                }
            }
        }

        private TelemetryCsvService()
        {
        }

        public static void ExportSamples(IReadOnlyList<TelemetrySample> samples, string filePath)
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var writer = new StreamWriter(filePath, false, new UTF8Encoding(false));
            writer.WriteLine(CsvHeader);
            foreach (var sample in samples)
            {
                writer.WriteLine(FormatSampleRow(sample));
            }
        }

        public void StartRecording(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path must not be empty.", nameof(filePath));
            }

            StopRecording();

            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            lock (_lock)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(TelemetryCsvService));
                }

                var writer = new StreamWriter(filePath, false, new UTF8Encoding(false));
                writer.WriteLine(CsvHeader);
                writer.Flush();
                _writer = writer;
                _currentFilePath = filePath;
                HardwareMonitorService.Instance.TelemetryUpdated += OnTelemetryUpdated;
            }

            RecordingStateChanged?.Invoke(true);
        }

        public void StopRecording()
        {
            bool stopped = false;
            lock (_lock)
            {
                if (_writer != null)
                {
                    HardwareMonitorService.Instance.TelemetryUpdated -= OnTelemetryUpdated;
                    try
                    {
                        _writer.Flush();
                        _writer.Dispose();
                    }
                    catch
                    {
                    }
                    _writer = null;
                    _currentFilePath = "";
                    stopped = true;
                }
            }

            if (stopped)
            {
                RecordingStateChanged?.Invoke(false);
            }
        }

        private void OnTelemetryUpdated(HardwareSnapshot snapshot)
        {
            string? failureMessage = null;
            lock (_lock)
            {
                if (_writer == null || _disposed)
                {
                    return;
                }

                try
                {
                    _writer.WriteLine(FormatSampleRow(TelemetrySample.FromSnapshot(snapshot)));
                    _writer.Flush();
                }
                catch (Exception ex)
                {
                    failureMessage = ex.Message;
                    HardwareMonitorService.Instance.TelemetryUpdated -= OnTelemetryUpdated;
                    try
                    {
                        _writer.Dispose();
                    }
                    catch
                    {
                    }
                    _writer = null;
                    _currentFilePath = "";
                }
            }

            if (failureMessage != null)
            {
                RecordingStateChanged?.Invoke(false);
                RecordingError?.Invoke(failureMessage);
            }
        }

        public static string FormatSampleRow(TelemetrySample sample)
        {
            return string.Join(",",
                sample.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                sample.CoolingModeActive ? "1" : "0",
                FormatPositiveDouble(sample.CpuTemp, "0.0"),
                FormatPositiveDouble(sample.CpuGhz, "0.00"),
                FormatNonNegativeDouble(sample.CpuLoad, "0.0"),
                sample.CpuPowerWatts.HasValue ? sample.CpuPowerWatts.Value.ToString(CultureInfo.InvariantCulture) : "",
                FormatPositiveDouble(sample.GpuTemp, "0.0"),
                FormatNonNegativeDouble(sample.GpuLoad, "0.0"),
                sample.GpuPowerWatts.HasValue ? sample.GpuPowerWatts.Value.ToString(CultureInfo.InvariantCulture) : "",
                sample.GpuHotspotTemp.HasValue ? FormatPositiveDouble(sample.GpuHotspotTemp.Value, "0.0") : "",
                sample.GpuVramTemp.HasValue ? FormatPositiveDouble(sample.GpuVramTemp.Value, "0.0") : "",
                sample.GpuFanRpm.HasValue && sample.GpuFanRpm.Value >= 0
                    ? sample.GpuFanRpm.Value.ToString(CultureInfo.InvariantCulture)
                    : "",
                FormatPositiveDouble(sample.RamUsedGb, "0.00"),
                FormatNonNegativeDouble(sample.RamUsagePercent, "0.0"));
        }

        private static string FormatPositiveDouble(double value, string format)
        {
            return double.IsFinite(value) && value > 0
                ? value.ToString(format, CultureInfo.InvariantCulture)
                : "";
        }

        private static string FormatNonNegativeDouble(double value, string format)
        {
            return double.IsFinite(value) && value >= 0
                ? value.ToString(format, CultureInfo.InvariantCulture)
                : "";
        }

        public void Dispose()
        {
            StopRecording();
            lock (_lock)
            {
                _disposed = true;
            }
        }
    }
}
