using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace CryoTray.Tests
{
    public static class CpuSensorDump
    {
        public static int Run()
        {
            var report = new StringBuilder();
            report.AppendLine("CryoTray CPU sensor dump");
            report.AppendLine($"Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"Machine: {Environment.MachineName}");
            report.AppendLine($"User: {Environment.UserName}");
            report.AppendLine($"Elevated: {Environment.IsPrivilegedProcess}");
            report.AppendLine();

            Computer? computer = null;
            try
            {
                computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = false,
                    IsStorageEnabled = false,
                    IsMotherboardEnabled = false,
                    IsControllerEnabled = false
                };
                computer.Open();

                int cpuNodes = 0;
                int temperatureSensors = 0;
                int clockSensors = 0;

                foreach (var hardware in computer.Hardware)
                {
                    if (hardware.HardwareType != HardwareType.Cpu)
                    {
                        continue;
                    }

                    cpuNodes++;
                    hardware.Update();

                    report.AppendLine($"Hardware: {hardware.Name}");
                    report.AppendLine($"  Type: {hardware.HardwareType}");
                    report.AppendLine($"  Identifier: {hardware.Identifier}");
                    report.AppendLine();

                    DumpSensors(hardware.Sensors, "  ", report, ref temperatureSensors, ref clockSensors);

                    foreach (var sub in hardware.SubHardware)
                    {
                        sub.Update();
                        report.AppendLine($"  Subhardware: {sub.Name}");
                        report.AppendLine($"    Identifier: {sub.Identifier}");
                        report.AppendLine();
                        DumpSensors(sub.Sensors, "    ", report, ref temperatureSensors, ref clockSensors);
                    }
                }

                if (cpuNodes == 0)
                {
                    report.AppendLine("No CPU hardware nodes found.");
                }

                report.AppendLine($"CPU nodes: {cpuNodes}");
                report.AppendLine($"Temperature sensors: {temperatureSensors}");
                report.AppendLine($"Clock sensors: {clockSensors}");
            }
            catch (Exception ex)
            {
                report.AppendLine("ERROR while reading CPU sensors:");
                report.AppendLine(ex.ToString());
            }
            finally
            {
                try
                {
                    computer?.Close();
                }
                catch
                {
                    // Ignore close failures.
                }
            }

            string text = report.ToString();
            string projectPath = Path.Combine(FindProjectDirectory(), "cpu-sensors.txt");

            try
            {
                File.WriteAllText(projectPath, text);
                return 0;
            }
            catch
            {
                try
                {
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "cpu-sensors.txt"), text);
                    return 0;
                }
                catch
                {
                    return 1;
                }
            }
        }

        private static void DumpSensors(
            IReadOnlyList<ISensor> sensors,
            string indent,
            StringBuilder report,
            ref int temperatureSensors,
            ref int clockSensors)
        {
            foreach (var sensor in sensors)
            {
                if (sensor.SensorType != SensorType.Temperature && sensor.SensorType != SensorType.Clock)
                {
                    continue;
                }

                if (sensor.SensorType == SensorType.Temperature)
                {
                    temperatureSensors++;
                }
                else
                {
                    clockSensors++;
                }

                report.AppendLine($"{indent}Sensor: {sensor.Name}");
                report.AppendLine($"{indent}  Type: {sensor.SensorType}");
                report.AppendLine($"{indent}  Value: {Format(sensor.Value)}");
                report.AppendLine($"{indent}  Min: {Format(sensor.Min)}");
                report.AppendLine($"{indent}  Max: {Format(sensor.Max)}");
                report.AppendLine($"{indent}  Identifier: {sensor.Identifier}");
                report.AppendLine();
            }
        }

        private static string Format(float? value)
        {
            return value.HasValue
                ? value.Value.ToString("0.###", CultureInfo.InvariantCulture)
                : "null";
        }

        private static string FindProjectDirectory()
        {
            string? directory = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(directory))
            {
                if (File.Exists(Path.Combine(directory, "CryoTray.csproj")))
                {
                    return directory;
                }

                directory = Directory.GetParent(directory)?.FullName;
            }

            return AppContext.BaseDirectory;
        }
    }
}
