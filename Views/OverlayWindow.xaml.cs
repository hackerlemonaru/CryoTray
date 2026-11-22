using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CryoTray.Models;
using CryoTray.Services;

namespace CryoTray.Views
{
    public partial class OverlayWindow : Window
    {
        private int _appliedOpacityPercent = -1;
        private bool _isClosing;

        public OverlayWindow()
        {
            InitializeComponent();
            LocalizationService.Instance.ApplyWindow(this);
            Loaded += OverlayWindow_Loaded;
            Closing += OverlayWindow_Closing;
            Closed += OverlayWindow_Closed;
            LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
            ThemeService.ThemeChanged += OnThemeChanged;
        }

        private void OverlayWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var s = SettingsService.Instance.Current;
            if (s.OverlayLeft.HasValue && s.OverlayTop.HasValue)
            {
                Left = s.OverlayLeft.Value;
                Top = s.OverlayTop.Value;
            }
            else
            {
                Left = SystemParameters.WorkArea.Right - Width - 30;
                Top = 30;
            }

            ClampToWorkArea();
            ApplyMetricVisibility(s);
            ApplyClickThrough(s.OverlayClickThrough);
            UpdateTelemetry(HardwareMonitorService.Instance.CurrentSnapshot);
            HardwareMonitorService.Instance.TelemetryUpdated += OnTelemetryUpdated;
            SettingsService.Instance.SettingsChanged += OnSettingsChanged;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyClickThrough(SettingsService.Instance.Current.OverlayClickThrough);
        }

        /// <summary>Toggles Win32 WS_EX_TRANSPARENT so clicks pass straight through the overlay.</summary>
        public void ApplyClickThrough(bool enabled)
        {
            BtnCloseOverlay.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            int targetStyle = enabled
                ? exStyle | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT
                : (exStyle | NativeMethods.WS_EX_LAYERED) & ~NativeMethods.WS_EX_TRANSPARENT;

            if (targetStyle != exStyle)
            {
                NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, targetStyle);
            }
        }

        private void OverlayWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _isClosing = true;
            SettingsService.Instance.Current.OverlayLeft = Left;
            SettingsService.Instance.Current.OverlayTop = Top;
            SettingsService.Instance.Save();
        }

        private void OverlayWindow_Closed(object? sender, EventArgs e)
        {
            _isClosing = true;
            Loaded -= OverlayWindow_Loaded;
            Closing -= OverlayWindow_Closing;
            Closed -= OverlayWindow_Closed;
            LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
            ThemeService.ThemeChanged -= OnThemeChanged;
            HardwareMonitorService.Instance.TelemetryUpdated -= OnTelemetryUpdated;
            SettingsService.Instance.SettingsChanged -= OnSettingsChanged;
            DataContext = null;
            Content = null;
        }

        private void ClampToWorkArea()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            Rect workArea = WindowPlacementHelper.GetWorkAreaForSavedPositionDips(
                Left, Top, Width, Height, dpi.DpiScaleX, dpi.DpiScaleY);
            var (clampedLeft, clampedTop) = WindowPlacementHelper.ClampToWorkArea(
                workArea, Left, Top, Width, Height, margin: 8);
            Left = clampedLeft;
            Top = clampedTop;
        }

        public void UpdateTelemetry(HardwareSnapshot snapshot)
        {
            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (Dispatcher.CheckAccess())
            {
                ApplyTelemetryCore(snapshot);
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isClosing)
                {
                    ApplyTelemetryCore(snapshot);
                }
            }));
        }

        private void ApplyTelemetryCore(HardwareSnapshot snapshot)
        {
            var s = SettingsService.Instance.Current;
            bool isFahrenheit = s.TemperatureUnit == "Fahrenheit";
            string unit = isFahrenheit ? "°F" : "°C";

            ApplyMetricVisibility(s);

            if (double.IsFinite(snapshot.CpuTemp) && snapshot.CpuTemp > 0)
            {
                double cpuT = isFahrenheit ? (snapshot.CpuTemp * 9 / 5 + 32) : snapshot.CpuTemp;
                TxtCpuTemp.Text = $"{Math.Round(cpuT)}{unit}";
            }
            else
            {
                TxtCpuTemp.Text = $"--{unit}";
            }

            TxtCpuTemp.Foreground = snapshot.CoolingModeActive
                ? ThemeService.GetBrush("CoolingOnTextBrush")
                : ThemeService.GetBrush("CoolingOffTextBrush");
            TxtCpuLoad.Text = double.IsFinite(snapshot.CpuLoad) && snapshot.CpuLoad >= 0
                ? $"{Math.Round(snapshot.CpuLoad)}%"
                : "--%";
            TxtCpuPower.Text = snapshot.CpuPackageWatts.HasValue
                ? $"{snapshot.CpuPackageWatts.Value}W"
                : "--W";
            TxtCpuClock.Text = double.IsFinite(snapshot.CpuGhz) && snapshot.CpuGhz > 0
                ? $"{snapshot.CpuGhz:0.00}G"
                : "--G";

            if (double.IsFinite(snapshot.GpuTemp) && snapshot.GpuTemp > 0)
            {
                double gpuT = isFahrenheit ? (snapshot.GpuTemp * 9 / 5 + 32) : snapshot.GpuTemp;
                TxtGpuTemp.Text = $"{Math.Round(gpuT)}{unit}";
            }
            else
            {
                TxtGpuTemp.Text = $"--{unit}";
            }

            TxtGpuLoad.Text = double.IsFinite(snapshot.GpuLoad) && snapshot.GpuLoad >= 0
                ? $"{Math.Round(snapshot.GpuLoad)}%"
                : "--%";
            TxtGpuPower.Text = snapshot.GpuPowerWatts.HasValue
                ? $"{snapshot.GpuPowerWatts.Value}W"
                : "--W";
            TxtRamUsed.Text = double.IsFinite(snapshot.RamUsedGb) && snapshot.RamUsedGb > 0
                ? $"{snapshot.RamUsedGb:0.0}G"
                : "--G";
            TxtRamPercent.Text = double.IsFinite(snapshot.RamUsagePercent) && snapshot.RamUsagePercent >= 0
                ? $"{Math.Round(snapshot.RamUsagePercent)}%"
                : "--%";

            var battery = BatteryTelemetryService.Read();
            if (battery.HasBattery)
            {
                TxtBatteryPercent.Text = $"{battery.Percent}%";
                TxtBatteryState.Text = BatteryTelemetryService.GetStateLabel(battery);
            }
            else
            {
                TxtBatteryPercent.Text = "--%";
                TxtBatteryState.Text = "";
            }
        }

        private void ApplyMetricVisibility(AppSettings settings)
        {
            bool showCpu = settings.OverlayShowCpuTemp ||
                settings.OverlayShowCpuLoad ||
                settings.OverlayShowCpuPower ||
                settings.OverlayShowCpuClock;
            bool showGpu = settings.OverlayShowGpuTemp ||
                settings.OverlayShowGpuLoad ||
                settings.OverlayShowGpuPower;
            bool showRam = settings.OverlayShowRam;
            var battery = BatteryTelemetryService.Read();
            bool showBattery = settings.ShowBatteryStatus && battery.HasBattery;

            CpuOverlayGroup.Visibility = showCpu
                ? Visibility.Visible
                : Visibility.Collapsed;
            GpuOverlayGroup.Visibility = showGpu
                ? Visibility.Visible
                : Visibility.Collapsed;
            RamOverlayGroup.Visibility = showRam
                ? Visibility.Visible
                : Visibility.Collapsed;
            RamOverlayGroup.Margin = (showCpu || showGpu) && showRam
                ? new Thickness(12, 0, 0, 0)
                : new Thickness(0);
            BatteryOverlayGroup.Visibility = showBattery
                ? Visibility.Visible
                : Visibility.Collapsed;
            BatteryOverlayGroup.Margin = (showCpu || showGpu || showRam) && showBattery
                ? new Thickness(12, 0, 0, 0)
                : new Thickness(0);

            double targetWidth = showCpu && showGpu && showRam ? 430 : 360;
            if (showBattery)
            {
                targetWidth += 70;
            }
            if (Math.Abs(Width - targetWidth) > 0.5)
            {
                Width = targetWidth;
                ClampToWorkArea();
            }

            TxtCpuTemp.Visibility = settings.OverlayShowCpuTemp
                ? Visibility.Visible
                : Visibility.Collapsed;
            TxtCpuLoad.Visibility = settings.OverlayShowCpuLoad
                ? Visibility.Visible
                : Visibility.Collapsed;
            TxtCpuPower.Visibility = settings.OverlayShowCpuPower
                ? Visibility.Visible
                : Visibility.Collapsed;
            TxtCpuClock.Visibility = settings.OverlayShowCpuClock
                ? Visibility.Visible
                : Visibility.Collapsed;

            TxtGpuTemp.Visibility = settings.OverlayShowGpuTemp
                ? Visibility.Visible
                : Visibility.Collapsed;
            TxtGpuLoad.Visibility = settings.OverlayShowGpuLoad
                ? Visibility.Visible
                : Visibility.Collapsed;
            TxtGpuPower.Visibility = settings.OverlayShowGpuPower
                ? Visibility.Visible
                : Visibility.Collapsed;

            TxtOverlayEmpty.Visibility = showCpu || showGpu || showRam || showBattery
                ? Visibility.Collapsed
                : Visibility.Visible;

            ApplySurfaceOpacity(settings);
        }

        private void ApplySurfaceOpacity(AppSettings settings)
        {
            int percent = Math.Clamp(settings.OverlayOpacityPercent, 0, 100);
            if (percent == _appliedOpacityPercent)
            {
                return;
            }

            _appliedOpacityPercent = percent;
            byte alpha = (byte)Math.Round(percent * 255.0 / 100.0);
            Color baseColor = ThemeService.GetColor("OverlayBackgroundBrush");
            var brush = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
            brush.Freeze();
            OverlaySurface.Background = brush;
        }

        private void OnTelemetryUpdated(HardwareSnapshot snapshot)
        {
            UpdateTelemetry(snapshot);
        }

        private void OnThemeChanged(object? sender, EventArgs e)
        {
            if (!IsLoaded || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _appliedOpacityPercent = -1;
                ApplySurfaceOpacity(SettingsService.Instance.Current);
            }));
        }

        private void OnSettingsChanged()
        {
            if (!IsLoaded)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                var current = SettingsService.Instance.Current;
                ApplyMetricVisibility(current);
                ApplyClickThrough(current.OverlayClickThrough);
            }));
        }

        private void OnLanguageChanged()
        {
            if (Dispatcher.CheckAccess())
            {
                LocalizationService.Instance.ApplyWindow(this);
            }
            else if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                Dispatcher.BeginInvoke(new Action(() => LocalizationService.Instance.ApplyWindow(this)));
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
