using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CryoTray.Models;

namespace CryoTray.Services
{
    public enum MemoryOptimizationTrigger
    {
        Manual,
        Hotkey,
        Threshold,
        Scheduled
    }

    public sealed record MemoryOptimizationResult(
        bool Succeeded,
        bool RequiresElevation,
        bool NoScopeSelected,
        ulong BeforeAvailableBytes,
        ulong AfterAvailableBytes,
        ulong FreedBytes,
        DateTime TimestampLocal,
        int LastNtStatus,
        MemoryOptimizationTrigger Trigger);

    /// <summary>Performs background NT kernel memory list cleanup and CPU-idle guarded auto-optimization.</summary>
    public sealed class MemoryOptimizerService : IDisposable
    {
        private static MemoryOptimizerService? _instance;
        public static MemoryOptimizerService Instance => _instance ??= new MemoryOptimizerService();

        private static readonly TimeSpan ThresholdCooldown = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan EvalInterval = TimeSpan.FromSeconds(10);

        private int _optimizingFlag;
        private int _autoEvalInProgress;
        private bool _started;
        private bool _disposed;
        private int _lastConfiguredIntervalHours;
        private DateTime _lastAutoEvalUtc = DateTime.MinValue;
        private DateTime _lastThresholdTriggerUtc = DateTime.MinValue;
        private DateTime _lastPeriodicRunUtc = DateTime.UtcNow;
        private CancellationTokenSource? _cts;

        public bool IsOptimizing => Volatile.Read(ref _optimizingFlag) == 1;
        public MemoryOptimizationResult? LastResult { get; private set; }
        public MemoryOptimizationResult? LastSuccessfulResult { get; private set; }

        public event Action<bool>? OptimizingStateChanged;
        public event Action<MemoryOptimizationResult>? OptimizationCompleted;

        private MemoryOptimizerService()
        {
        }

        public void Start()
        {
            if (_disposed || _started)
            {
                return;
            }

            _started = true;
            _cts = new CancellationTokenSource();
            var settings = SettingsService.Instance.Current;
            _lastConfiguredIntervalHours = settings.AutoRamIntervalHours;
            _lastPeriodicRunUtc = DateTime.UtcNow;

            HardwareMonitorService.Instance.TelemetryUpdated += OnTelemetryUpdated;
            SettingsService.Instance.SettingsChanged += OnSettingsChanged;
        }

        public void Stop()
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            HardwareMonitorService.Instance.TelemetryUpdated -= OnTelemetryUpdated;
            SettingsService.Instance.SettingsChanged -= OnSettingsChanged;

            try
            {
                _cts?.Cancel();
            }
            catch
            {
            }

            _cts?.Dispose();
            _cts = null;
        }

        private void OnSettingsChanged()
        {
            int interval = SettingsService.Instance.Current.AutoRamIntervalHours;
            if (interval != _lastConfiguredIntervalHours)
            {
                _lastConfiguredIntervalHours = interval;
                if (interval > 0)
                {
                    _lastPeriodicRunUtc = DateTime.UtcNow;
                }
            }
        }

        private void OnTelemetryUpdated(HardwareSnapshot snapshot)
        {
            if (_disposed || !_started || IsOptimizing)
            {
                return;
            }

            var settings = SettingsService.Instance.Current;
            if (!settings.AutoRamOptimizeEnabled)
            {
                return;
            }

            DateTime nowUtc = DateTime.UtcNow;
            if (nowUtc - _lastAutoEvalUtc < EvalInterval)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _autoEvalInProgress, 1, 0) != 0)
            {
                return;
            }

            _lastAutoEvalUtc = nowUtc;

            double availablePercent = snapshot.Memory?.AvailablePercent ??
                (double.IsFinite(snapshot.RamUsagePercent) ? Math.Max(0.0, 100.0 - snapshot.RamUsagePercent) : double.NaN);

            bool thresholdDue = double.IsFinite(availablePercent) &&
                availablePercent < settings.AutoRamThresholdPercent &&
                (nowUtc - _lastThresholdTriggerUtc) >= ThresholdCooldown;

            bool periodicDue = settings.AutoRamIntervalHours > 0 &&
                (nowUtc - _lastPeriodicRunUtc) >= TimeSpan.FromHours(settings.AutoRamIntervalHours);

            if (!thresholdDue && !periodicDue)
            {
                Volatile.Write(ref _autoEvalInProgress, 0);
                return;
            }

            CancellationToken token = _cts?.Token ?? CancellationToken.None;
            _ = EvaluateAndTriggerAutoAsync(thresholdDue, periodicDue, token);
        }

        private async Task EvaluateAndTriggerAutoAsync(bool thresholdDue, bool periodicDue, CancellationToken token)
        {
            try
            {
                var settings = SettingsService.Instance.Current;
                if (!settings.AutoRamOptimizeEnabled || token.IsCancellationRequested)
                {
                    return;
                }

                if (settings.AutoRamCpuIdleOnly)
                {
                    double? cpuUsage = await MemoryTelemetryService.MeasureCpuUsageAsync(
                        TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    if (!cpuUsage.HasValue || cpuUsage.Value >= 10.0)
                    {
                        // Defer evaluation by 20s total while busy without dropping due triggers.
                        _lastAutoEvalUtc = DateTime.UtcNow.AddSeconds(10);
                        return;
                    }
                }

                var trigger = thresholdDue
                    ? MemoryOptimizationTrigger.Threshold
                    : MemoryOptimizationTrigger.Scheduled;

                var result = await OptimizeAsync(trigger, token).ConfigureAwait(false);
                if (result != null && result.Succeeded)
                {
                    DateTime doneUtc = DateTime.UtcNow;
                    if (thresholdDue) _lastThresholdTriggerUtc = doneUtc;
                    if (periodicDue) _lastPeriodicRunUtc = doneUtc;
                }
                else if (result != null && result.RequiresElevation)
                {
                    DateTime doneUtc = DateTime.UtcNow;
                    if (thresholdDue) _lastThresholdTriggerUtc = doneUtc;
                    if (periodicDue) _lastPeriodicRunUtc = doneUtc;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Auto RAM optimization check error: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _autoEvalInProgress, 0);
            }
        }

        /// <summary>Executes the requested NT memory list commands safely, handling missing privileges without throwing.</summary>
        internal static (bool Succeeded, bool RequiresElevation, int LastNtStatus) ExecuteKernelCommands(
            bool freeWorkingSets,
            bool clearStandby,
            Func<int, (int PrivStatus, int CmdStatus)>? runnerOverride = null)
        {
            var commands = new List<int>(3);
            if (freeWorkingSets)
            {
                commands.Add(NativeMethods.MemoryEmptyWorkingSets);
                commands.Add(NativeMethods.MemoryFlushModifiedList);
            }
            if (clearStandby)
            {
                commands.Add(NativeMethods.MemoryPurgeStandbyList);
            }

            if (commands.Count == 0)
            {
                return (false, false, 0);
            }

            if (runnerOverride == null)
            {
                int privStatus = NativeMethods.RtlAdjustPrivilege(
                    NativeMethods.SeProfileSingleProcessPrivilege,
                    true,
                    false,
                    out _);
                if (privStatus != 0)
                {
                    return (false, true, privStatus);
                }
            }

            bool anySucceeded = false;
            int lastFailureStatus = 0;

            foreach (int command in commands)
            {
                int cmdStatus;
                if (runnerOverride != null)
                {
                    (int privStatus, int mockCmdStatus) = runnerOverride(command);
                    if (privStatus < 0)
                    {
                        return (false, IsPrivilegeStatus(privStatus), privStatus);
                    }
                    cmdStatus = mockCmdStatus;
                }
                else
                {
                    int localCommand = command;
                    cmdStatus = NativeMethods.NtSetSystemInformation(
                        NativeMethods.SystemMemoryListInformation,
                        ref localCommand,
                        sizeof(int));
                }

                if (cmdStatus == 0)
                {
                    anySucceeded = true;
                }
                else
                {
                    lastFailureStatus = cmdStatus;
                }
            }

            bool requiresElevation = !anySucceeded && IsPrivilegeStatus(lastFailureStatus);
            return (anySucceeded, requiresElevation, anySucceeded ? 0 : lastFailureStatus);
        }

        private static bool IsPrivilegeStatus(int status)
        {
            return status == NativeMethods.STATUS_PRIVILEGE_NOT_HELD ||
                   status == NativeMethods.STATUS_ACCESS_DENIED;
        }

        /// <summary>Runs enabled RAM cleanup commands asynchronously off the UI thread.</summary>
        public async Task<MemoryOptimizationResult?> OptimizeAsync(
            MemoryOptimizationTrigger trigger,
            CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                return null;
            }

            if (Interlocked.CompareExchange(ref _optimizingFlag, 1, 0) != 0)
            {
                return null;
            }

            var settings = SettingsService.Instance.Current;
            bool freeWorkingSets = settings.RamFreeWorkingSets;
            bool clearStandby = settings.RamClearStandby;
            bool clearClipboard = settings.RamClearClipboard;
            bool showNotification = settings.RamShowNotification && !settings.MuteTemperatureAlerts &&
                                    !settings.DisableAllNotifications;

            if (!freeWorkingSets && !clearStandby)
            {
                MemoryTelemetryService.TryReadBreakdown(out var current);
                var emptyScope = new MemoryOptimizationResult(
                    Succeeded: false,
                    RequiresElevation: false,
                    NoScopeSelected: true,
                    BeforeAvailableBytes: current.AvailableBytes,
                    AfterAvailableBytes: current.AvailableBytes,
                    FreedBytes: 0UL,
                    TimestampLocal: DateTime.Now,
                    LastNtStatus: 0,
                    Trigger: trigger);
                LastResult = emptyScope;
                Volatile.Write(ref _optimizingFlag, 0);
                NotifyOptimizationCompleted(emptyScope, false, false);
                return emptyScope;
            }

            NotifyOptimizingStateChanged(true);

            try
            {
                var result = await Task.Run(async () =>
                {
                    MemoryTelemetryService.TryReadBreakdown(out var before);
                    (bool succeeded, bool requiresElevation, int lastStatus) =
                        ExecuteKernelCommands(freeWorkingSets, clearStandby);

                    if (succeeded)
                    {
                        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    }

                    MemoryTelemetryService.TryReadBreakdown(out var after);
                    ulong freed = succeeded && after.AvailableBytes > before.AvailableBytes
                        ? after.AvailableBytes - before.AvailableBytes
                        : 0UL;

                    return new MemoryOptimizationResult(
                        Succeeded: succeeded,
                        RequiresElevation: requiresElevation,
                        NoScopeSelected: false,
                        BeforeAvailableBytes: before.AvailableBytes,
                        AfterAvailableBytes: after.AvailableBytes,
                        FreedBytes: freed,
                        TimestampLocal: DateTime.Now,
                        LastNtStatus: lastStatus,
                        Trigger: trigger);
                }, cancellationToken).ConfigureAwait(false);

                LastResult = result;
                if (result.Succeeded)
                {
                    LastSuccessfulResult = result;
                }

                HardwareMonitorService.Instance.RefreshMemorySnapshotNow();
                Volatile.Write(ref _optimizingFlag, 0);
                NotifyOptimizingStateChanged(false);
                NotifyOptimizationCompleted(result, result.Succeeded && clearClipboard, result.Succeeded && showNotification);
                return result;
            }
            catch (OperationCanceledException)
            {
                Volatile.Write(ref _optimizingFlag, 0);
                NotifyOptimizingStateChanged(false);
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Memory optimization error: {ex.Message}");
                Volatile.Write(ref _optimizingFlag, 0);
                NotifyOptimizingStateChanged(false);
                return null;
            }
        }

        private void NotifyOptimizingStateChanged(bool optimizing)
        {
            var handler = OptimizingStateChanged;
            if (handler != null)
            {
                handler.Invoke(optimizing);
            }
        }

        private void NotifyOptimizationCompleted(
            MemoryOptimizationResult result,
            bool clearClipboard,
            bool showNotification)
        {
            var app = System.Windows.Application.Current as App;
            if (app?.Dispatcher != null && (clearClipboard || showNotification))
            {
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (clearClipboard)
                    {
                        try
                        {
                            System.Windows.Clipboard.Clear();
                        }
                        catch
                        {
                        }
                    }

                    if (showNotification)
                    {
                        string freedText = MemoryTelemetryService.FormatBytes(result.FreedBytes);
                        double usedPct = HardwareMonitorService.Instance.CurrentSnapshot.RamUsagePercent;
                        string pctText = double.IsFinite(usedPct)
                            ? $"{Math.Round(usedPct)}"
                            : "--";
                        app.ShowTrayNotification(
                            LocalizationService.Get("Ram_NotifyTitle"),
                            LocalizationService.Format("Ram_NotifyBody", freedText, pctText),
                            null,
                            System.Windows.Forms.ToolTipIcon.Info);
                    }
                }));
            }

            OptimizationCompleted?.Invoke(result);
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
