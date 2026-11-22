using System;
using System.Collections.Generic;

namespace CryoTray.Models
{
    public class AppSettings
    {
        public string Language { get; set; } = "English"; // "English" or "العربية"
        public string Theme { get; set; } = "Dark"; // "Dark", "Light", or "System"
        public string TemperatureUnit { get; set; } = "Celsius"; // "Celsius" or "Fahrenheit"
        public bool ShowComputerName { get; set; } = true;
        public bool LaunchOnStartup { get; set; } = false;
        public bool LaunchInBackground { get; set; } = false;
        public bool EnableCoolingOnLaunch { get; set; } = false;
        public bool EnableCoolingOnBattery { get; set; } = false;
        public bool DisableCoolingModeOnExit { get; set; } = true;
        public bool PinToTaskbar { get; set; } = true;
        public bool AlwaysOnTop { get; set; } = false;
        public bool HideOnFocusLoss { get; set; } = true;
        public bool UseHotkey { get; set; } = false;
        public string HotkeyModifiers { get; set; } = "Ctrl+Shift";
        public string HotkeyKey { get; set; } = "C";

        // Memory Optimization
        public bool RamFreeWorkingSets { get; set; } = true;
        public bool RamClearStandby { get; set; } = true;
        public bool RamClearClipboard { get; set; } = false;
        public bool RamShowNotification { get; set; } = true;
        public bool AutoRamOptimizeEnabled { get; set; } = false;
        public int AutoRamThresholdPercent { get; set; } = 25;
        public bool AutoRamCpuIdleOnly { get; set; } = true;
        public int AutoRamIntervalHours { get; set; } = 0; // 0 = Disabled, 1, 2, 4, 6
        public bool UseRamHotkey { get; set; } = false;
        public string RamHotkeyModifiers { get; set; } = "Ctrl+Alt";
        public string RamHotkeyKey { get; set; } = "M";

        // View
        public bool ShowGpuBadge { get; set; } = true;
        public bool ShowCpuBadge { get; set; } = true;
        public bool CpuTrayWhiteDigits { get; set; } = false;
        public bool GpuTrayWhiteDigits { get; set; } = false;
        public string CpuTrayBadgeMetric { get; set; } = "Temperature";
        public string GpuTrayBadgeMetric { get; set; } = "Temperature";
        public string CoolingOnFrameColor { get; set; } = "#2B88D8";
        public string CoolingOffFrameColor { get; set; } = "#C26A47";
        public bool AnchorToTray { get; set; } = true;
        public bool ShowOverlayRow { get; set; } = false;
        public bool ShowAutopilotRow { get; set; } = false;
        public bool MergeTrayIcons { get; set; } = false;

        // Dashboard readouts
        public bool ShowCpuClock { get; set; } = true;
        public bool ShowCpuBaseSpeed { get; set; } = true;
        public bool ShowCpuGraph { get; set; } = true;
        public bool CpuGraphSmall { get; set; } = false;
        public int CpuGraphTimeframeMinutes { get; set; } = 1;
        public bool ShowDiskTemp { get; set; } = true;
        public List<string> DiskGraphDrives { get; set; } = new();
        public Dictionary<string, int> DiskGraphTimeframes { get; set; } = new();
        public bool ShowCpuPower { get; set; } = true;
        public bool ShowRamRow { get; set; } = true;
        public bool ShowGpuPower { get; set; } = true;
        public bool ShowGpuTemp { get; set; } = true;
        public bool ShowGpuName { get; set; } = false;
        public bool ShowGpuHotspot { get; set; } = true;
        public bool ShowGpuVram { get; set; } = true;
        public bool ShowGpuFan { get; set; } = true;
        public bool ShowGpuDetails { get; set; } = false;
        public string GpuGraphMetric { get; set; } = "Temperature";
        public bool ShowFanSpeeds { get; set; } = true;
        public string PreferredGpuIdentifier { get; set; } = "";

        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }

        // CPU & Power Plan
        public string CoolingPowerPlanGuid { get; set; } = "";
        public string NormalPowerPlanGuid { get; set; } = "381b4222-f694-41f0-9685-ff5bb260df2e"; // Balanced
        public int MaxCpuThrottlePercent { get; set; } = 99; // Default 99%
        public int PerfBoostMode { get; set; } = 0; // 0 = Disabled (Default), 1 = Enabled

        // Stress Test
        public int StressFpsReference { get; set; } = 100; // 60, 100, 120, 144, 165, or 240

        // Autopilot
        public bool AutopilotEnabled { get; set; } = false;
        public bool AutopilotPauseOnFullscreen { get; set; } = false;
        public bool AutopilotForegroundOnly { get; set; } = false;
        public List<string> AutopilotWhitelist { get; set; } = new();

        // Overlay
        public bool OverlayEnabled { get; set; } = false;
        public bool OverlayClickThrough { get; set; } = false;
        public bool OverlayShowCpuTemp { get; set; } = true;
        public bool OverlayShowCpuLoad { get; set; } = true;
        public bool OverlayShowCpuPower { get; set; } = true;
        public bool OverlayShowCpuClock { get; set; } = true;
        public bool OverlayShowGpuTemp { get; set; } = true;
        public bool OverlayShowGpuLoad { get; set; } = true;
        public bool OverlayShowGpuPower { get; set; } = true;
        public bool OverlayShowRam { get; set; } = false;
        public bool ShowBatteryStatus { get; set; } = true;
        public int OverlayOpacityPercent { get; set; } = 90;
        public double? OverlayLeft { get; set; }
        public double? OverlayTop { get; set; }

        // Thresholds and alerts
        public int HighTempThresholdCpu { get; set; } = 85;
        public int HighTempThresholdGpu { get; set; } = 80;
        public int HighTempThresholdDisk { get; set; } = 60;
        public bool DisableAllNotifications { get; set; } = false;
        public bool MuteTemperatureAlerts { get; set; } = false;
        public bool GpuDeltaAlertsEnabled { get; set; } = true;
        public int GpuHotspotDeltaThresholdCelsius { get; set; } = 25;
        public bool FanHealthAlertsEnabled { get; set; } = true;
        public Dictionary<string, string> FanAliases { get; set; } = new();
        public bool StorageHealthAlertsEnabled { get; set; } = true;

        // Updates
        public bool AutomaticUpdateChecks { get; set; } = true;
        public bool SaveRollbackOnUpdate { get; set; } = true;
        public DateTime? LastUpdateCheckUtc { get; set; }
        public string LastUpdateOutcome { get; set; } = "";
        public string LastUpdateVersion { get; set; } = "";
        public string LastUpdateDetail { get; set; } = "";
        public string LastNotifiedUpdateVersion { get; set; } = "";
        public string DismissedUpdateVersion { get; set; } = "";
        public string UpdateSnoozedVersion { get; set; } = "";
        public DateTime? UpdateSnoozedUntilUtc { get; set; }

        public List<string> ExcludedDrives { get; set; } = new();
    }
}
