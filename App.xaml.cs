using System;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CryoTray.Models;
using CryoTray.Services;
using CryoTray.Tray;
using CryoTray.Views;
using Microsoft.Win32;

namespace CryoTray
{
    public partial class App : System.Windows.Application
    {
        public const string ActivateEventName = "CryoTray-Show-Dashboard-Event-99210";

        private static Mutex? _singleInstanceMutex;
        private EventWaitHandle? _activateWaitEvent;
        private RegisteredWaitHandle? _activateRegisteredWait;
        private TrayIconManager? _trayManager;
        private MainWindow? _mainWindow;
        private OverlayWindow? _overlayWindow;
        private HwndSource? _hotkeySource;
        private DispatcherTimer? _startupTrimTimer;
        private DispatcherTimer? _powerPlanWatchTimer;
        private DispatcherTimer? _coolingPauseTimer;
        private DispatcherTimer? _updateCheckTimer;
        private TemperatureAlertService? _temperatureAlertService;
        private FanHealthService? _fanHealthService;
        private StorageHealthAlertService? _storageHealthAlertService;
        private static readonly TimeSpan DashboardUpdateInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan TrayUpdateInterval = TimeSpan.FromMinutes(30);
        private DateTime _lastAutomaticUpdateCheckUtc = DateTime.MinValue;
        private bool _dashboardVisible;
        private bool _updateCheckInProgress;
        private bool _coolingEnabledByBattery;
        private bool _isExiting;
        private bool _isPrimaryInstance;
        private bool _mutexReleased;

        public DateTime? CoolingPausedUntilUtc { get; private set; }
        public bool IsCoolingPausedByTimer =>
            CoolingPausedUntilUtc.HasValue && CoolingPausedUntilUtc.Value > DateTime.UtcNow;

        public ReleaseInfo? AvailableUpdate { get; private set; }
        public UpdateCompletionInfo? PendingUpdateSuccess { get; private set; }
        public event Action? UpdateAvailabilityChanged;

        protected override void OnStartup(StartupEventArgs e)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

            if (e.Args.Length > 0 && e.Args[0].Equals("/test", StringComparison.OrdinalIgnoreCase))
            {
                int exitCode = Tests.VerificationTests.RunAll();
                Environment.Exit(exitCode);
                return;
            }

            if (e.Args.Length > 0 && e.Args[0].Equals("/test-unit", StringComparison.OrdinalIgnoreCase))
            {
                int exitCode = Tests.VerificationTests.RunUnitSafe();
                Environment.Exit(exitCode);
                return;
            }

            if (e.Args.Length > 0 &&
                e.Args[0].Equals("/test-updates", StringComparison.OrdinalIgnoreCase))
            {
                int exitCode = Tests.UpdateVerificationTests.RunAll();
                Environment.Exit(exitCode);
                return;
            }

            if (e.Args.Length > 0 && e.Args[0].Equals("/dump", StringComparison.OrdinalIgnoreCase))
            {
                int exitCode = Tests.CpuSensorDump.Run();
                Environment.Exit(exitCode);
                return;
            }

            if (e.Args.Length > 0 && e.Args[0].Equals("/uninstall-cleanup", StringComparison.OrdinalIgnoreCase))
            {
                PowerPlanService.Instance.CleanupForUninstall();
                Environment.Exit(0);
                return;
            }

            bool isAutostart = Array.Exists(
                e.Args,
                arg => arg.Equals(StartupTaskService.AutostartArg, StringComparison.OrdinalIgnoreCase));

            const string appGuid = "CryoTray-Hardware-Monitor-Mutex-99210";
            _singleInstanceMutex = new Mutex(true, appGuid, out bool createdNew);
            if (!createdNew)
            {
                if (!isAutostart)
                {
                    TrySignalExistingInstance(ActivateEventName);
                }
                Shutdown();
                return;
            }

            _isPrimaryInstance = true;
            RegisterActivationWaitHandle();

            base.OnStartup(e);

            ApplyWindowAccent(PowerPlanService.Instance.IsCoolingModeActive);
            PowerPlanService.Instance.CoolingModeChanged += ApplyWindowAccent;
            PowerPlanService.Instance.CoolingModeChanged += OnPowerPlanCoolingChanged;

            DispatcherUnhandledException += (s, args) =>
            {
                try
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CryoTray");
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now}] Dispatcher Exception: {args.Exception}\n");
                }
                catch { }
                MessageBox.Show(
                    LocalizationService.Format("Error_Body", args.Exception.Message),
                    LocalizationService.Get("Error_Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                try
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CryoTray");
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now}] Domain Exception: {args.ExceptionObject}\n");
                }
                catch { }
            };

            var settings = SettingsService.Instance.Current;
            LocalizationService.Instance.Apply(settings.Language);
            try
            {
#if !DEBUG
                // Debug builds and an administrator-created dev-mode marker may run from
                // user-writable output; release and published copies stay protected.
                if (!UpdateStorage.IsDeveloperModeEnabled)
                {
                    string applicationDirectory = AppContext.BaseDirectory.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (!UpdateStorage.IsSafeApplicationLocation(applicationDirectory))
                    {
                        MessageBox.Show(
                            LocalizationService.Get("Update_UnsafeLocation"),
                            LocalizationService.Get("Error_Title"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        Shutdown(1);
                        return;
                    }

                    UpdateStorage.EnsureApplicationDirectoryProtected(applicationDirectory);
                }
#endif
                UpdateStorage.EnsureUpdateRoot();
                UpdateStorage.EnsureRollbackRoot();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.Message,
                    LocalizationService.Get("Error_Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            ThemeService.Initialize(settings.Theme);
            UpdateStatusService.Instance.Restore(settings);
            PendingUpdateSuccess = UpdateCompletionService.ReadPending(UpdateService.CurrentVersion);
            ThemeService.ThemeChanged += OnThemeChanged;
            StartupTaskService.EnsureSynchronized(settings.LaunchOnStartup);

            _trayManager = new TrayIconManager();
            _trayManager.ApplyTheme();
            _trayManager.OnOpenDashboardRequested += ToggleDashboard;
            _trayManager.OnToggleCoolingRequested += () => PowerPlanService.Instance.ToggleCoolingMode();
            _trayManager.OnPauseCoolingRequested += PauseCoolingFor;
            _trayManager.OnCancelCoolingPauseRequested += () => CancelCoolingPause(resumeCooling: true, forceResume: true);
            _trayManager.OnOpenStressTestRequested += OpenStressTestFromTray;
            _trayManager.OnToggleOverlayClickThroughRequested += ToggleOverlayClickThrough;
            _trayManager.OnOpenSettingsRequested += OpenSettingsFromTray;
            _trayManager.OnExitRequested += ExitApplication;

            SettingsService.Instance.SettingsChanged += RegisterHotkeyIfEnabled;
            SettingsService.Instance.SettingsChanged += OnSettingsChanged;
            LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
            SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

            _temperatureAlertService = new TemperatureAlertService();
            _temperatureAlertService.AlertRaised += OnTemperatureAlertRaised;

            _fanHealthService = new FanHealthService();
            _fanHealthService.FanStopped += OnFanStopped;

            _storageHealthAlertService = new StorageHealthAlertService();
            _storageHealthAlertService.AlertRaised += OnStorageHealthAlertRaised;

            HardwareMonitorService.Instance.TelemetryUpdated += OnTelemetryUpdated;
            HardwareMonitorService.Instance.Start();
            AutopilotService.Instance.ExternalPauseHoldCheck = () => IsCoolingPausedByTimer;
            AutopilotService.Instance.Start();
            MemoryOptimizerService.Instance.Start();

            if (settings.EnableCoolingOnLaunch)
            {
                PowerPlanService.Instance.SetCoolingMode(true);
            }
            EvaluateBatteryCoolingRule();

            RegisterHotkeyIfEnabled();

            _powerPlanWatchTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _powerPlanWatchTimer.Tick += (s, args) => PowerPlanService.Instance.SyncCoolingStateFromSystem();
            _powerPlanWatchTimer.Start();

            if (!settings.LaunchInBackground)
            {
                ShowDashboard();
            }

            _startupTrimTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _startupTrimTimer.Tick += (s, args) =>
            {
                _startupTrimTimer?.Stop();
                _startupTrimTimer = null;
                TrimWorkingSet();
            };
            _startupTrimTimer.Start();

            ScheduleAutomaticUpdateCheck(checkNow: true);
        }

        internal static bool TrySignalExistingInstance(string eventName)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(eventName, out var existing))
                {
                    using (existing)
                    {
                        return existing.Set();
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private void RegisterActivationWaitHandle()
        {
            try
            {
                _activateWaitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
                _activateRegisteredWait = ThreadPool.RegisterWaitForSingleObject(
                    _activateWaitEvent,
                    (_, timedOut) =>
                    {
                        if (!timedOut)
                        {
                            ActivateDashboardFromSignal();
                        }
                    },
                    null,
                    Timeout.Infinite,
                    false);
            }
            catch
            {
            }
        }

        private void UnregisterActivationWaitHandle()
        {
            try
            {
                _activateRegisteredWait?.Unregister(null);
            }
            catch
            {
            }
            _activateRegisteredWait = null;

            try
            {
                _activateWaitEvent?.Dispose();
            }
            catch
            {
            }
            _activateWaitEvent = null;
        }

        private void ActivateDashboardFromSignal()
        {
            if (_isExiting)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isExiting)
                {
                    return;
                }

                var window = ShowDashboard();
                if (window != null)
                {
                    IntPtr hwnd = new WindowInteropHelper(window).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        NativeMethods.SetForegroundWindow(hwnd);
                    }
                }
            }));
        }

        private void ToggleOverlayClickThrough()
        {
            var settings = SettingsService.Instance.Current;
            settings.OverlayClickThrough = !settings.OverlayClickThrough;
            SettingsService.Instance.Save();
        }

        private void OnSystemPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                _fanHealthService?.Suspend();
                return;
            }

            if (e.Mode == PowerModes.Resume)
            {
                _fanHealthService?.Resume();
                if (!_isExiting)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                        TryRunAutomaticUpdateCheck(TimeSpan.FromSeconds(30))));
                }
                return;
            }

            if (e.Mode != PowerModes.StatusChange || _isExiting)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(EvaluateBatteryCoolingRule));
        }

        private void EvaluateBatteryCoolingRule()
        {
            if (_isExiting || IsCoolingPausedByTimer)
            {
                return;
            }

            var settings = SettingsService.Instance.Current;
            if (!settings.EnableCoolingOnBattery)
            {
                _coolingEnabledByBattery = false;
                return;
            }

            var lineStatus = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus;
            if (lineStatus == System.Windows.Forms.PowerLineStatus.Offline)
            {
                if (!PowerPlanService.Instance.IsCoolingModeActive)
                {
                    _coolingEnabledByBattery = true;
                    PowerPlanService.Instance.SetCoolingMode(true);
                }
            }
            else if (lineStatus == System.Windows.Forms.PowerLineStatus.Online && _coolingEnabledByBattery)
            {
                _coolingEnabledByBattery = false;
                if (PowerPlanService.Instance.IsCoolingModeActive)
                {
                    PowerPlanService.Instance.SetCoolingMode(false);
                }
            }
        }

        private void OnPowerPlanCoolingChanged(bool isCooling)
        {
            if (!isCooling || !CoolingPausedUntilUtc.HasValue)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() => CancelCoolingPause(resumeCooling: false)));
        }

        public void PauseCoolingFor(TimeSpan duration)
        {
            if (_isExiting || duration <= TimeSpan.Zero)
            {
                return;
            }

            CoolingPausedUntilUtc = DateTime.UtcNow.Add(duration);
            _coolingEnabledByBattery = false;
            _trayManager?.SetCoolingPauseUntil(CoolingPausedUntilUtc);

            if (PowerPlanService.Instance.IsCoolingModeActive)
            {
                PowerPlanService.Instance.SetCoolingMode(false);
            }

            if (_coolingPauseTimer == null)
            {
                _coolingPauseTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _coolingPauseTimer.Tick += CoolingPauseTimer_Tick;
            }

            _coolingPauseTimer.Start();
        }

        public void CancelCoolingPause(bool resumeCooling = true, bool forceResume = false)
        {
            _coolingPauseTimer?.Stop();
            CoolingPausedUntilUtc = null;
            _trayManager?.SetCoolingPauseUntil(null);

            if (!resumeCooling || _isExiting)
            {
                return;
            }

            if (!forceResume &&
                (AutopilotService.Instance.ShouldHoldPause || AutopilotService.Instance.IsPauseConditionActive))
            {
                AutopilotService.Instance.TransferTimedPauseToAutopilot();
                return;
            }

            if (!PowerPlanService.Instance.IsCoolingModeActive)
            {
                PowerPlanService.Instance.SetCoolingMode(true);
            }
        }

        private void CoolingPauseTimer_Tick(object? sender, EventArgs e)
        {
            if (_isExiting)
            {
                _coolingPauseTimer?.Stop();
                return;
            }

            if (!CoolingPausedUntilUtc.HasValue || DateTime.UtcNow >= CoolingPausedUntilUtc.Value)
            {
                CancelCoolingPause(resumeCooling: true, forceResume: false);
                return;
            }

            _trayManager?.SetCoolingPauseUntil(CoolingPausedUntilUtc);
        }

        private MainWindow? EnsureMainWindow()
        {
            if (_mainWindow != null)
            {
                return _mainWindow;
            }

            var window = new MainWindow();
            window.Closed += MainWindow_Closed;
            _mainWindow = window;
            return window;
        }

        private MainWindow? ShowDashboard()
        {
            var window = EnsureMainWindow();
            if (window == null)
            {
                return null;
            }

            if (!window.IsVisible)
            {
                window.PositionAboveTray();
            }

            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
            return window;
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            if (ReferenceEquals(sender, _mainWindow))
            {
                _mainWindow = null;
            }

            if (!_isExiting)
            {
                TrimWorkingSet();
            }
        }

        private void ToggleDashboard()
        {
            if (_mainWindow != null && _mainWindow.IsVisible && _mainWindow.WindowState != WindowState.Minimized)
            {
                _mainWindow.Close();
            }
            else
            {
                ShowDashboard();
            }
        }

        private void OpenSettingsFromTray()
        {
            ShowDashboard()?.OpenSettingsDialog();
        }

        private void OpenStressTestFromTray()
        {
            ShowDashboard()?.OpenStressTestDialog();
        }

        public void ToggleOverlay()
        {
            if (_overlayWindow != null && _overlayWindow.IsVisible)
            {
                DisableOverlay();
            }
            else
            {
                EnableOverlay();
            }
        }

        private void EnableOverlay()
        {
            if (_overlayWindow == null)
            {
                _overlayWindow = new OverlayWindow();
                _overlayWindow.Closed += OverlayWindow_Closed;
            }

            _overlayWindow.Show();
            _overlayWindow.Activate();

            var settings = SettingsService.Instance.Current;
            if (!settings.OverlayEnabled)
            {
                settings.OverlayEnabled = true;
                SettingsService.Instance.Save();
            }
        }

        private void DisableOverlay()
        {
            var overlay = _overlayWindow;
            _overlayWindow = null;

            if (overlay != null)
            {
                overlay.Closed -= OverlayWindow_Closed;
                overlay.Close();
            }

            var settings = SettingsService.Instance.Current;
            if (settings.OverlayEnabled)
            {
                settings.OverlayEnabled = false;
                SettingsService.Instance.Save();
            }

            TrimWorkingSet();
        }

        private void OverlayWindow_Closed(object? sender, EventArgs e)
        {
            if (ReferenceEquals(sender, _overlayWindow))
            {
                _overlayWindow = null;
            }

            if (_isExiting)
            {
                return;
            }

            var settings = SettingsService.Instance.Current;
            if (settings.OverlayEnabled)
            {
                settings.OverlayEnabled = false;
                SettingsService.Instance.Save();
            }

            TrimWorkingSet();
        }

        private void RegisterHotkeyIfEnabled()
        {
            var settings = SettingsService.Instance.Current;

            if (!settings.UseHotkey && !settings.UseRamHotkey)
            {
                GlobalHotkeyService.Instance.Unregister();
                GlobalHotkeyService.Instance.UnregisterRamHotkey();
                if (_hotkeySource != null)
                {
                    _hotkeySource.RemoveHook(HwndHook);
                    _hotkeySource.Dispose();
                    _hotkeySource = null;
                }
                return;
            }

            if (_hotkeySource == null)
            {
                var parameters = new HwndSourceParameters("CryoTrayHotkey")
                {
                    Width = 0,
                    Height = 0,
                    WindowStyle = 0,
                    ExtendedWindowStyle = 0,
                    ParentWindow = new IntPtr(-3)
                };

                _hotkeySource = new HwndSource(parameters);
                _hotkeySource.AddHook(HwndHook);
            }

            if (settings.UseHotkey)
            {
                GlobalHotkeyService.Instance.Register(_hotkeySource.Handle, settings.HotkeyModifiers, settings.HotkeyKey);
            }
            else
            {
                GlobalHotkeyService.Instance.Unregister();
            }

            if (settings.UseRamHotkey)
            {
                GlobalHotkeyService.Instance.RegisterRamHotkey(_hotkeySource.Handle, settings.RamHotkeyModifiers, settings.RamHotkeyKey);
            }
            else
            {
                GlobalHotkeyService.Instance.UnregisterRamHotkey();
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY)
            {
                int hotkeyId = wParam.ToInt32();
                if (hotkeyId == GlobalHotkeyService.CoolingHotkeyId)
                {
                    PowerPlanService.Instance.ToggleCoolingMode();
                    handled = true;
                }
                else if (hotkeyId == GlobalHotkeyService.RamOptimizeHotkeyId)
                {
                    _ = MemoryOptimizerService.Instance.OptimizeAsync(MemoryOptimizationTrigger.Hotkey);
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private void OnTelemetryUpdated(HardwareSnapshot snapshot)
        {
            _trayManager?.UpdateBadges(
                snapshot.GpuTemp,
                snapshot.CpuTemp,
                snapshot.CoolingModeActive,
                gpuLoad: snapshot.GpuLoad,
                cpuLoad: snapshot.CpuLoad,
                ramPercent: snapshot.RamUsagePercent);
            _temperatureAlertService?.Observe(snapshot);
            _fanHealthService?.Observe(snapshot);
            _storageHealthAlertService?.Observe(snapshot);
        }

        private void OnSettingsChanged()
        {
            var settings = SettingsService.Instance.Current;
            LocalizationService.Instance.Apply(settings.Language);
            ThemeService.Apply(settings.Theme);
            ApplyWindowAccent(PowerPlanService.Instance.IsCoolingModeActive);
            _trayManager?.ApplyLanguage();
            EvaluateBatteryCoolingRule();
            ScheduleAutomaticUpdateCheck();
        }

        private void OnLanguageChanged()
        {
            _trayManager?.ApplyLanguage();
        }

        private void OnThemeChanged(object? sender, EventArgs e)
        {
            _trayManager?.ApplyTheme();
        }

        private void OnFanStopped(FanStoppedEvent alert)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                string fanName = FanHealthService.GetDisplayName(
                    alert.FanKey, alert.FanName, alert.IsGpuFan);
                ShowTrayNotification(
                    LocalizationService.Get("Alert_FanTitle"),
                    LocalizationService.Format("Alert_FanStopped", fanName, alert.LastRpm),
                    () => ShowDashboard()?.OpenSystemInfoDialog(alert.FanKey),
                    System.Windows.Forms.ToolTipIcon.Warning);
            }));
        }

        private void OnStorageHealthAlertRaised(StorageHealthAlert alert)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ShowTrayNotification(
                    LocalizationService.Get("Alert_DriveHealthTitle"),
                    LocalizationService.Format(
                        "Alert_DriveHealthBody",
                        alert.DriveName,
                        alert.HealthPercent,
                        LocalizationService.Health(alert.StatusText)),
                    () => ShowDashboard(),
                    System.Windows.Forms.ToolTipIcon.Warning);
            }));
        }

        private void OnTemperatureAlertRaised(TemperatureAlert alert)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                string current = FormatTemperature(alert.CurrentCelsius);
                string threshold = FormatTemperature(alert.ThresholdCelsius);
                string message = alert.Component switch
                {
                    TemperatureAlertComponent.Cpu => LocalizationService.Format("Alert_Cpu", current, threshold),
                    TemperatureAlertComponent.Gpu => LocalizationService.Format("Alert_Gpu", current, threshold),
                    TemperatureAlertComponent.GpuDelta => LocalizationService.Format(
                        "Alert_GpuDelta",
                        FormatTemperatureDelta(alert.CurrentCelsius),
                        FormatTemperatureDelta(alert.ThresholdCelsius)),
                    TemperatureAlertComponent.GpuHotspot => LocalizationService.Format("Alert_GpuHotspot", current, threshold),
                    TemperatureAlertComponent.GpuVram => LocalizationService.Format("Alert_GpuVram", current, threshold),
                    _ => LocalizationService.Format(
                        "Alert_Drive",
                        alert.DriveName ?? LocalizationService.Format("Alert_DriveName", 1),
                        current,
                        threshold)
                };

                ShowTrayNotification(
                    LocalizationService.Get("Alert_Title"),
                    message,
                    () => ShowDashboard(),
                    System.Windows.Forms.ToolTipIcon.Warning);
            }));
        }

        public void ShowTestNotification()
        {
            ShowTrayNotification(
                LocalizationService.Get("Alert_TestTitle"),
                LocalizationService.Get("Alert_TestBody"),
                null,
                System.Windows.Forms.ToolTipIcon.Info);
        }

        public void ShowTrayNotification(string title, string message, Action? onClick = null,
            System.Windows.Forms.ToolTipIcon icon = System.Windows.Forms.ToolTipIcon.Info)
        {
            if (SettingsService.Instance.Current.DisableAllNotifications)
            {
                return;
            }

            _trayManager?.ShowNotification(title, message, onClick, icon);
        }

        public FanHealthService? FanHealth => _fanHealthService;

        private static string FormatTemperature(double celsius)
        {
            bool isFahrenheit = SettingsService.Instance.Current.TemperatureUnit == "Fahrenheit";
            double value = isFahrenheit ? (celsius * 9 / 5) + 32 : celsius;
            return $"{Math.Round(value)}°{(isFahrenheit ? "F" : "C")}";
        }

        private static string FormatTemperatureDelta(double celsius)
        {
            bool isFahrenheit = SettingsService.Instance.Current.TemperatureUnit == "Fahrenheit";
            double value = isFahrenheit ? celsius * 9 / 5 : celsius;
            return $"{Math.Round(value)}°{(isFahrenheit ? "F" : "C")}";
        }

        private void ScheduleAutomaticUpdateCheck(bool checkNow = false)
        {
            var settings = SettingsService.Instance.Current;
            if (!settings.AutomaticUpdateChecks)
            {
                if (_updateCheckTimer != null)
                {
                    _updateCheckTimer.Stop();
                    _updateCheckTimer.Tick -= UpdateCheckTimer_Tick;
                    _updateCheckTimer = null;
                }
                return;
            }

            TimeSpan interval = _dashboardVisible ? DashboardUpdateInterval : TrayUpdateInterval;
            bool hadTimer = _updateCheckTimer != null;
            if (hadTimer && _updateCheckTimer!.Interval == interval)
            {
                if (checkNow)
                {
                    TryRunAutomaticUpdateCheck(TimeSpan.Zero);
                }
                return;
            }

            if (_updateCheckTimer != null)
            {
                _updateCheckTimer.Stop();
                _updateCheckTimer.Tick -= UpdateCheckTimer_Tick;
            }

            _updateCheckTimer = new DispatcherTimer { Interval = interval };
            _updateCheckTimer.Tick += UpdateCheckTimer_Tick;
            _updateCheckTimer.Start();

            if (checkNow || !hadTimer)
            {
                TryRunAutomaticUpdateCheck(TimeSpan.Zero);
            }
        }

        private void UpdateCheckTimer_Tick(object? sender, EventArgs e)
        {
            TryRunAutomaticUpdateCheck(TimeSpan.Zero);
        }

        private void TryRunAutomaticUpdateCheck(TimeSpan minimumGap)
        {
            if (_isExiting || _updateCheckInProgress)
            {
                return;
            }

            if (!SettingsService.Instance.Current.AutomaticUpdateChecks)
            {
                return;
            }

            if (minimumGap > TimeSpan.Zero &&
                DateTime.UtcNow - _lastAutomaticUpdateCheckUtc < minimumGap)
            {
                return;
            }

            _lastAutomaticUpdateCheckUtc = DateTime.UtcNow;
            _ = RunAutomaticUpdateCheckAsync();
        }

        public void OnDashboardVisibilityChanged(bool visible)
        {
            if (_isExiting || _dashboardVisible == visible)
            {
                return;
            }

            _dashboardVisible = visible;
            ScheduleAutomaticUpdateCheck();
            if (visible)
            {
                TryRunAutomaticUpdateCheck(DashboardUpdateInterval);
            }
        }

        private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
        {
            if (!e.IsAvailable || _isExiting)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
                TryRunAutomaticUpdateCheck(TimeSpan.FromSeconds(30))));
        }

        public void SetAvailableUpdate(ReleaseInfo? release)
        {
            if (string.Equals(AvailableUpdate?.Version, release?.Version, StringComparison.Ordinal))
            {
                return;
            }

            AvailableUpdate = release;
            UpdateAvailabilityChanged?.Invoke();
        }

        public void ConsumeUpdateSuccess()
        {
            PendingUpdateSuccess = null;
            UpdateCompletionService.Clear();
        }

        private async System.Threading.Tasks.Task RunAutomaticUpdateCheckAsync()
        {
            if (_updateCheckInProgress)
            {
                return;
            }

            _updateCheckInProgress = true;
            try
            {
                var release = await UpdateService.Instance.GetLatestReleaseAsync(
                    System.Threading.CancellationToken.None);
                var settings = SettingsService.Instance.Current;

                ReleaseInfo? available = release != null &&
                    UpdateService.IsNewer(release.Version, UpdateService.CurrentVersion)
                    ? release
                    : null;

                SetAvailableUpdate(available);

                if (release == null)
                {
                    UpdateStatusService.Instance.Record(UpdateCheckOutcome.NoReleases, "");
                }
                else if (available == null)
                {
                    UpdateStatusService.Instance.Record(
                        UpdateCheckOutcome.UpToDate, UpdateService.CurrentVersion);
                }
                else
                {
                    UpdateStatusService.Instance.Record(
                        UpdateCheckOutcome.Available, available.Version);
                }

                bool notified = false;
                if (available != null &&
                    !string.Equals(settings.LastNotifiedUpdateVersion, available.Version, StringComparison.Ordinal) &&
                    !settings.DisableAllNotifications)
                {
                    settings.LastNotifiedUpdateVersion = available.Version;
                    notified = true;
                    ShowTrayNotification(
                        LocalizationService.Get("Update_NotificationTitle"),
                        LocalizationService.Format("Update_NotificationBody", available.Version),
                        () => ShowDashboard()?.OpenSettingsDialog(true, available, false),
                        System.Windows.Forms.ToolTipIcon.Info);
                }

                if (notified)
                {
                    SettingsService.Instance.Save();
                }
            }
            catch (Exception ex)
            {
                UpdateStatusService.Instance.Record(
                    UpdateCheckOutcome.Failed, string.Empty, ex.Message);
            }
            finally
            {
                _updateCheckInProgress = false;
            }
        }

        public void TrimWorkingSet()
        {
            if (_isExiting)
            {
                return;
            }

            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();

            using var process = Process.GetCurrentProcess();
            NativeMethods.EmptyWorkingSet(process.Handle);
        }

        private void ExitApplication()
        {
            if (!ConfirmExitWithCoolingMode())
            {
                return;
            }

            _isExiting = true;
            _startupTrimTimer?.Stop();
            _startupTrimTimer = null;
            _powerPlanWatchTimer?.Stop();
            _powerPlanWatchTimer = null;
            _coolingPauseTimer?.Stop();
            _coolingPauseTimer = null;
            CoolingPausedUntilUtc = null;
            _updateCheckTimer?.Stop();
            _updateCheckTimer = null;

            GlobalHotkeyService.Instance.Unregister();
            GlobalHotkeyService.Instance.UnregisterRamHotkey();
            SystemEvents.PowerModeChanged -= OnSystemPowerModeChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            PowerPlanService.Instance.CoolingModeChanged -= OnPowerPlanCoolingChanged;
            UnregisterActivationWaitHandle();
            AutopilotService.Instance.ExternalPauseHoldCheck = null;
            SettingsService.Instance.SettingsChanged -= RegisterHotkeyIfEnabled;
            SettingsService.Instance.SettingsChanged -= OnSettingsChanged;
            LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
            if (_temperatureAlertService != null)
            {
                _temperatureAlertService.AlertRaised -= OnTemperatureAlertRaised;
                _temperatureAlertService = null;
            }

            if (_fanHealthService != null)
            {
                _fanHealthService.FanStopped -= OnFanStopped;
                _fanHealthService = null;
            }

            if (_storageHealthAlertService != null)
            {
                _storageHealthAlertService.AlertRaised -= OnStorageHealthAlertRaised;
                _storageHealthAlertService = null;
            }

            if (_hotkeySource != null)
            {
                _hotkeySource.RemoveHook(HwndHook);
                _hotkeySource.Dispose();
                _hotkeySource = null;
            }

            var overlay = _overlayWindow;
            _overlayWindow = null;
            overlay?.Close();

            var main = _mainWindow;
            _mainWindow = null;
            main?.Close();

            MemoryOptimizerService.Instance.Stop();
            AutopilotService.Instance.Stop();
            HardwareMonitorService.Instance.Stop();
            TelemetryCsvService.Instance.Dispose();
            _trayManager?.Dispose();
            _trayManager = null;
            ReleaseSingleInstanceMutex();
            Shutdown();
        }

        private void ReleaseSingleInstanceMutex()
        {
            if (_mutexReleased)
            {
                return;
            }

            _mutexReleased = true;
            var mutex = _singleInstanceMutex;
            _singleInstanceMutex = null;
            if (mutex == null)
            {
                return;
            }

            try
            {
                if (_isPrimaryInstance)
                {
                    mutex.ReleaseMutex();
                }
            }
            catch
            {
            }

            mutex.Dispose();
        }

        private bool ConfirmExitWithCoolingMode()
        {
            var settings = SettingsService.Instance.Current;
            if (!settings.DisableCoolingModeOnExit ||
                !PowerPlanService.Instance.IsCoolingModeActive)
            {
                return true;
            }

            var owner = _mainWindow != null &&
                        _mainWindow.IsVisible &&
                        _mainWindow.WindowState != WindowState.Minimized
                ? _mainWindow
                : null;
            owner?.SetExitConfirmationOpen(true);

            try
            {
                var dialog = new ConfirmExitWindow();
                if (owner != null)
                {
                    dialog.Owner = owner;
                    dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }

                if (dialog.ShowDialog() != true)
                {
                    return false;
                }
            }
            finally
            {
                owner?.SetExitConfirmationOpen(false);
            }

            DisableCoolingForExit();
            return true;
        }

        private void DisableCoolingForExit()
        {
            if (!_isPrimaryInstance)
            {
                return;
            }

            try
            {
                if (SettingsService.Instance.Current.DisableCoolingModeOnExit &&
                    PowerPlanService.Instance.IsCoolingModeActive)
                {
                    PowerPlanService.Instance.SetCoolingMode(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to disable Cooling Mode on exit: {ex.Message}");
            }
        }

        public const string DefaultCoolingOnFrameColor = "#2B88D8";
        public const string DefaultCoolingOffFrameColor = "#C26A47";

        internal static Color ResolveFrameColor(string? configuredHex, string fallbackHex)
        {
            if (!string.IsNullOrWhiteSpace(configuredHex))
            {
                try
                {
                    if (ColorConverter.ConvertFromString(configuredHex.Trim()) is Color parsed)
                    {
                        return Color.FromRgb(parsed.R, parsed.G, parsed.B);
                    }
                }
                catch
                {
                }
            }

            var fallback = (Color)ColorConverter.ConvertFromString(fallbackHex);
            return Color.FromRgb(fallback.R, fallback.G, fallback.B);
        }

        internal static string NormalizeHexColor(Color color) =>
            $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        private void ApplyWindowAccent(bool isCooling)
        {
            var settings = SettingsService.Instance.Current;
            Color color = isCooling
                ? ResolveFrameColor(settings.CoolingOnFrameColor, DefaultCoolingOnFrameColor)
                : ResolveFrameColor(settings.CoolingOffFrameColor, DefaultCoolingOffFrameColor);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Resources["WindowBorderBrush"] = brush;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            DisableCoolingForExit();
            _isExiting = true;
            _startupTrimTimer?.Stop();
            _powerPlanWatchTimer?.Stop();
            _coolingPauseTimer?.Stop();
            _coolingPauseTimer = null;
            CoolingPausedUntilUtc = null;
            _updateCheckTimer?.Stop();
            _updateCheckTimer = null;

            HardwareMonitorService.Instance.TelemetryUpdated -= OnTelemetryUpdated;
            PowerPlanService.Instance.CoolingModeChanged -= ApplyWindowAccent;
            PowerPlanService.Instance.CoolingModeChanged -= OnPowerPlanCoolingChanged;
            SystemEvents.PowerModeChanged -= OnSystemPowerModeChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            SettingsService.Instance.SettingsChanged -= RegisterHotkeyIfEnabled;
            SettingsService.Instance.SettingsChanged -= OnSettingsChanged;
            LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
            ThemeService.ThemeChanged -= OnThemeChanged;
            ThemeService.Shutdown();
            UnregisterActivationWaitHandle();
            AutopilotService.Instance.ExternalPauseHoldCheck = null;

            if (_hotkeySource != null)
            {
                _hotkeySource.RemoveHook(HwndHook);
                _hotkeySource.Dispose();
                _hotkeySource = null;
            }

            GlobalHotkeyService.Instance.Unregister();
            GlobalHotkeyService.Instance.UnregisterRamHotkey();
            _trayManager?.Dispose();
            MemoryOptimizerService.Instance.Dispose();
            TelemetryCsvService.Instance.Dispose();
            AutopilotService.Instance.Dispose();
            HardwareMonitorService.Instance.Dispose();
            ReleaseSingleInstanceMutex();
            base.OnExit(e);
        }
    }
}
