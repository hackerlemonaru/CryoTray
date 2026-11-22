using System;
using System.Collections.Generic;
using CryoTray.Models;

namespace CryoTray.Services
{
    public enum FanHealthState
    {
        Watching,
        Armed,
        IdleAtZero,
        Stopped
    }

    public sealed class FanHealthStatus
    {
        public FanHealthStatus(string key, string name, bool isGpuFan, int rpm,
            FanHealthState state, DateTime? lastAlertUtc, DateTime? spinningSinceUtc,
            DateTime? zeroSinceUtc)
        {
            Key = key;
            Name = name;
            IsGpuFan = isGpuFan;
            Rpm = rpm;
            State = state;
            LastAlertUtc = lastAlertUtc;
            SpinningSinceUtc = spinningSinceUtc;
            ZeroSinceUtc = zeroSinceUtc;
        }

        public string Key { get; }
        public string Name { get; }
        public bool IsGpuFan { get; }
        public int Rpm { get; }
        public FanHealthState State { get; }
        public DateTime? LastAlertUtc { get; }
        public DateTime? SpinningSinceUtc { get; }
        public DateTime? ZeroSinceUtc { get; }
    }

    public sealed class FanStoppedEvent
    {
        public FanStoppedEvent(string fanKey, string fanName, int lastRpm, bool isGpuFan)
        {
            FanKey = fanKey;
            FanName = fanName;
            LastRpm = lastRpm;
            IsGpuFan = isGpuFan;
        }

        public string FanKey { get; }
        public string FanName { get; }
        public int LastRpm { get; }
        public bool IsGpuFan { get; }
    }

    public sealed class FanHealthService
    {
        internal const int MinSpinningRpm = 300;
        internal static readonly TimeSpan SpinArmDuration = TimeSpan.FromMinutes(1);
        internal static readonly TimeSpan StoppedAlertDuration = TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan DemandGrace = TimeSpan.FromSeconds(15);
        internal static readonly TimeSpan ResumeGrace = TimeSpan.FromSeconds(60);
        internal static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(10);

        private const double DemandCpuLoadPercent = 25;
        private const double DemandGpuLoadPercent = 25;
        private const double DemandTemperatureCelsius = 60;

        private readonly object _stateLock = new();
        private readonly Dictionary<string, FanState> _states = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _order = new();
        private DateTime? _demandSinceUtc;
        private DateTime? _gpuDemandSinceUtc;
        private DateTime? _resumeGraceUntilUtc;

        public event Action<FanStoppedEvent>? FanStopped;

        public static string GetDisplayName(FanHealthStatus status)
        {
            return GetDisplayName(status.Key, status.Name, status.IsGpuFan);
        }

        public static string GetDisplayName(string fanKey, string fallbackName, bool isGpuFan)
        {
            var aliases = SettingsService.Instance.Current.FanAliases;
            if (aliases != null &&
                aliases.TryGetValue(fanKey, out string? alias) &&
                !string.IsNullOrWhiteSpace(alias))
            {
                return alias.Replace('\r', ' ').Replace('\n', ' ').Trim();
            }

            return isGpuFan ? LocalizationService.Get("FanHealth_GpuFan") : fallbackName;
        }

        public IReadOnlyList<FanHealthStatus> GetStatuses()
        {
            lock (_stateLock)
            {
                var result = new List<FanHealthStatus>(_order.Count);
                foreach (string key in _order)
                {
                    if (!_states.TryGetValue(key, out var state))
                    {
                        continue;
                    }

                    FanHealthState status = state.Armed
                        ? (state.CurrentRpm == 0 ? FanHealthState.Stopped : FanHealthState.Armed)
                        : (state.CurrentRpm == 0 ? FanHealthState.IdleAtZero : FanHealthState.Watching);
                    result.Add(new FanHealthStatus(
                        key,
                        state.Name,
                        state.IsGpuFan,
                        state.CurrentRpm,
                        status,
                        state.LastAlertUtc,
                        state.SpinningSinceUtc,
                        state.ZeroSinceUtc));
                }

                return result;
            }
        }

        public void Observe(HardwareSnapshot snapshot)
        {
            Observe(snapshot, DateTime.UtcNow);
        }

        internal void Observe(HardwareSnapshot snapshot, DateTime nowUtc)
        {
            if (!SettingsService.Instance.Current.FanHealthAlertsEnabled)
            {
                lock (_stateLock)
                {
                    ClearState();
                }

                return;
            }

            var alerts = new List<FanStoppedEvent>();
            lock (_stateLock)
            {
                if (_resumeGraceUntilUtc.HasValue)
                {
                    if (nowUtc < _resumeGraceUntilUtc.Value)
                    {
                        return;
                    }

                    _resumeGraceUntilUtc = null;
                }

                bool cpuDemand = IsAtLeast(snapshot.CpuLoad, DemandCpuLoadPercent) &&
                                 IsAtLeast(snapshot.CpuTemp, DemandTemperatureCelsius);
                bool gpuDemand = IsAtLeast(snapshot.GpuLoad, DemandGpuLoadPercent) &&
                                 IsAtLeast(snapshot.GpuTemp, DemandTemperatureCelsius);

                if (cpuDemand || gpuDemand)
                {
                    _demandSinceUtc ??= nowUtc;
                }
                else
                {
                    _demandSinceUtc = null;
                }

                if (gpuDemand)
                {
                    _gpuDemandSinceUtc ??= nowUtc;
                }
                else
                {
                    _gpuDemandSinceUtc = null;
                }

                bool demandSustained = _demandSinceUtc.HasValue &&
                                       nowUtc - _demandSinceUtc.Value >= DemandGrace;
                bool gpuDemandSustained = _gpuDemandSinceUtc.HasValue &&
                                          nowUtc - _gpuDemandSinceUtc.Value >= DemandGrace;

                var observations = new List<(string Key, string Name, bool IsGpu, int Rpm)>();
                foreach (var fan in snapshot.Fans)
                {
                    if (!string.IsNullOrWhiteSpace(fan.Name))
                    {
                        observations.Add(($"fan:{fan.Name}", fan.Name, false, fan.Rpm));
                    }
                }

                if (snapshot.GpuFanRpm.HasValue && snapshot.GpuFanRpm.Value >= 0)
                {
                    observations.Add(("gpu", "", true, snapshot.GpuFanRpm.Value));
                }

                _order.Clear();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var observation in observations)
                {
                    if (!seen.Add(observation.Key))
                    {
                        continue;
                    }

                    _order.Add(observation.Key);
                    if (!_states.TryGetValue(observation.Key, out var state))
                    {
                        state = new FanState
                        {
                            Name = observation.Name,
                            IsGpuFan = observation.IsGpu
                        };
                        _states[observation.Key] = state;
                    }

                    state.CurrentRpm = observation.Rpm;
                    bool fanDemandSustained = observation.IsGpu
                        ? gpuDemandSustained
                        : demandSustained;

                    if (observation.Rpm >= MinSpinningRpm)
                    {
                        if (!state.SpinningSinceUtc.HasValue)
                        {
                            state.SpinningSinceUtc = nowUtc;
                            state.Armed = false;
                            state.Alerted = false;
                        }

                        if (!state.Armed &&
                            nowUtc - state.SpinningSinceUtc.Value >= SpinArmDuration)
                        {
                            state.Armed = true;
                        }

                        state.LastRpm = observation.Rpm;
                        state.ZeroSinceUtc = null;
                    }
                    else if (observation.Rpm == 0)
                    {
                        if (!state.Armed)
                        {
                            state.SpinningSinceUtc = null;
                            state.ZeroSinceUtc = null;
                            continue;
                        }

                        state.SpinningSinceUtc = null;
                        state.ZeroSinceUtc ??= nowUtc;

                        if (state.Alerted || !fanDemandSustained)
                        {
                            continue;
                        }

                        if (nowUtc - state.ZeroSinceUtc.Value < StoppedAlertDuration)
                        {
                            continue;
                        }

                        if (state.LastAlertUtc.HasValue &&
                            nowUtc - state.LastAlertUtc.Value < AlertCooldown)
                        {
                            continue;
                        }

                        state.Alerted = true;
                        state.LastAlertUtc = nowUtc;
                        alerts.Add(new FanStoppedEvent(
                            observation.Key,
                            state.Name,
                            state.LastRpm,
                            state.IsGpuFan));
                    }
                    else
                    {
                        state.SpinningSinceUtc = null;
                        state.ZeroSinceUtc = null;
                        state.Armed = false;
                        state.Alerted = false;
                    }
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
                FanStopped?.Invoke(alert);
            }
        }

        public void Suspend()
        {
            lock (_stateLock)
            {
                ClearState();
            }
        }

        public void Resume()
        {
            Resume(DateTime.UtcNow);
        }

        internal void Resume(DateTime nowUtc)
        {
            lock (_stateLock)
            {
                ClearState();
                _resumeGraceUntilUtc = nowUtc + ResumeGrace;
            }
        }

        private void ClearState()
        {
            _states.Clear();
            _order.Clear();
            _demandSinceUtc = null;
            _gpuDemandSinceUtc = null;
            _resumeGraceUntilUtc = null;
        }

        private static bool IsAtLeast(double value, double threshold)
        {
            return double.IsFinite(value) && value >= threshold;
        }

        private sealed class FanState
        {
            public string Name { get; set; } = "";
            public bool IsGpuFan { get; set; }
            public int CurrentRpm { get; set; }
            public DateTime? SpinningSinceUtc { get; set; }
            public DateTime? ZeroSinceUtc { get; set; }
            public DateTime? LastAlertUtc { get; set; }
            public bool Armed { get; set; }
            public bool Alerted { get; set; }
            public int LastRpm { get; set; }
        }
    }
}
