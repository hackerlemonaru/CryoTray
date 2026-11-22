using System;
using System.Collections.Generic;
using CryoTray.Models;

namespace CryoTray.Services
{
    public sealed class StorageHealthAlert
    {
        public StorageHealthAlert(string driveKey, string driveName, int healthPercent,
            string statusText, int? previousPercent)
        {
            DriveKey = driveKey;
            DriveName = driveName;
            HealthPercent = healthPercent;
            StatusText = statusText;
            PreviousPercent = previousPercent;
        }

        public string DriveKey { get; }
        public string DriveName { get; }
        public int HealthPercent { get; }
        public string StatusText { get; }
        public int? PreviousPercent { get; }
    }

    public sealed class StorageHealthAlertService
    {
        internal static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
        internal const int MeaningfulDropPercent = 10;

        private readonly object _stateLock = new();
        private readonly Dictionary<string, DriveHealthState> _states = new(StringComparer.Ordinal);

        public event Action<StorageHealthAlert>? AlertRaised;

        public void Observe(HardwareSnapshot snapshot)
        {
            Observe(snapshot, DateTime.UtcNow);
        }

        internal void Observe(HardwareSnapshot snapshot, DateTime nowUtc)
        {
            var settings = SettingsService.Instance.Current;
            if (!settings.StorageHealthAlertsEnabled)
            {
                lock (_stateLock)
                {
                    _states.Clear();
                }

                return;
            }

            var alerts = new List<StorageHealthAlert>();
            lock (_stateLock)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var drive in snapshot.StorageDrives)
                {
                    if (!drive.SmartHealthPercent.HasValue ||
                        HardwareMonitorService.IsDriveExcluded(drive, settings.ExcludedDrives))
                    {
                        continue;
                    }

                    int percent = Math.Clamp(drive.SmartHealthPercent.Value, 0, 100);
                    string status = drive.StatusText;
                    string key = HardwareMonitorService.GetDriveExclusionKey(drive);
                    string driveName = string.IsNullOrWhiteSpace(drive.Model)
                        ? LocalizationService.Format("Alert_DriveName", drive.Index + 1)
                        : drive.Model.Trim();

                    seen.Add(key);
                    if (!_states.TryGetValue(key, out var state))
                    {
                        state = new DriveHealthState();
                        _states[key] = state;

                        if (IsWarningStatus(status))
                        {
                            state.LastAlertUtc = nowUtc;
                            alerts.Add(new StorageHealthAlert(key, driveName, percent, status, null));
                        }

                        state.LastPercent = percent;
                        state.BaselinePercent = percent;
                        state.LastStatus = status;
                        continue;
                    }

                    bool enteredWarning = IsWarningStatus(status) &&
                                          !IsWarningStatus(state.LastStatus);
                    bool becameReplace = string.Equals(status, "Replace", StringComparison.OrdinalIgnoreCase) &&
                                         !string.Equals(state.LastStatus, "Replace", StringComparison.OrdinalIgnoreCase);
                    int? baseline = state.BaselinePercent ?? state.LastPercent;
                    bool meaningfulDrop = baseline.HasValue &&
                                          percent <= baseline.Value - MeaningfulDropPercent;

                    bool cooldownElapsed = !state.LastAlertUtc.HasValue ||
                                           nowUtc - state.LastAlertUtc.Value >= Cooldown;
                    if ((enteredWarning || becameReplace || meaningfulDrop) && cooldownElapsed)
                    {
                        state.LastAlertUtc = nowUtc;
                        alerts.Add(new StorageHealthAlert(
                            key,
                            driveName,
                            percent,
                            status,
                            meaningfulDrop ? baseline : state.LastPercent));
                        state.BaselinePercent = percent;
                    }
                    else if (!state.BaselinePercent.HasValue || percent > state.BaselinePercent.Value)
                    {
                        state.BaselinePercent = percent;
                    }

                    state.LastPercent = percent;
                    state.LastStatus = status;
                }

                var missing = new List<string>();
                foreach (string key in _states.Keys)
                {
                    if (!seen.Contains(key))
                    {
                        missing.Add(key);
                    }
                }

                foreach (string key in missing)
                {
                    _states.Remove(key);
                }
            }

            foreach (var alert in alerts)
            {
                AlertRaised?.Invoke(alert);
            }
        }

        public void Reset()
        {
            lock (_stateLock)
            {
                _states.Clear();
            }
        }

        private static bool IsWarningStatus(string? status)
        {
            return string.Equals(status, "Worn", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "Replace", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class DriveHealthState
        {
            public int? LastPercent { get; set; }
            public int? BaselinePercent { get; set; }
            public string LastStatus { get; set; } = "";
            public DateTime? LastAlertUtc { get; set; }
        }
    }
}
