using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace CryoTray.Services
{
    public sealed record StartupTaskState(bool Exists, string? CommandPath, string? Arguments, bool AllowsBattery);

    public static class StartupTaskService
    {
        public const string TaskName = "CryoTray";
        public const string AutostartArg = "/autostart";

        public static bool IsCurrentProcessElevated()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        internal static string ResolveExecutablePath()
        {
            string baseDirExe = Path.Combine(AppContext.BaseDirectory, "CryoTray.exe");
            if (File.Exists(baseDirExe))
            {
                return Path.GetFullPath(baseDirExe);
            }

            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                string fileName = Path.GetFileNameWithoutExtension(processPath);
                if (!fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(processPath))
                {
                    return Path.GetFullPath(processPath);
                }
            }

            return Path.GetFullPath(baseDirExe);
        }

        internal static string BuildTaskXml(string exePath, string workingDirectory, string userId)
        {
            string escapedExe = SecurityElement.Escape(exePath) ?? exePath;
            string escapedDir = SecurityElement.Escape(workingDirectory) ?? workingDirectory;
            string escapedUser = SecurityElement.Escape(userId) ?? userId;
            string escapedArgs = SecurityElement.Escape(AutostartArg) ?? AutostartArg;

            return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{escapedUser}</UserId>
      <Delay>PT5S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{escapedUser}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{escapedExe}</Command>
      <Arguments>{escapedArgs}</Arguments>
      <WorkingDirectory>{escapedDir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
";
        }

        public static StartupTaskState QueryState() => QueryStateForTask(TaskName);

        internal static StartupTaskState QueryStateForTask(string taskName)
        {
            var (exitCode, stdout, _) = RunSchtasks($"/query /tn \"{taskName}\" /xml");
            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                return new StartupTaskState(false, null, null, false);
            }

            try
            {
                var doc = XDocument.Parse(stdout);
                XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

                string? command = doc.Descendants(ns + "Command").FirstOrDefault()?.Value?.Trim().Trim('"');
                string? arguments = doc.Descendants(ns + "Arguments").FirstOrDefault()?.Value?.Trim();
                string? disallowBattery = doc.Descendants(ns + "DisallowStartIfOnBatteries").FirstOrDefault()?.Value?.Trim();
                bool allowsBattery = string.Equals(disallowBattery, "false", StringComparison.OrdinalIgnoreCase);

                return new StartupTaskState(true, command, arguments, allowsBattery);
            }
            catch
            {
                return new StartupTaskState(true, null, null, false);
            }
        }

        public static bool IsStartupConfigured() => QueryState().Exists;

        public static void SetStartup(bool enable)
        {
            TrySetStartup(enable, out _);
        }

        public static bool TrySetStartup(bool enable, out string? errorMessage)
        {
            return TrySetStartupForTask(TaskName, enable, out errorMessage);
        }

        internal static bool TrySetStartupForTask(string taskName, bool enable, out string? errorMessage)
        {
            errorMessage = null;
            try
            {
                if (!enable)
                {
                    if (!QueryStateForTask(taskName).Exists)
                    {
                        return true;
                    }

                    var (exitCode, stdout, stderr) = RunSchtasks($"/delete /tn \"{taskName}\" /f");
                    if (exitCode == 0 || !QueryStateForTask(taskName).Exists)
                    {
                        return true;
                    }

                    errorMessage = PickErrorText(stderr, stdout, $"schtasks exited with code {exitCode}");
                    return false;
                }

                string exePath = ResolveExecutablePath();
                if (!File.Exists(exePath))
                {
                    errorMessage = $"Executable not found at {exePath}";
                    return false;
                }

                string workingDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
                string userId;
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    userId = identity.Name;
                }

                string xml = BuildTaskXml(exePath, workingDir, userId);
                string tempXmlPath = Path.Combine(Path.GetTempPath(), $"cryotray_task_{Guid.NewGuid():N}.xml");
                try
                {
                    File.WriteAllText(tempXmlPath, xml, Encoding.Unicode);
                    var (exitCode, stdout, stderr) = RunSchtasks($"/create /tn \"{taskName}\" /xml \"{tempXmlPath}\" /f");
                    if (exitCode == 0 && QueryStateForTask(taskName).Exists)
                    {
                        return true;
                    }

                    errorMessage = PickErrorText(stderr, stdout, $"schtasks exited with code {exitCode}");
                    return false;
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempXmlPath))
                        {
                            File.Delete(tempXmlPath);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                Debug.WriteLine($"StartupTaskService error: {ex.Message}");
                return false;
            }
        }

        public static void EnsureSynchronized(bool shouldEnable)
        {
            try
            {
                bool isAdmin = IsCurrentProcessElevated();
                var state = QueryState();

                if (!shouldEnable)
                {
                    if (state.Exists && isAdmin)
                    {
                        TrySetStartup(false, out _);
                    }
                    return;
                }

                if (!isAdmin)
                {
                    return;
                }

                string expectedExe = ResolveExecutablePath();
                bool hasAutostart = !string.IsNullOrWhiteSpace(state.Arguments) &&
                    state.Arguments.Contains(AutostartArg, StringComparison.OrdinalIgnoreCase);

                bool needsCreateOrUpdate = !state.Exists ||
                    !string.Equals(state.CommandPath, expectedExe, StringComparison.OrdinalIgnoreCase) ||
                    !hasAutostart ||
                    !state.AllowsBattery;

                if (needsCreateOrUpdate)
                {
                    TrySetStartup(true, out _);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StartupTaskService EnsureSynchronized error: {ex.Message}");
            }
        }

        private static (int ExitCode, string StdOut, string StdErr) RunSchtasks(string arguments)
        {
            string schtasksPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
            var psi = new ProcessStartInfo
            {
                FileName = schtasksPath,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return (-1, "", "Failed to start schtasks.exe");
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(10000))
            {
                try { proc.Kill(); } catch { }
                return (-1, "", "schtasks.exe timed out.");
            }

            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
            return (proc.ExitCode, stdout, stderr);
        }

        private static string PickErrorText(string stderr, string stdout, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                return stderr.Trim();
            }

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                return stdout.Trim();
            }

            return fallback;
        }
    }
}
