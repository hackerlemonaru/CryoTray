using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using CryoTray.Models;
using WinForms = System.Windows.Forms;

namespace CryoTray.Services
{
    public sealed class AutopilotService : IDisposable
    {
        private static AutopilotService? _instance;
        public static AutopilotService Instance => _instance ??= new AutopilotService();

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2.5);

        private static readonly HashSet<string> ShellProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer",
            "CryoTray",
            "SearchHost",
            "ShellExperienceHost",
            "StartMenuExperienceHost",
            "ApplicationFrameHost",
            "LockApp"
        };

        private readonly object _stateLock = new();
        private Timer? _timer;
        private int _polling;
        private bool _pausedByAutopilot;
        private bool _lastShouldPause;
        private bool _disposed;

        public bool ShouldHoldPause
        {
            get
            {
                lock (_stateLock)
                {
                    return _pausedByAutopilot;
                }
            }
        }

        public bool IsPauseConditionActive
        {
            get
            {
                lock (_stateLock)
                {
                    return _lastShouldPause;
                }
            }
        }

        internal Func<bool>? ExternalPauseHoldCheck { get; set; }

        internal void TransferTimedPauseToAutopilot()
        {
            lock (_stateLock)
            {
                if (_lastShouldPause)
                {
                    _pausedByAutopilot = true;
                }
            }
        }

        private AutopilotService()
        {
        }

        public void Start()
        {
            lock (_stateLock)
            {
                if (_disposed || _timer != null)
                {
                    return;
                }

                _timer = new Timer(_ => Poll(), null, PollInterval, PollInterval);
            }
        }

        public void Stop()
        {
            Timer? timer;
            lock (_stateLock)
            {
                timer = _timer;
                _timer = null;
            }

            timer?.Dispose();
        }

        private void Poll()
        {
            if (_disposed || Interlocked.Exchange(ref _polling, 1) != 0)
            {
                return;
            }

            try
            {
                Evaluate();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Autopilot poll error: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
            }
        }

        private void Evaluate()
        {
            var settings = SettingsService.Instance.Current;
            if (!settings.AutopilotEnabled)
            {
                lock (_stateLock)
                {
                    _pausedByAutopilot = false;
                    _lastShouldPause = false;
                }
                return;
            }

            var whitelist = settings.AutopilotWhitelist ?? new List<string>();
            bool hasWhitelist = whitelist.Any(entry => !string.IsNullOrWhiteSpace(entry));
            bool needProcessScan = hasWhitelist || settings.AutopilotPauseOnFullscreen;

            Dictionary<uint, string>? runningByPid = needProcessScan
                ? EnumerateProcessesByPidToolhelp32()
                : null;

            bool isWhitelistedRunning = hasWhitelist && runningByPid != null &&
                (settings.AutopilotForegroundOnly
                    ? IsWhitelistedForegroundProcess(whitelist, runningByPid)
                    : IsAnyWhitelistedProcessRunning(whitelist, runningByPid.Values));
            bool isFullscreenRunning = settings.AutopilotPauseOnFullscreen &&
                IsFullscreen3DOrBorderlessActive(runningByPid);
            bool shouldPause = isWhitelistedRunning || isFullscreenRunning;

            bool pauseCooling = false;
            bool resumeCooling = false;

            lock (_stateLock)
            {
                _lastShouldPause = shouldPause;
                if (shouldPause &&
                    !_pausedByAutopilot &&
                    PowerPlanService.Instance.IsCoolingModeActive)
                {
                    _pausedByAutopilot = true;
                    pauseCooling = true;
                }
                else if (!shouldPause && _pausedByAutopilot)
                {
                    _pausedByAutopilot = false;
                    resumeCooling = true;
                }
            }

            if (pauseCooling)
            {
                SetCoolingMode(false);
            }
            else if (resumeCooling && ExternalPauseHoldCheck?.Invoke() != true)
            {
                SetCoolingMode(true);
            }
        }

        private static bool IsAnyWhitelistedProcessRunning(
            IReadOnlyCollection<string> whitelist,
            IEnumerable<string> runningNames)
        {
            var targets = whitelist
                .Select(NormalizeProcessName)
                .Where(name => name.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (targets.Count == 0)
            {
                return false;
            }

            foreach (string processName in runningNames)
            {
                if (targets.Contains(processName))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool IsWhitelistedProcessName(
            string? processName,
            IReadOnlyCollection<string> whitelist)
        {
            string normalized = NormalizeProcessName(processName);
            if (normalized.Length == 0)
            {
                return false;
            }

            foreach (string entry in whitelist)
            {
                if (string.Equals(
                    NormalizeProcessName(entry),
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool IsWhitelistedForegroundProcess(
            IReadOnlyCollection<string> whitelist,
            IReadOnlyDictionary<uint, string> processesByPid)
        {
            IntPtr fgWindow = NativeMethods.GetForegroundWindow();
            if (fgWindow == IntPtr.Zero)
            {
                return false;
            }

            _ = NativeMethods.GetWindowThreadProcessId(fgWindow, out uint fgPid);
            if (fgPid == 0 || fgPid == (uint)Environment.ProcessId)
            {
                return false;
            }

            return processesByPid.TryGetValue(fgPid, out string? processName) &&
                   IsWhitelistedProcessName(processName, whitelist);
        }

        /// <summary>Enumerates running process names keyed by PID with a single Toolhelp32 kernel snapshot.</summary>
        internal static Dictionary<uint, string> EnumerateProcessesByPidToolhelp32()
        {
            var result = new Dictionary<uint, string>();
            IntPtr snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == NativeMethods.INVALID_HANDLE_VALUE)
            {
                return result;
            }

            try
            {
                var entry = new NativeMethods.PROCESSENTRY32W
                {
                    dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>()
                };

                if (!NativeMethods.Process32FirstW(snapshot, ref entry))
                {
                    return result;
                }

                uint currentPid = (uint)Environment.ProcessId;
                do
                {
                    if (entry.th32ProcessID != 0 && entry.th32ProcessID != currentPid)
                    {
                        string normalized = NormalizeProcessName(entry.szExeFile);
                        if (!string.IsNullOrEmpty(normalized))
                        {
                            result[entry.th32ProcessID] = normalized;
                        }
                    }
                }
                while (NativeMethods.Process32NextW(snapshot, ref entry));
            }
            finally
            {
                NativeMethods.CloseHandle(snapshot);
            }

            return result;
        }

        /// <summary>Detects exclusive Direct3D fullscreen or borderless fullscreen non-shell windows.</summary>
        internal static bool IsFullscreen3DOrBorderlessActive(IReadOnlyDictionary<uint, string>? processesByPid = null)
        {
            int state = 0;
            bool hasNotificationState = NativeMethods.SHQueryUserNotificationState(out state) == 0;
            if (hasNotificationState && state == NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN)
            {
                return true;
            }

            IntPtr fgWindow = NativeMethods.GetForegroundWindow();
            if (fgWindow == IntPtr.Zero)
            {
                return false;
            }

            _ = NativeMethods.GetWindowThreadProcessId(fgWindow, out uint fgPid);
            if (fgPid == 0 || fgPid == (uint)Environment.ProcessId)
            {
                return false;
            }

            var processMap = processesByPid ?? EnumerateProcessesByPidToolhelp32();
            if (processMap.TryGetValue(fgPid, out string? procName) &&
                ShellProcessNames.Contains(procName))
            {
                return false;
            }

            if (hasNotificationState &&
                (state == NativeMethods.QUNS_BUSY || state == NativeMethods.QUNS_PRESENTATION_MODE))
            {
                return true;
            }

            if (!NativeMethods.GetWindowRect(fgWindow, out var rect))
            {
                return false;
            }

            var bounds = WinForms.Screen.FromHandle(fgWindow).Bounds;
            return rect.Left <= bounds.Left + 2 &&
                   rect.Top <= bounds.Top + 2 &&
                   rect.Right >= bounds.Right - 2 &&
                   rect.Bottom >= bounds.Bottom - 2;
        }

        private static string NormalizeProcessName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            try
            {
                return Path.GetFileNameWithoutExtension(value.Trim());
            }
            catch (ArgumentException)
            {
                return "";
            }
        }

        private static void SetCoolingMode(bool enable)
        {
            var application = System.Windows.Application.Current;
            if (application?.Dispatcher != null && !application.Dispatcher.CheckAccess())
            {
                application.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (PowerPlanService.Instance.IsCoolingModeActive != enable)
                    {
                        PowerPlanService.Instance.SetCoolingMode(enable);
                    }
                }));
                return;
            }

            if (PowerPlanService.Instance.IsCoolingModeActive != enable)
            {
                PowerPlanService.Instance.SetCoolingMode(enable);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
        }
    }
}
