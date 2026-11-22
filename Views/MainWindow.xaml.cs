using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using CryoTray.Models;
using CryoTray.Services;
using CryoTray.Tray;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace CryoTray.Views
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<CoreReadout> CoreItems { get; } = new();

        private readonly Storyboard _fanStoryboard = new();
        private DoubleAnimation? _fanAnimation;
        private bool _fanRunning;
        private int _timeframeSamples = 60; // Default 1 min
        private int _coreColumns = 4;
        private bool _isClosing;
        private bool _isExitConfirmationOpen;
        private bool _showKeyboardFocusCues;
        private bool _isApplyingSettings;
        private ReleaseInfo? _visibleUpdate;
        private DispatcherTimer? _updateSuccessTimer;
        private DispatcherTimer? _updateSnoozeTimer;
        private readonly double _expandedHeight;
        private const double BaselineSsdSectionHeight = 79;

        private List<TelemetrySample> _cpuChartSamples = new();
        private List<TelemetrySample> _gpuChartSamples = new();
        private double _gpuChartMinimum = 30;
        private double _gpuChartMaximum = 90;
        private string _gpuChartMetric = "Temperature";
        private string? _gpuChartGpuIdentifier;
        private bool _cpuHoverActive;
        private bool _cpuHoverSmall;
        private bool _gpuHoverActive;
        private double _cpuHoverX;
        private double _gpuHoverX;
        private SsdRowView? _activeHoverDriveRow;

        private readonly List<SsdRowView> _ssdRows = new();
        private string? _ssdRowSignature;
        private DispatcherTimer? _ramFeedbackTimer;
        private bool _ramTooltipPinned;

        private static Brush SsdHealthGoodBrush => ThemeService.GetBrush("SuccessTextBrush");
        private static Brush SsdHealthFairBrush => ThemeService.GetBrush("WarningTextBrush");
        private static Brush SsdHealthWarnBrush => ThemeService.GetBrush("DangerTextBrush");
        private static Brush RamFooterMutedBrush => ThemeService.GetBrush("RamFooterMutedBrush");
        private static Brush RamFooterTextBrush => ThemeService.GetBrush("RamFooterTextBrush");
        private static Brush RamAccentBrush => ThemeService.GetBrush("RamAccentBrush");
        private static Brush RamWarnBrush => ThemeService.GetBrush("WarningTextBrush");

        private static Brush CreateFrozenBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private sealed class SsdRowView
        {
            public required string DriveKey { get; init; }
            public required bool ShowGraph { get; init; }
            public required Grid Root { get; init; }
            public required TextBlock BadgeText { get; init; }
            public required TextBlock RowTitleText { get; init; }
            public required TextBlock HealthText { get; init; }
            public required TextBlock TooltipTitle { get; init; }
            public required TextBlock TooltipHealth { get; init; }
            public Border? TempBorder { get; init; }
            public TextBlock? TempText { get; init; }
            public TextBlock? UnitText { get; init; }
            public Canvas? ChartCanvas { get; init; }
            public Canvas? ChartHoverCanvas { get; init; }
            public TextBlock? ChartUnavailableText { get; init; }
            public List<TelemetrySample> ChartSamples { get; } = new();
            public bool HoverActive { get; set; }
            public double HoverX { get; set; }
        }

        public int CoreColumns
        {
            get => _coreColumns;
            private set
            {
                if (_coreColumns == value)
                {
                    return;
                }

                _coreColumns = value;
                OnPropertyChanged();
            }
        }

        public bool ShowKeyboardFocusCues
        {
            get => _showKeyboardFocusCues;
            private set
            {
                if (_showKeyboardFocusCues == value)
                {
                    return;
                }

                _showKeyboardFocusCues = value;
                OnPropertyChanged();
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            _expandedHeight = Height;
            LocalizationService.Instance.ApplyWindow(this);
            DataContext = this;
            CpuCoresPopup.Opened += CpuCoresPopup_Opened;
            RamBreakdownToolTip.PlacementTarget = BtnRamBreakdown;
            SetupFanAnimation();

            Loaded += MainWindow_Loaded;
            IsVisibleChanged += MainWindow_IsVisibleChanged;
            Activated += MainWindow_Activated;
            Deactivated += MainWindow_Deactivated;
            StateChanged += MainWindow_StateChanged;
            Closing += MainWindow_Closing;
            Closed += MainWindow_Closed;
        }

        private void SetupFanAnimation()
        {
            _fanAnimation = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromSeconds(3.5),
                RepeatBehavior = RepeatBehavior.Forever
            };

            // The fan is decorative; 30 fps keeps it smooth with less render work.
            Timeline.SetDesiredFrameRate(_fanAnimation, 30);
            Storyboard.SetTarget(_fanAnimation, FanRotor);
            Storyboard.SetTargetProperty(_fanAnimation, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
            _fanStoryboard.Children.Add(_fanAnimation);
        }

        private void MainWindow_Activated(object? sender, EventArgs e)
        {
            StartFanAnimation();
            if (IsLoaded)
            {
                UpdateTelemetryUI(HardwareMonitorService.Instance.CurrentSnapshot);
            }
        }

        private void MainWindow_Deactivated(object? sender, EventArgs e)
        {
            StopFanAnimation();
        }

        private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            (System.Windows.Application.Current as App)?.OnDashboardVisibilityChanged(IsVisible);

            if (IsVisible)
            {
                StartFanAnimation();
                if (IsLoaded)
                {
                    UpdateTelemetryUI(HardwareMonitorService.Instance.CurrentSnapshot);
                }
            }
            else
            {
                StopFanAnimation();
            }
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                StopFanAnimation();
                return;
            }

            StartFanAnimation();
            if (IsLoaded)
            {
                UpdateTelemetryUI(HardwareMonitorService.Instance.CurrentSnapshot);
            }
        }

        private bool CanAnimateFan =>
            IsVisible && IsActive && WindowState != WindowState.Minimized;

        private void StartFanAnimation()
        {
            if (_fanRunning || _fanAnimation == null || !CanAnimateFan)
            {
                return;
            }

            _fanStoryboard.Begin();
            _fanRunning = true;
        }

        private void StopFanAnimation()
        {
            if (!_fanRunning)
            {
                return;
            }

            _fanStoryboard.Stop();
            _fanRunning = false;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ApplySettings();
            if (System.Windows.Application.Current is App app)
            {
                app.UpdateAvailabilityChanged += RefreshUpdateBanner;
            }
            UpdateStatusService.Instance.Changed += OnUpdateStatusChanged;
            RefreshUpdateBanner();
            RefreshSuccessBanner();
            SettingsService.Instance.SettingsChanged += ApplySettings;
            LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
            ThemeService.ThemeChanged += OnThemeChanged;

            PowerPlanService.Instance.CoolingModeChanged += OnCoolingModeChanged;
            HardwareMonitorService.Instance.TelemetryUpdated += OnTelemetryUpdated;
            MemoryOptimizerService.Instance.OptimizingStateChanged += OnRamOptimizingStateChanged;
            MemoryOptimizerService.Instance.OptimizationCompleted += OnRamOptimizationCompleted;

            UpdateTheme(PowerPlanService.Instance.IsCoolingModeActive, false);
            UpdateTelemetryUI(HardwareMonitorService.Instance.CurrentSnapshot);
            SetRamOptimizingVisualState(MemoryOptimizerService.Instance.IsOptimizing);
            StartFanAnimation();
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _isClosing = true;
            HideChartTooltips();
            CloseRamBreakdownToolTip();
            if (RamScopeMenu != null)
            {
                RamScopeMenu.IsOpen = false;
            }
            if (ToolsContextMenu != null)
            {
                ToolsContextMenu.IsOpen = false;
            }
            CpuCoresPopup.IsOpen = false;
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            Loaded -= MainWindow_Loaded;
            IsVisibleChanged -= MainWindow_IsVisibleChanged;
            Activated -= MainWindow_Activated;
            Deactivated -= MainWindow_Deactivated;
            StateChanged -= MainWindow_StateChanged;
            CpuCoresPopup.Opened -= CpuCoresPopup_Opened;
            if (System.Windows.Application.Current is App app)
            {
                app.UpdateAvailabilityChanged -= RefreshUpdateBanner;
            }
            UpdateStatusService.Instance.Changed -= OnUpdateStatusChanged;
            _updateSnoozeTimer?.Stop();
            _updateSnoozeTimer = null;
            if (UpdateSuccessBanner.Visibility == Visibility.Visible)
            {
                HideUpdateSuccessBanner(consume: true);
            }
            else
            {
                _updateSuccessTimer?.Stop();
                _updateSuccessTimer = null;
            }
            SettingsService.Instance.SettingsChanged -= ApplySettings;
            LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
            ThemeService.ThemeChanged -= OnThemeChanged;
            PowerPlanService.Instance.CoolingModeChanged -= OnCoolingModeChanged;
            HardwareMonitorService.Instance.TelemetryUpdated -= OnTelemetryUpdated;
            MemoryOptimizerService.Instance.OptimizingStateChanged -= OnRamOptimizingStateChanged;
            MemoryOptimizerService.Instance.OptimizationCompleted -= OnRamOptimizationCompleted;
            _ramFeedbackTimer?.Stop();
            _ramFeedbackTimer = null;
            OptGlyphHost?.BeginAnimation(OpacityProperty, null);
            StopFanAnimation();
            DataContext = null;
            Content = null;
        }

        public void ApplySettings()
        {
            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (Dispatcher.CheckAccess())
            {
                ApplySettingsCore();
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isClosing)
                {
                    ApplySettingsCore();
                }
            }));
        }

        private void ApplySettingsCore()
        {
            var s = SettingsService.Instance.Current;
            _isApplyingSettings = true;
            try
            {
                TxtAppTitle.Text = s.ShowComputerName
                    ? LocalizationService.Format("Main_AppWithPc", Environment.MachineName)
                    : LocalizationService.Get("App_Title");

                TxtCpuModel.TextAlignment = LocalizationService.Instance.IsArabic
                    ? TextAlignment.Right
                    : TextAlignment.Left;

                Topmost = s.AlwaysOnTop;
                ShowInTaskbar = s.PinToTaskbar;

                _timeframeSamples = TimeframeMinutesToSamples(s.CpuGraphTimeframeMinutes);
                int targetIdx = TimeframeMinutesToIndex(s.CpuGraphTimeframeMinutes);
                if (CmbTimeframe != null && CmbTimeframe.SelectedIndex != targetIdx)
                {
                    CmbTimeframe.SelectedIndex = targetIdx;
                }

                ApplyTelemetryVisibility(s);
                _ssdRowSignature = null;

                RamRow.Visibility = s.ShowRamRow
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                RamSeparator.Visibility = RamRow.Visibility;
                OverlayRow.Visibility = s.ShowOverlayRow
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                AutopilotRow.Visibility = s.ShowAutopilotRow
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                AutopilotSeparator.Visibility = s.ShowOverlayRow && s.ShowAutopilotRow
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                bool isFahrenheit = s.TemperatureUnit == "Fahrenheit";
                string unit = isFahrenheit ? "°F" : "°C";
                RenderSsdRows(HardwareMonitorService.Instance.CurrentSnapshot.StorageDrives, isFahrenheit, unit);
                UpdateWindowHeight(s.ShowOverlayRow, s.ShowAutopilotRow);
                DrawCpuChart();
            }
            finally
            {
                _isApplyingSettings = false;
            }
        }

        private void UpdateWindowHeight(bool showOverlay, bool showAutopilot)
        {
            var s = SettingsService.Instance.Current;
            double height = _expandedHeight;

            if (!s.ShowCpuGraph)
            {
                height -= 80;
            }
            else if (s.CpuGraphSmall)
            {
                height -= 80;
                height += CpuSmallChartRow.Height
                    + CpuSmallChartSeparator.Height
                    + CpuSmallChartSeparator.Margin.Top
                    + CpuSmallChartSeparator.Margin.Bottom;
            }

            if (CpuSpeedRow.Visibility != Visibility.Visible)
            {
                height -= CpuSpeedRow.Height
                    + CpuSpeedSeparator.Height
                    + CpuSpeedSeparator.Margin.Top
                    + CpuSpeedSeparator.Margin.Bottom;
            }

            if (RamRow.Visibility != Visibility.Visible)
            {
                height -= RamRow.Height
                    + RamSeparator.Height
                    + RamSeparator.Margin.Top
                    + RamSeparator.Margin.Bottom;
            }
            if (!showOverlay)
            {
                height -= OverlayRow.Height;
            }
            if (!showAutopilot)
            {
                height -= AutopilotRow.Height;
            }
            if (!showOverlay || !showAutopilot)
            {
                height -= AutopilotSeparator.Height
                    + AutopilotSeparator.Margin.Top
                    + AutopilotSeparator.Margin.Bottom;
            }
            if (GpuHealthRow.Visibility != Visibility.Visible)
            {
                height -= GpuHealthRow.Height;
            }
            if (s.ShowGpuDetails)
            {
                height += GpuDetailsRow.Height;
            }

            double actualSsdHeight;
            if (!s.ShowDiskTemp || SsdRowsContainer.Visibility != Visibility.Visible || SsdRowsContainer.Children.Count == 0)
            {
                actualSsdHeight = 0;
            }
            else if (_ssdRows.Count == 0)
            {
                actualSsdHeight = 38;
            }
            else
            {
                actualSsdHeight = _ssdRows.Sum(r => r.Root.Height) + 3;
            }
            height += actualSsdHeight - BaselineSsdSectionHeight;

            if (PawnIoBanner.Visibility == Visibility.Visible)
            {
                height += PawnIoBanner.Height;
            }
            if (UpdateBanner.Visibility == Visibility.Visible)
            {
                height += UpdateBanner.Height;
            }
            if (UpdateSuccessBanner.Visibility == Visibility.Visible)
            {
                height += UpdateSuccessBanner.Height;
            }

            Rect monitorWorkArea = WindowPlacementHelper.GetMonitorWorkAreaDips(this);
            double maxWorkHeight = Math.Max(260, monitorWorkArea.Height - 24);
            height = Math.Clamp(height, 260, maxWorkHeight);

            if (Math.Abs(Height - height) < 0.1)
            {
                return;
            }

            Height = height;
            if (s.AnchorToTray)
            {
                Top = monitorWorkArea.Bottom - Height - 12;
            }
        }

        public void PositionAboveTray()
        {
            var s = SettingsService.Instance.Current;
            if (!s.AnchorToTray && s.WindowLeft.HasValue && s.WindowTop.HasValue)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                Rect savedMonitor = WindowPlacementHelper.GetWorkAreaForSavedPositionDips(
                    s.WindowLeft.Value,
                    s.WindowTop.Value,
                    Width,
                    Height,
                    dpi.DpiScaleX,
                    dpi.DpiScaleY);
                var (clampedLeft, clampedTop) = WindowPlacementHelper.ClampToWorkArea(
                    savedMonitor,
                    s.WindowLeft.Value,
                    s.WindowTop.Value,
                    Width,
                    Height,
                    margin: 8);
                Left = clampedLeft;
                Top = clampedTop;
                return;
            }

            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 12;
            Top = workArea.Bottom - Height - 12;
        }

        private SettingsWindow? _settingsWindow;
        private SystemInfoWindow? _systemInfoWindow;
        private StressTestWindow? _stressTestWindow;
        private bool _isChildDialogOpen;

        private void Window_Deactivated(object? sender, EventArgs e)
        {
            if (_isChildDialogOpen || _isClosing || _isExitConfirmationOpen) return;

            var s = SettingsService.Instance.Current;
            if (!s.HideOnFocusLoss || s.AlwaysOnTop) return;

            HideChartTooltips();
            CloseRamBreakdownToolTip();
            if (RamScopeMenu != null)
            {
                RamScopeMenu.IsOpen = false;
            }
            if (ToolsContextMenu != null)
            {
                ToolsContextMenu.IsOpen = false;
            }
            Close();
        }

        private void OnCoolingModeChanged(bool isCooling)
        {
            Dispatcher.Invoke(() => UpdateTheme(isCooling, true));
        }

        private void UpdateTheme(bool isCooling, bool animate)
        {
            Color target1 = (Color)ColorConverter.ConvertFromString(isCooling ? "#255D8E" : "#9E4531");
            Color target2 = (Color)ColorConverter.ConvertFromString(isCooling ? "#1E9FD0" : "#C26A45");

            if (animate)
            {
                var anim1 = new ColorAnimation(target1, TimeSpan.FromMilliseconds(400));
                var anim2 = new ColorAnimation(target2, TimeSpan.FromMilliseconds(400));
                GradStop1.BeginAnimation(GradientStop.ColorProperty, anim1);
                GradStop2.BeginAnimation(GradientStop.ColorProperty, anim2);
            }
            else
            {
                GradStop1.Color = target1;
                GradStop2.Color = target2;
            }

            ApplyFanTheme(isCooling);

            if (isCooling)
            {
                // Toggle Switch state: ON (Blue track, white thumb)
                TogglePillBorder.Width = LocalizationService.Instance.IsArabic ? 68 : 48;
                TogglePillBorder.Background = ThemeService.GetBrush("AccentBrush");
                TxtToggleState.Text = LocalizationService.Get("Common_On");
                TxtToggleState.Foreground = Brushes.White;
                TxtToggleState.HorizontalAlignment = HorizontalAlignment.Left;
                TxtToggleState.Margin = new Thickness(7, 0, 0, 0);

                ToggleThumb.Fill = Brushes.White;
                ToggleThumb.HorizontalAlignment = HorizontalAlignment.Right;
                ToggleThumb.Margin = new Thickness(0, 0, 0, 0);
            }
            else
            {
                // Toggle Switch state: OFF (Brown track, coral thumb)
                TogglePillBorder.Width = LocalizationService.Instance.IsArabic ? 68 : 48;
                TogglePillBorder.Background = ThemeService.GetBrush("ToggleOffTrackBrush");
                TxtToggleState.Text = LocalizationService.Get("Common_Off");
                TxtToggleState.Foreground = ThemeService.GetBrush("ToggleOffTextBrush");
                TxtToggleState.HorizontalAlignment = HorizontalAlignment.Right;
                TxtToggleState.Margin = new Thickness(0, 0, 7, 0);

                ToggleThumb.Fill = ThemeService.GetBrush("ToggleOffThumbBrush");
                ToggleThumb.HorizontalAlignment = HorizontalAlignment.Left;
                ToggleThumb.Margin = new Thickness(0, 0, 0, 0);
            }

            DrawCpuChart();
        }

        private void ApplyFanTheme(bool isCooling)
        {
            Color accent = (Color)ColorConverter.ConvertFromString(isCooling ? "#35D7FF" : "#F08A4B");
            Color ring = (Color)ColorConverter.ConvertFromString(isCooling ? "#1A7A99" : "#8A4728");
            Color circuits = (Color)ColorConverter.ConvertFromString(isCooling ? "#1F9FC4" : "#C56B3C");
            Color dots = (Color)ColorConverter.ConvertFromString(isCooling ? "#8FF4FF" : "#FFD0A3");
            Color tileTop = (Color)ColorConverter.ConvertFromString(isCooling ? "#071A2B" : "#1A0E0A");
            Color tileMiddle = (Color)ColorConverter.ConvertFromString(isCooling ? "#0B2A40" : "#2A160F");
            Color tileBottom = (Color)ColorConverter.ConvertFromString(isCooling ? "#071722" : "#140B08");
            Color petalTop = (Color)ColorConverter.ConvertFromString(isCooling ? "#E9FDFF" : "#FFE3B5");
            Color petalMiddle = (Color)ColorConverter.ConvertFromString(isCooling ? "#9DE8FF" : "#FFAD5C");
            Color petalBottom = (Color)ColorConverter.ConvertFromString(isCooling ? "#3FAFD9" : "#C94A1E");
            Color petalOutline = (Color)ColorConverter.ConvertFromString(isCooling ? "#C8F7FF" : "#FFC07A");
            Color petalVein = (Color)ColorConverter.ConvertFromString(isCooling ? "#E8FFFF" : "#FFE2B8");

            FanTile.BorderBrush = new SolidColorBrush(accent);
            FanGlowEffect.Color = accent;
            FanGlowEffect.Opacity = isCooling ? 0.55 : 0.5;
            FanTileStop1.Color = tileTop;
            FanTileStop2.Color = tileMiddle;
            FanTileStop3.Color = tileBottom;
            FanPetalStop1.Color = petalTop;
            FanPetalStop2.Color = petalMiddle;
            FanPetalStop3.Color = petalBottom;
            FanPetals.Stroke = new SolidColorBrush(petalOutline);
            FanPetalVeins.Stroke = new SolidColorBrush(petalVein);
            FanOuterRing.Stroke = new SolidColorBrush(accent);
            FanInnerRing.Stroke = new SolidColorBrush(ring);
            FanCircuitTraces.Stroke = new SolidColorBrush(circuits);
            FanRimDots.Fill = new SolidColorBrush(dots);
            FanPetalGlint.Opacity = isCooling ? 0.8 : 0.6;

            if (_fanAnimation != null)
            {
                StopFanAnimation();
                _fanAnimation.Duration = TimeSpan.FromSeconds(isCooling ? 0.9 : 3.5);
                StartFanAnimation();
            }
        }

        private void OnTelemetryUpdated(HardwareSnapshot snapshot)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsVisible || WindowState == WindowState.Minimized)
                {
                    return;
                }

                UpdateTelemetryUI(snapshot);
            }));
        }

        private void UpdateTelemetryUI(HardwareSnapshot snapshot)
        {
            var s = SettingsService.Instance.Current;
            bool isFahrenheit = s.TemperatureUnit == "Fahrenheit";
            string unit = isFahrenheit ? "°F" : "°C";

            PawnIoBanner.Visibility = snapshot.PawnIoInstalled
                ? Visibility.Collapsed
                : Visibility.Visible;

            // Hero Card
            TxtCpuModel.Text = string.IsNullOrWhiteSpace(snapshot.CpuName)
                ? LocalizationService.Get("Common_NotReported")
                : snapshot.CpuName;
            TxtCpuCores.Text = snapshot.CpuCores > 0
                ? LocalizationService.Format("Main_CpuProcessor", snapshot.CpuCores)
                : string.Empty;
            if (double.IsFinite(snapshot.CpuTemp) && snapshot.CpuTemp > 0)
            {
                double cpuT = isFahrenheit ? (snapshot.CpuTemp * 9 / 5 + 32) : snapshot.CpuTemp;
                TxtHeroTemp.Text = $"{Math.Round(cpuT)}";
            }
            else
            {
                TxtHeroTemp.Text = "--";
            }
            TxtHeroUnit.Text = unit;

            // CPU Speed & Small CPU label
            TxtCpuSmallLabel.Text = FormatCpuSmallRowLabel(snapshot.CpuName, snapshot.CpuVendor);
            TxtCpuClockRatio.Text = FormatCpuClockReadout(
                snapshot.CpuGhz, snapshot.CpuBaseGhz, snapshot.CpuMaxGhz, s.ShowCpuBaseSpeed);
            TxtCpuPackageWatts.Text = snapshot.CpuPackageWatts.HasValue
                ? $"{snapshot.CpuPackageWatts.Value}W"
                : "--W";
            TxtCpuPackagePowerPopup.Text = FormatPackagePower(snapshot.CpuPackageWatts);

            // RAM Row
            if (double.IsFinite(snapshot.RamUsedGb) &&
                double.IsFinite(snapshot.RamTotalGb) &&
                snapshot.RamTotalGb > 0)
            {
                TxtRamRatio.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:0.0} / {1:0.0}",
                    snapshot.RamUsedGb,
                    snapshot.RamTotalGb);
                TxtRamPercent.Text = double.IsFinite(snapshot.RamUsagePercent)
                    ? $"{Math.Round(snapshot.RamUsagePercent)}%"
                    : "--%";
            }
            else
            {
                TxtRamRatio.Text = "-- / --";
                TxtRamPercent.Text = "--%";
            }
            UpdateMemoryBreakdownUi(snapshot);

            // GPU Row
            TxtGpuLabel.Text = LocalizationService.Get("Main_GpuLabel");
            if (double.IsFinite(snapshot.GpuTemp) && snapshot.GpuTemp > 0)
            {
                double gpuT = isFahrenheit ? (snapshot.GpuTemp * 9 / 5 + 32) : snapshot.GpuTemp;
                TxtGpuTemp.Text = $"{Math.Round(gpuT)}{unit}";
            }
            else
            {
                TxtGpuTemp.Text = $"--{unit}";
            }
            TxtGpuPower.Text = snapshot.GpuPowerWatts.HasValue
                ? $"{snapshot.GpuPowerWatts.Value}W"
                : "--W";
            TxtGpuVendor.Text = snapshot.GpuVendor?.Trim() ?? "";
            TxtGpuVendor.Visibility = string.IsNullOrWhiteSpace(TxtGpuVendor.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            string fullGpuName = snapshot.GpuName?.Trim() ?? "";
            TxtGpuName.Text = CompactGpuName(fullGpuName);
            TxtGpuName.ToolTip = string.IsNullOrWhiteSpace(fullGpuName)
                ? null
                : fullGpuName;

            double? hotspotT = snapshot.GpuHotspotTemp.HasValue
                ? (isFahrenheit ? snapshot.GpuHotspotTemp.Value * 9 / 5 + 32 : snapshot.GpuHotspotTemp.Value)
                : null;
            double? vramT = snapshot.GpuVramTemp.HasValue
                ? (isFahrenheit ? snapshot.GpuVramTemp.Value * 9 / 5 + 32 : snapshot.GpuVramTemp.Value)
                : null;
            TxtGpuHotspot.Text = hotspotT.HasValue
                ? $"{Math.Round(hotspotT.Value)}{unit}"
                : "--";
            TxtGpuVram.Text = vramT.HasValue
                ? $"{Math.Round(vramT.Value)}{unit}"
                : "--";
            TxtGpuFan.Text = snapshot.GpuFanRpm.HasValue
                ? $"{snapshot.GpuFanRpm.Value} {LocalizationService.Get("Main_RpmUnit")}"
                : "--";
            TxtGpuVramUsage.Text = FormatGpuVramUsage(snapshot);
            TxtGpuClocks.Text = FormatGpuClocks(snapshot);
            TxtGpuThrottle.Text = CompactThrottleReason(snapshot.GpuThrottleReason);
            TxtGpuThrottle.ToolTip = string.IsNullOrWhiteSpace(snapshot.GpuThrottleReason)
                ? null
                : snapshot.GpuThrottleReason;
            ApplyTelemetryVisibility(s);

            // SSD Rows (rendered before height update so height reflects visible & graphed drives)
            RenderSsdRows(snapshot.StorageDrives, isFahrenheit, unit);
            UpdateWindowHeight(s.ShowOverlayRow, s.ShowAutopilotRow);

            // Draw Charts
            DrawCpuChart();
            DrawGpuChart();

            if (CpuCoresPopup.IsOpen)
            {
                UpdateCpuCoreItems(snapshot);
            }
        }

        private static string FormatGpuVramUsage(HardwareSnapshot snapshot)
        {
            if (!snapshot.GpuVramUsedMb.HasValue ||
                !snapshot.GpuVramTotalMb.HasValue ||
                !double.IsFinite(snapshot.GpuVramUsedMb.Value) ||
                !double.IsFinite(snapshot.GpuVramTotalMb.Value) ||
                snapshot.GpuVramTotalMb.Value <= 0)
            {
                return "--";
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.0} / {1:0.0} GB",
                snapshot.GpuVramUsedMb.Value / 1024.0,
                snapshot.GpuVramTotalMb.Value / 1024.0);
        }

        internal static string FormatGpuClocks(HardwareSnapshot snapshot)
        {
            if (!snapshot.GpuCoreClockMhz.HasValue && !snapshot.GpuMemoryClockMhz.HasValue)
            {
                return "--";
            }

            string core = snapshot.GpuCoreClockMhz.HasValue
                ? snapshot.GpuCoreClockMhz.Value.ToString("0", CultureInfo.InvariantCulture)
                : "--";
            string memory = snapshot.GpuMemoryClockMhz.HasValue
                ? snapshot.GpuMemoryClockMhz.Value.ToString("0", CultureInfo.InvariantCulture)
                : "--";
            return $"{core}/{memory} MHz";
        }

        internal static string CompactThrottleReason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return "--";
            }

            string compact = reason.Trim();
            int separator = compact.LastIndexOf('-');
            if (separator >= 0 && separator + 1 < compact.Length)
            {
                compact = compact[(separator + 1)..].Trim();
            }

            return compact.Length > 18 ? compact[..18] : compact;
        }

        internal static string FormatCpuClockReadout(double cpuGhz, double cpuBaseGhz, double cpuMaxGhz, bool showBaseSpeed)
        {
            string clockNow = double.IsFinite(cpuGhz) && cpuGhz > 0
                ? cpuGhz.ToString("0.00", CultureInfo.InvariantCulture)
                : "--";
            if (!showBaseSpeed)
            {
                return clockNow;
            }

            double baseVal = double.IsFinite(cpuBaseGhz) && cpuBaseGhz > 0 ? cpuBaseGhz : cpuMaxGhz;
            string clockBase = double.IsFinite(baseVal) && baseVal > 0
                ? baseVal.ToString("0.00", CultureInfo.InvariantCulture)
                : "--";
            return $"{clockNow} / {clockBase}";
        }

        internal static string FormatCpuSmallRowLabel(string? cpuName, string? cpuVendor = null)
        {
            string vendor = !string.IsNullOrWhiteSpace(cpuVendor)
                ? cpuVendor.Trim()
                : HardwareMonitorService.ResolveCpuVendor(null, cpuName);

            return !string.IsNullOrWhiteSpace(vendor)
                ? LocalizationService.Format("Main_CpuVendorFormat", vendor)
                : LocalizationService.Get("Common_Cpu");
        }

        private void RenderSsdRows(List<DriveTelemetry> drives, bool isFahrenheit, string unit)
        {
            var s = SettingsService.Instance.Current;
            if (!s.ShowDiskTemp)
            {
                SsdChartTooltip.IsOpen = false;
                _activeHoverDriveRow = null;
                if (_ssdRowSignature != "HIDDEN")
                {
                    SsdRowsContainer.Children.Clear();
                    _ssdRows.Clear();
                    _ssdRowSignature = "HIDDEN";
                }
                return;
            }

            var excluded = s.ExcludedDrives;
            var graphDrives = s.DiskGraphDrives ?? new List<string>();
            var graphTimeframes = s.DiskGraphTimeframes ?? new Dictionary<string, int>();
            var visibleDrives = drives
                .Where(d => !HardwareMonitorService.IsDriveExcluded(d, excluded))
                .ToList();

            string signature = $"1|{drives.Count}|" + string.Join("\n", visibleDrives.Select(drive =>
            {
                string key = HardwareMonitorService.GetDriveExclusionKey(drive);
                bool showGraph = graphDrives.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                int tf = graphTimeframes.TryGetValue(key, out int mins) ? mins : 1;
                return $"{key}|{drive.Model}|{drive.DriveLetters}|{drive.IsSolidState}|{showGraph}|{tf}";
            }));

            if (!string.Equals(signature, _ssdRowSignature, StringComparison.Ordinal))
            {
                BuildSsdRows(visibleDrives, drives.Count, graphDrives);
                _ssdRowSignature = signature;
            }

            for (int i = 0; i < _ssdRows.Count && i < visibleDrives.Count; i++)
            {
                var row = _ssdRows[i];
                var drive = visibleDrives[i];
                int tfMinutes = graphTimeframes.TryGetValue(row.DriveKey, out int mins) ? mins : 1;
                UpdateSsdRow(row, drive, tfMinutes, isFahrenheit, unit);
            }
        }

        private static string[] ParseDriveLetterTokens(string? driveLetters)
        {
            if (string.IsNullOrWhiteSpace(driveLetters))
            {
                return Array.Empty<string>();
            }

            return driveLetters
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(token => token.Trim().TrimEnd(':').ToUpperInvariant())
                .Where(token => token.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        internal static string FormatSsdBadgeText(DriveTelemetry drive)
        {
            string[] tokens = ParseDriveLetterTokens(drive.DriveLetters);
            return tokens.Length > 0
                ? tokens[0]
                : (drive.Index + 1).ToString(CultureInfo.InvariantCulture);
        }

        internal static string FormatSsdRowLabel(DriveTelemetry drive)
        {
            bool isHdd = drive.IsSolidState == false;
            string[] tokens = ParseDriveLetterTokens(drive.DriveLetters);
            if (tokens.Length > 0)
            {
                string formatted = string.Join(", ", tokens.Select(t => $"{t}:"));
                return LocalizationService.Format(isHdd ? "Main_HddWithDrive" : "Main_SsdWithDrive", formatted);
            }

            return LocalizationService.Format(isHdd ? "Main_HddIndexed" : "Main_SsdIndexed", drive.Index + 1);
        }

        private void BuildSsdRows(List<DriveTelemetry> visibleDrives, int totalDetectedDrives, IReadOnlyCollection<string> graphDrives)
        {
            SsdChartTooltip.IsOpen = false;
            _activeHoverDriveRow = null;
            SsdRowsContainer.Children.Clear();
            _ssdRows.Clear();

            if (totalDetectedDrives == 0)
            {
                var empty = new Grid { Height = 38 };
                empty.Children.Add(new TextBlock
                {
                    Text = LocalizationService.Get("Main_StorageUnavailable"),
                    Foreground = ThemeService.GetBrush("TextMutedBrush"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                });
                SsdRowsContainer.Children.Add(empty);
                return;
            }

            if (visibleDrives.Count == 0)
            {
                return;
            }

            foreach (var drive in visibleDrives)
            {
                string key = HardwareMonitorService.GetDriveExclusionKey(drive);
                bool showGraph = graphDrives.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                SsdRowView row = BuildSsdRow(drive, key, showGraph);
                _ssdRows.Add(row);
                SsdRowsContainer.Children.Add(row.Root);
            }

            SsdRowsContainer.Children.Add(new Rectangle
            {
                Height = 1,
                Fill = ThemeService.GetBrush("DividerBrush"),
                Margin = new Thickness(0, 1, 0, 1)
            });
        }

        private SsdRowView BuildSsdRow(DriveTelemetry drive, string driveKey, bool showGraph)
        {
            var grid = new Grid { Height = showGraph ? 46 : 38 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent
            };
            Grid.SetColumn(leftStack, 0);

            var iconHost = new Grid
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 10, 0),
                FlowDirection = System.Windows.FlowDirection.LeftToRight
            };

            iconHost.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M3,5 L23,5 L23,20 L3,20 Z M18,5 L18,20"),
                Stroke = ThemeService.GetBrush("TextTitleBrush"),
                StrokeThickness = 1.2,
                Stretch = Stretch.Uniform,
                Width = 20,
                Height = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            var badgeText = new TextBlock
            {
                Text = FormatSsdBadgeText(drive),
                Foreground = ThemeService.GetBrush("TextTitleBrush"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            iconHost.Children.Add(badgeText);

            var iconLines = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0)
            };
            for (int line = 0; line < 3; line++)
            {
                iconLines.Children.Add(new Rectangle
                {
                    Width = 1,
                    Height = 4,
                    Fill = ThemeService.GetBrush("TextTertiaryBrush"),
                    Margin = new Thickness(0, line == 1 ? 1 : 0, 0, line == 1 ? 0 : 1)
                });
            }
            iconHost.Children.Add(iconLines);
            leftStack.Children.Add(iconHost);

            var rowTitleText = new TextBlock
            {
                Text = FormatSsdRowLabel(drive),
                Foreground = ThemeService.GetBrush("TextTitleBrush"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                FlowDirection = System.Windows.FlowDirection.LeftToRight
            };
            leftStack.Children.Add(rowTitleText);

            var healthText = new TextBlock
            {
                Foreground = SsdHealthGoodBrush,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                FlowDirection = System.Windows.FlowDirection.LeftToRight,
                Margin = new Thickness(8, 0, 0, 0),
                Visibility = Visibility.Collapsed
            };
            leftStack.Children.Add(healthText);
            grid.Children.Add(leftStack);

            var tooltipTitle = new TextBlock
            {
                Foreground = ThemeService.GetBrush("TextPrimaryBrush"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 280
            };
            var tooltipHealth = new TextBlock
            {
                Foreground = ThemeService.GetBrush("TextSecondaryBrush"),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 280,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var tooltipContent = new StackPanel();
            tooltipContent.Children.Add(tooltipTitle);
            tooltipContent.Children.Add(tooltipHealth);

            var tooltip = new ToolTip
            {
                Style = (Style)FindResource("SsdToolTipStyle"),
                Content = tooltipContent,
                Placement = PlacementMode.Top,
                VerticalOffset = -6
            };

            Border? valBorder = null;
            TextBlock? txtVal = null;
            TextBlock? txtUnit = null;
            Canvas? chartCanvas = null;
            Canvas? chartHoverCanvas = null;
            TextBlock? chartUnavailableText = null;

            SsdRowView row;
            if (showGraph)
            {
                ToolTipService.SetInitialShowDelay(leftStack, 250);
                ToolTipService.SetShowDuration(leftStack, 8000);
                leftStack.ToolTip = tooltip;

                var chartHostGrid = new Grid();
                chartCanvas = new Canvas
                {
                    FlowDirection = System.Windows.FlowDirection.LeftToRight,
                    Background = Brushes.Transparent,
                    ClipToBounds = true
                };
                chartHoverCanvas = new Canvas
                {
                    FlowDirection = System.Windows.FlowDirection.LeftToRight,
                    IsHitTestVisible = false,
                    ClipToBounds = true
                };
                chartUnavailableText = new TextBlock
                {
                    Text = "--",
                    Foreground = ThemeService.GetBrush("TextTertiaryBrush"),
                    FontSize = 10.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed
                };

                chartHostGrid.Children.Add(chartCanvas);
                chartHostGrid.Children.Add(chartHoverCanvas);
                chartHostGrid.Children.Add(chartUnavailableText);

                var chartBorder = new Border
                {
                    Width = 238,
                    Height = 36,
                    Background = ThemeService.GetBrush("InsetBackgroundBrush"),
                    BorderBrush = ThemeService.GetBrush("BorderStrongBrush"),
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    FlowDirection = System.Windows.FlowDirection.LeftToRight,
                    Child = chartHostGrid
                };
                Grid.SetColumn(chartBorder, 1);
                grid.Children.Add(chartBorder);

                row = new SsdRowView
                {
                    DriveKey = driveKey,
                    ShowGraph = true,
                    Root = grid,
                    BadgeText = badgeText,
                    RowTitleText = rowTitleText,
                    HealthText = healthText,
                    TooltipTitle = tooltipTitle,
                    TooltipHealth = tooltipHealth,
                    ChartCanvas = chartCanvas,
                    ChartHoverCanvas = chartHoverCanvas,
                    ChartUnavailableText = chartUnavailableText
                };

                chartCanvas.SizeChanged += (_, _) =>
                {
                    var s = SettingsService.Instance.Current;
                    bool isF = s.TemperatureUnit == "Fahrenheit";
                    int tf = s.DiskGraphTimeframes != null && s.DiskGraphTimeframes.TryGetValue(row.DriveKey, out int m) ? m : 1;
                    DrawDriveChart(row, drive, tf, isF, isF ? "°F" : "°C");
                };
                chartCanvas.MouseMove += (_, e) =>
                {
                    if (_activeHoverDriveRow != null && !ReferenceEquals(_activeHoverDriveRow, row))
                    {
                        _activeHoverDriveRow.HoverActive = false;
                        _activeHoverDriveRow.ChartHoverCanvas?.Children.Clear();
                    }
                    _activeHoverDriveRow = row;
                    row.HoverActive = true;
                    row.HoverX = e.GetPosition(chartCanvas).X;
                    UpdateDriveChartTooltip(row);
                };
                chartCanvas.MouseLeave += (_, _) =>
                {
                    row.HoverActive = false;
                    chartHoverCanvas.Children.Clear();
                    if (ReferenceEquals(_activeHoverDriveRow, row))
                    {
                        _activeHoverDriveRow = null;
                        SsdChartTooltip.IsOpen = false;
                    }
                };
            }
            else
            {
                var rightStack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    FlowDirection = System.Windows.FlowDirection.LeftToRight
                };
                Grid.SetColumn(rightStack, 1);

                valBorder = new Border
                {
                    Background = ThemeService.GetBrush("CardBackgroundBrush"),
                    BorderBrush = ThemeService.GetBrush("BorderSubtleBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(12, 3, 12, 3),
                    Margin = new Thickness(0, 0, 6, 0)
                };

                txtVal = new TextBlock
                {
                    Foreground = ThemeService.GetBrush("TextPrimaryBrush"),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold
                };
                valBorder.Child = txtVal;
                rightStack.Children.Add(valBorder);

                var unitBorder = new Border
                {
                    Background = Brushes.Transparent,
                    BorderBrush = ThemeService.GetBrush("InputBorderBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(7, 3, 7, 3)
                };

                txtUnit = new TextBlock
                {
                    Foreground = ThemeService.GetBrush("TextTertiaryBrush"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold
                };
                unitBorder.Child = txtUnit;
                rightStack.Children.Add(unitBorder);
                grid.Children.Add(rightStack);

                ToolTipService.SetInitialShowDelay(valBorder, 250);
                ToolTipService.SetShowDuration(valBorder, 8000);
                valBorder.ToolTip = tooltip;

                row = new SsdRowView
                {
                    DriveKey = driveKey,
                    ShowGraph = false,
                    Root = grid,
                    BadgeText = badgeText,
                    RowTitleText = rowTitleText,
                    HealthText = healthText,
                    TooltipTitle = tooltipTitle,
                    TooltipHealth = tooltipHealth,
                    TempBorder = valBorder,
                    TempText = txtVal,
                    UnitText = txtUnit
                };
            }

            return row;
        }

        private void UpdateSsdRow(SsdRowView row, DriveTelemetry drive, int timeframeMinutes, bool isFahrenheit, string unit)
        {
            row.BadgeText.Text = FormatSsdBadgeText(drive);
            row.RowTitleText.Text = FormatSsdRowLabel(drive);

            if (drive.SmartHealthPercent.HasValue)
            {
                int pct = drive.SmartHealthPercent.Value;
                row.HealthText.Text = $"{pct}%";
                row.HealthText.Foreground = pct >= 80
                    ? SsdHealthGoodBrush
                    : pct >= 50
                        ? SsdHealthFairBrush
                        : SsdHealthWarnBrush;
                row.HealthText.Visibility = Visibility.Visible;
            }
            else
            {
                row.HealthText.Visibility = Visibility.Collapsed;
            }

            bool hasTemperature = double.IsFinite(drive.Temperature) && drive.Temperature > 0;
            double displayTemp = hasTemperature
                ? (isFahrenheit ? (drive.Temperature * 9 / 5 + 32) : drive.Temperature)
                : 0;

            string model = !string.IsNullOrWhiteSpace(drive.Model)
                ? drive.Model.Trim()
                : drive.IsSolidState == false
                    ? "HDD"
                    : drive.IsSolidState == true
                        ? "SSD"
                        : LocalizationService.Format("Alert_DriveName", drive.Index + 1);
            row.TooltipTitle.Text = hasTemperature
                ? LocalizationService.Format("Main_SsdModelTemp", model, Math.Round(displayTemp), unit)
                : model;
            row.TooltipHealth.Text = drive.SmartHealthPercent.HasValue
                ? LocalizationService.Format(
                    "Main_SsdHealth",
                    drive.SmartHealthPercent.Value,
                    LocalizationService.Health(drive.StatusText))
                : LocalizationService.Get("Main_SsdHealthUnknown");

            if (row.ShowGraph)
            {
                DrawDriveChart(row, drive, timeframeMinutes, isFahrenheit, unit);
            }
            else if (row.TempBorder != null && row.TempText != null && row.UnitText != null)
            {
                row.TempText.Text = hasTemperature ? $"{Math.Round(displayTemp)}" : "--";
                row.UnitText.Text = unit;
                AutomationProperties.SetHelpText(row.TempBorder,
                    $"{row.RowTitleText.Text} {row.TempText.Text}{row.UnitText.Text}. {row.TooltipTitle.Text}. {row.TooltipHealth.Text}");
            }
        }

        private void DrawDriveChart(SsdRowView row, DriveTelemetry drive, int timeframeMinutes, bool isFahrenheit, string unit)
        {
            if (row.ChartCanvas == null || row.ChartHoverCanvas == null || row.ChartUnavailableText == null)
            {
                return;
            }

            Canvas canvas = row.ChartCanvas;
            canvas.Children.Clear();
            row.ChartHoverCanvas.Children.Clear();
            double w = canvas.ActualWidth;
            double h = canvas.ActualHeight;
            if (w <= 0 || h <= 0)
            {
                return;
            }

            DrawInlineGridLines(canvas, w, h, horizontalBands: 4, verticalBands: 4, "#2C2C2C");

            int windowSamples = TimeframeMinutesToSamples(timeframeMinutes);
            var samples = HardwareMonitorService.Instance.RingBuffer.GetRecentSamples(windowSamples)
                .Where(sample => sample.TryGetDriveTemp(row.DriveKey, out _))
                .ToList();

            if (samples.Count == 1)
            {
                samples.Add(samples[0]);
            }
            else if (samples.Count == 0 && double.IsFinite(drive.Temperature) && drive.Temperature > 0)
            {
                var fallback = TelemetrySample.FromSnapshot(HardwareMonitorService.Instance.CurrentSnapshot);
                samples.Add(fallback);
                samples.Add(fallback);
            }

            row.ChartSamples.Clear();
            row.ChartSamples.AddRange(samples);

            if (samples.Count < 2)
            {
                row.ChartUnavailableText.Visibility = Visibility.Visible;
                if (ReferenceEquals(_activeHoverDriveRow, row))
                {
                    SsdChartTooltip.IsOpen = false;
                }
                return;
            }

            row.ChartUnavailableText.Visibility = Visibility.Collapsed;

            const double minT = 20;
            const double maxT = 80;
            double stepX = w / Math.Max(1, samples.Count - 1);
            var points = new List<Point>(samples.Count);

            for (int i = 0; i < samples.Count; i++)
            {
                samples[i].TryGetDriveTemp(row.DriveKey, out double t);
                double norm = Math.Clamp((t - minT) / (maxT - minT), 0, 1);
                double y = h - (norm * (h - 8) + 4);
                points.Add(new Point(i * stepX, y));
            }

            var areaBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            areaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(110, 176, 176, 176), 0));
            areaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(14, 176, 176, 176), 1));

            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = CreateSmoothAreaGeometry(points, h),
                Fill = areaBrush
            });

            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = CreateSmoothLineGeometry(points),
                Stroke = ThemeService.GetBrush("ChartLineBrush"),
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            });

            if (row.HoverActive && ReferenceEquals(_activeHoverDriveRow, row))
            {
                UpdateDriveChartTooltip(row);
            }
        }

        private void UpdateDriveChartTooltip(SsdRowView row)
        {
            if (row.ChartCanvas == null || row.ChartHoverCanvas == null || row.ChartSamples.Count < 2)
            {
                return;
            }

            double width = row.ChartCanvas.ActualWidth;
            double height = row.ChartCanvas.ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            int count = row.ChartSamples.Count;
            double stepX = width / Math.Max(1, count - 1);
            int index = Math.Clamp((int)Math.Round(row.HoverX / stepX), 0, count - 1);
            var sample = row.ChartSamples[index];
            if (!sample.TryGetDriveTemp(row.DriveKey, out double temperature))
            {
                SsdChartTooltip.IsOpen = false;
                return;
            }

            double x = index * stepX;
            double normalized = Math.Clamp((temperature - 20) / (80 - 20), 0, 1);
            double y = height - (normalized * (height - 8) + 4);

            bool isFahrenheit = SettingsService.Instance.Current.TemperatureUnit == "Fahrenheit";
            double displayTemp = isFahrenheit ? (temperature * 9 / 5) + 32 : temperature;
            string unit = isFahrenheit ? "°F" : "°C";

            TxtSsdChartTooltipValue.Text = $"{Math.Round(displayTemp)}{unit}";
            TxtSsdChartTooltipTime.Text = sample.Timestamp.ToString("h:mm:ss tt", CultureInfo.CurrentCulture);

            DrawChartHoverMarker(row.ChartHoverCanvas, x, y, height, ThemeService.GetColor("ChartLineBrush"));
            ShowChartTooltip(SsdChartTooltip, row.ChartCanvas, x, y, width);
        }

        private static void DrawInlineGridLines(Canvas canvas, double w, double h, int horizontalBands, int verticalBands, string strokeHex)
        {
            Brush brush = ThemeService.GetBrush("ChartGridBrush");
            for (int i = 1; i < horizontalBands; i++)
            {
                double y = (h / horizontalBands) * i;
                canvas.Children.Add(new Line
                {
                    X1 = 0,
                    X2 = w,
                    Y1 = y,
                    Y2 = y,
                    Stroke = brush,
                    StrokeThickness = 1
                });
            }

            for (int i = 1; i < verticalBands; i++)
            {
                double x = (w / verticalBands) * i;
                canvas.Children.Add(new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = 0,
                    Y2 = h,
                    Stroke = brush,
                    StrokeThickness = 1
                });
            }
        }

        private void DrawCpuChart()
        {
            var s = SettingsService.Instance.Current;
            if (!s.ShowCpuGraph)
            {
                CpuChartCanvas.Children.Clear();
                CpuChartHoverCanvas.Children.Clear();
                CpuSmallChartCanvas.Children.Clear();
                CpuSmallChartHoverCanvas.Children.Clear();
                CpuChartTooltip.IsOpen = false;
                return;
            }

            if (s.CpuGraphSmall)
            {
                CpuChartCanvas.Children.Clear();
                CpuChartHoverCanvas.Children.Clear();
                DrawCpuChartCore(CpuSmallChartCanvas, CpuSmallChartHoverCanvas, TxtCpuSmallChartUnavailable, isSmall: true);
            }
            else
            {
                CpuSmallChartCanvas.Children.Clear();
                CpuSmallChartHoverCanvas.Children.Clear();
                DrawCpuChartCore(CpuChartCanvas, CpuChartHoverCanvas, TxtCpuChartUnavailable, isSmall: false);
            }
        }

        private void DrawCpuChartCore(Canvas plotCanvas, Canvas hoverCanvas, TextBlock unavailableText, bool isSmall)
        {
            plotCanvas.Children.Clear();
            hoverCanvas.Children.Clear();
            double w = plotCanvas.ActualWidth;
            double h = plotCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            double plotH = isSmall ? h : Math.Max(10, h - 26);

            if (isSmall)
            {
                DrawInlineGridLines(plotCanvas, w, plotH, horizontalBands: 4, verticalBands: 4, "#2C2C2C");
            }
            else
            {
                for (int i = 1; i <= 3; i++)
                {
                    double y = (plotH / 4.0) * i;
                    plotCanvas.Children.Add(new Line
                    {
                        X1 = 0,
                        X2 = w,
                        Y1 = y,
                        Y2 = y,
                        Stroke = ThemeService.GetBrush("ChartGridBrush"),
                        StrokeThickness = 1
                    });
                }

                for (int i = 1; i <= 2; i++)
                {
                    double x = (w / 3.0) * i;
                    plotCanvas.Children.Add(new Line
                    {
                        X1 = x,
                        X2 = x,
                        Y1 = 0,
                        Y2 = plotH,
                        Stroke = ThemeService.GetBrush("ChartGridBrush"),
                        StrokeThickness = 1
                    });
                }
            }

            var samples = HardwareMonitorService.Instance.RingBuffer.GetRecentSamples(_timeframeSamples)
                .Where(sample => double.IsFinite(sample.CpuTemp) && sample.CpuTemp > 0)
                .ToList();
            _cpuChartSamples = samples;
            if (samples.Count < 2)
            {
                CpuChartTooltip.IsOpen = false;
                var current = HardwareMonitorService.Instance.CurrentSnapshot;
                bool cpuUnavailable = !(double.IsFinite(current.CpuTemp) && current.CpuTemp > 0);
                unavailableText.Visibility = cpuUnavailable
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                return;
            }

            unavailableText.Visibility = Visibility.Collapsed;

            double minT = 25;
            double maxT = 90;
            double padY = isSmall ? 4.0 : 8.0;

            int n = samples.Count;
            double stepX = w / Math.Max(1, n - 1);

            int start = 0;
            while (start < n - 1)
            {
                bool mode = samples[start].CoolingModeActive;
                int end = start;
                while (end < n - 1 && samples[end + 1].CoolingModeActive == mode)
                {
                    end++;
                }

                int segEnd = Math.Min(end + 1, n - 1);
                DrawCpuSegment(plotCanvas, samples, start, segEnd, mode, minT, maxT, stepX, plotH, padY, h, includeGlow: !isSmall);
                start = end + 1;
            }

            if (_cpuHoverActive && _cpuHoverSmall == isSmall)
            {
                UpdateCpuChartTooltip(_cpuHoverX, isSmall);
            }
            else if (!_cpuHoverActive)
            {
                CpuChartTooltip.IsOpen = false;
            }
        }

        private static void DrawCpuSegment(
            Canvas targetCanvas,
            List<TelemetrySample> samples,
            int start,
            int end,
            bool isCooling,
            double minT,
            double maxT,
            double stepX,
            double h,
            double padY,
            double fillBottom,
            bool includeGlow)
        {
            var points = new List<Point>(end - start + 1);
            for (int i = start; i <= end; i++)
            {
                double t = samples[i].CpuTemp;
                double norm = (t - minT) / (maxT - minT);
                norm = Math.Clamp(norm, 0, 1);
                double y = h - (norm * (h - (padY * 2)) + padY);
                double x = i * stepX;
                points.Add(new Point(x, y));
            }

            if (points.Count < 2)
            {
                return;
            }

            Color lineColor = isCooling
                ? (Color)ColorConverter.ConvertFromString("#3B9BCB")
                : (Color)ColorConverter.ConvertFromString("#C77950");
            Color areaTop = isCooling
                ? (Color)ColorConverter.ConvertFromString("#1A5A7E")
                : (Color)ColorConverter.ConvertFromString("#6A3320");

            var areaBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            areaBrush.GradientStops.Add(new GradientStop(
                Color.FromArgb(160, areaTop.R, areaTop.G, areaTop.B), 0));
            areaBrush.GradientStops.Add(new GradientStop(
                Color.FromArgb(0, areaTop.R, areaTop.G, areaTop.B), 1));

            var area = new System.Windows.Shapes.Path
            {
                Data = CreateSmoothAreaGeometry(points, fillBottom),
                Fill = areaBrush
            };
            targetCanvas.Children.Add(area);

            Geometry lineGeometry = CreateSmoothLineGeometry(points);
            if (includeGlow)
            {
                var glow = new System.Windows.Shapes.Path
                {
                    Data = lineGeometry,
                    Stroke = new SolidColorBrush(Color.FromArgb(42, lineColor.R, lineColor.G, lineColor.B)),
                    StrokeThickness = 3.4,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round
                };
                targetCanvas.Children.Add(glow);
            }

            var line = new System.Windows.Shapes.Path
            {
                Data = lineGeometry,
                Stroke = new SolidColorBrush(lineColor),
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            targetCanvas.Children.Add(line);
        }

        private static Geometry CreateSmoothLineGeometry(IReadOnlyList<Point> points)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(points[0], false, false);
                AddSmoothCurve(context, points);
            }
            geometry.Freeze();
            return geometry;
        }

        private static Geometry CreateSmoothAreaGeometry(IReadOnlyList<Point> points, double baseline)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var first = points[0];
                var last = points[points.Count - 1];
                context.BeginFigure(new Point(first.X, baseline), true, true);
                context.LineTo(first, false, false);
                AddSmoothCurve(context, points);
                context.LineTo(new Point(last.X, baseline), false, false);
            }
            geometry.Freeze();
            return geometry;
        }

        private static void AddSmoothCurve(StreamGeometryContext context, IReadOnlyList<Point> points)
        {
            for (int i = 0; i < points.Count - 1; i++)
            {
                var p0 = i > 0 ? points[i - 1] : points[i];
                var p1 = points[i];
                var p2 = points[i + 1];
                var p3 = i + 2 < points.Count ? points[i + 2] : p2;

                var cp1 = new Point(
                    p1.X + (p2.X - p0.X) / 6.0,
                    p1.Y + (p2.Y - p0.Y) / 6.0);
                var cp2 = new Point(
                    p2.X - (p3.X - p1.X) / 6.0,
                    p2.Y - (p3.Y - p1.Y) / 6.0);

                context.BezierTo(cp1, cp2, p2, true, false);
            }
        }

        private static string NormalizeGpuGraphMetric(string? metric)
        {
            return metric switch
            {
                "Power" => "Power",
                "Load" => "Load",
                _ => "Temperature"
            };
        }

        private static bool TryGetGpuChartValue(
            TelemetrySample sample, string metric, string? gpuIdentifier, out double value)
        {
            switch (metric)
            {
                case "Power":
                    return sample.TryGetGpuPower(gpuIdentifier, out value);
                case "Load":
                    return sample.TryGetGpuLoad(gpuIdentifier, out value);
                default:
                    return sample.TryGetGpuTemp(gpuIdentifier, out value);
            }
        }

        private static bool IsGpuChartMetricAvailable(HardwareSnapshot snapshot, string metric)
        {
            return metric switch
            {
                "Power" => snapshot.GpuPowerWatts.HasValue,
                "Load" => double.IsFinite(snapshot.GpuLoad),
                _ => double.IsFinite(snapshot.GpuTemp) && snapshot.GpuTemp > 0
            };
        }

        private static Color ResolveGpuChartColor(string metric)
        {
            return metric switch
            {
                "Power" => ThemeService.GetColor("CoolingOffTextBrush"),
                "Load" => ThemeService.GetColor("AccentTextBrush"),
                _ => ThemeService.GetColor("GpuAccentBrush")
            };
        }

        private static string FormatGpuChartValue(double value, string metric)
        {
            if (metric == "Power")
            {
                return $"{Math.Round(value)}W";
            }

            if (metric == "Load")
            {
                return $"{Math.Round(value)}%";
            }

            bool isFahrenheit = SettingsService.Instance.Current.TemperatureUnit == "Fahrenheit";
            double displayTemp = isFahrenheit ? (value * 9 / 5) + 32 : value;
            return $"{Math.Round(displayTemp)}°{(isFahrenheit ? "F" : "C")}";
        }

        private void DrawGpuChart()
        {
            GpuChartCanvas.Children.Clear();
            GpuChartHoverCanvas.Children.Clear();
            double w = GpuChartCanvas.ActualWidth;
            double h = GpuChartCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            string metric = NormalizeGpuGraphMetric(SettingsService.Instance.Current.GpuGraphMetric);
            var current = HardwareMonitorService.Instance.CurrentSnapshot;
            string? activeGpuId = string.IsNullOrWhiteSpace(current.GpuIdentifier)
                ? null
                : current.GpuIdentifier;

            var samples = HardwareMonitorService.Instance.RingBuffer.GetRecentSamples(_timeframeSamples)
                .Where(sample => TryGetGpuChartValue(sample, metric, activeGpuId, out _))
                .ToList();
            _gpuChartSamples = samples;
            _gpuChartMetric = metric;
            _gpuChartGpuIdentifier = activeGpuId;
            if (samples.Count < 2)
            {
                GpuChartTooltip.IsOpen = false;
                bool gpuUnavailable = !IsGpuChartMetricAvailable(current, metric);
                TxtGpuChartUnavailable.Visibility = gpuUnavailable
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                return;
            }

            TxtGpuChartUnavailable.Visibility = Visibility.Collapsed;

            double minValue = metric == "Temperature" ? 30 : 0;
            double maxValue = metric switch
            {
                "Temperature" => 90,
                "Load" => 100,
                _ => Math.Max(
                    50,
                    Math.Ceiling(
                        samples.Max(sample => TryGetGpuChartValue(
                            sample, metric, activeGpuId, out double power) ? power : 0) / 50.0) * 50)
            };
            _gpuChartMinimum = minValue;
            _gpuChartMaximum = maxValue;

            double stepX = w / Math.Max(1, samples.Count - 1);

            var areaPoints = new PointCollection { new Point(0, h) };
            var linePoints = new PointCollection();

            for (int i = 0; i < samples.Count; i++)
            {
                TryGetGpuChartValue(samples[i], metric, activeGpuId, out double value);
                double norm = (value - minValue) / Math.Max(1, maxValue - minValue);
                norm = Math.Clamp(norm, 0, 1);
                double y = h - (norm * (h - 8) + 4);
                double x = i * stepX;

                areaPoints.Add(new Point(x, y));
                linePoints.Add(new Point(x, y));
            }

            areaPoints.Add(new Point(w, h));

            Color accent = ResolveGpuChartColor(metric);
            var area = new Polygon
            {
                Points = areaPoints,
                Fill = new SolidColorBrush(Color.FromArgb(100, accent.R, accent.G, accent.B))
            };
            GpuChartCanvas.Children.Add(area);

            for (int i = 1; i <= 3; i++)
            {
                double x = (w / 4.0) * i;
                GpuChartCanvas.Children.Add(new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = 0,
                    Y2 = h,
                    Stroke = ThemeService.GetBrush("ChartGridBrush"),
                    StrokeThickness = 1
                });
            }

            GpuChartCanvas.Children.Add(new Line
            {
                X1 = 0,
                X2 = w,
                Y1 = h / 2,
                Y2 = h / 2,
                Stroke = ThemeService.GetBrush("ChartGridBrush"),
                StrokeThickness = 1
            });

            var line = new Polyline
            {
                Points = linePoints,
                Stroke = new SolidColorBrush(accent),
                StrokeThickness = 1.5
            };
            GpuChartCanvas.Children.Add(line);

            if (_gpuHoverActive)
            {
                UpdateGpuChartTooltip(_gpuHoverX);
            }
            else
            {
                GpuChartTooltip.IsOpen = false;
            }
        }

        private static int TimeframeMinutesToSamples(int minutes) => minutes switch
        {
            5 => 300,
            10 => 600,
            30 => 1800,
            60 => 3600,
            _ => 60
        };

        private static int TimeframeMinutesToIndex(int minutes) => minutes switch
        {
            5 => 1,
            10 => 2,
            30 => 3,
            60 => 4,
            _ => 0
        };

        private static int IndexToTimeframeMinutes(int index) => index switch
        {
            1 => 5,
            2 => 10,
            3 => 30,
            4 => 60,
            _ => 1
        };

        private void CpuChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawCpuChart();
        }

        private void CpuSmallChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawCpuChart();
        }

        private void CmbTimeframe_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int minutes = IndexToTimeframeMinutes(CmbTimeframe.SelectedIndex);
            _timeframeSamples = TimeframeMinutesToSamples(minutes);

            // The initial selection fires while XAML is still creating the GPU chart.
            if (CpuChartCanvas == null || CpuChartHoverCanvas == null ||
                GpuChartCanvas == null || GpuChartHoverCanvas == null)
            {
                return;
            }

            if (!_isApplyingSettings && IsLoaded)
            {
                var settings = SettingsService.Instance.Current;
                if (settings.CpuGraphTimeframeMinutes != minutes)
                {
                    settings.CpuGraphTimeframeMinutes = minutes;
                    SettingsService.Instance.Save();
                }
            }

            DrawCpuChart();
            DrawGpuChart();
        }

        private void CpuChartCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            _cpuHoverActive = true;
            _cpuHoverSmall = false;
            _cpuHoverX = e.GetPosition(CpuChartCanvas).X;
            UpdateCpuChartTooltip(_cpuHoverX, false);
        }

        private void CpuChartCanvas_MouseLeave(object sender, MouseEventArgs e)
        {
            _cpuHoverActive = false;
            CpuChartTooltip.IsOpen = false;
            CpuChartHoverCanvas.Children.Clear();
        }

        private void CpuSmallChartCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            _cpuHoverActive = true;
            _cpuHoverSmall = true;
            _cpuHoverX = e.GetPosition(CpuSmallChartCanvas).X;
            UpdateCpuChartTooltip(_cpuHoverX, true);
        }

        private void CpuSmallChartCanvas_MouseLeave(object sender, MouseEventArgs e)
        {
            _cpuHoverActive = false;
            CpuChartTooltip.IsOpen = false;
            CpuSmallChartHoverCanvas.Children.Clear();
        }

        private void GpuChartCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            _gpuHoverActive = true;
            _gpuHoverX = e.GetPosition(GpuChartCanvas).X;
            UpdateGpuChartTooltip(_gpuHoverX);
        }

        private void GpuChartCanvas_MouseLeave(object sender, MouseEventArgs e)
        {
            _gpuHoverActive = false;
            GpuChartTooltip.IsOpen = false;
            GpuChartHoverCanvas.Children.Clear();
        }

        private void UpdateCpuChartTooltip(double hoverX, bool isSmall)
        {
            if (_cpuChartSamples.Count < 2)
            {
                return;
            }

            Canvas targetCanvas = isSmall ? CpuSmallChartCanvas : CpuChartCanvas;
            Canvas hoverCanvas = isSmall ? CpuSmallChartHoverCanvas : CpuChartHoverCanvas;
            double width = targetCanvas.ActualWidth;
            double height = targetCanvas.ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            double plotH = isSmall ? height : Math.Max(10, height - 26);
            double padY = isSmall ? 4.0 : 8.0;
            int count = _cpuChartSamples.Count;
            double stepX = width / Math.Max(1, count - 1);
            int index = Math.Clamp((int)Math.Round(hoverX / stepX), 0, count - 1);
            var sample = _cpuChartSamples[index];
            double x = index * stepX;
            double temperature = sample.CpuTemp;
            if (!double.IsFinite(temperature) || temperature <= 0)
            {
                CpuChartTooltip.IsOpen = false;
                return;
            }
            double normalized = Math.Clamp((temperature - 25) / (90 - 25), 0, 1);
            double y = plotH - (normalized * (plotH - (padY * 2)) + padY);

            bool isFahrenheit = SettingsService.Instance.Current.TemperatureUnit == "Fahrenheit";
            double displayTemp = isFahrenheit ? (temperature * 9 / 5) + 32 : temperature;
            string unit = isFahrenheit ? "°F" : "°C";

            TxtCpuChartTooltipValue.Text = LocalizationService.Format(
                "Main_CpuChartValue", Math.Round(displayTemp), unit);
            TxtCpuChartTooltipTime.Text = sample.Timestamp.ToString("h:mm:ss tt", CultureInfo.CurrentCulture);

            Color accent = sample.CoolingModeActive
                ? ThemeService.GetColor("CpuAccentBrush")
                : ThemeService.GetColor("CoolingOffTextBrush");

            DrawChartHoverMarker(hoverCanvas, x, y, plotH, accent);
            ShowChartTooltip(CpuChartTooltip, targetCanvas, x, y, width);
        }

        private void UpdateGpuChartTooltip(double hoverX)
        {
            if (_gpuChartSamples.Count < 2)
            {
                return;
            }

            double width = GpuChartCanvas.ActualWidth;
            double height = GpuChartCanvas.ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            int count = _gpuChartSamples.Count;
            double stepX = width / Math.Max(1, count - 1);
            int index = Math.Clamp((int)Math.Round(hoverX / stepX), 0, count - 1);
            var sample = _gpuChartSamples[index];
            double x = index * stepX;
            if (!TryGetGpuChartValue(
                sample, _gpuChartMetric, _gpuChartGpuIdentifier, out double value))
            {
                GpuChartTooltip.IsOpen = false;
                return;
            }
            double normalized = Math.Clamp(
                (value - _gpuChartMinimum) / Math.Max(1, _gpuChartMaximum - _gpuChartMinimum),
                0,
                1);
            double y = height - (normalized * (height - 8) + 4);

            TxtGpuChartTooltipValue.Text = FormatGpuChartValue(value, _gpuChartMetric);
            TxtGpuChartTooltipTime.Text = sample.Timestamp.ToString("h:mm:ss tt", CultureInfo.CurrentCulture);

            DrawChartHoverMarker(
                GpuChartHoverCanvas,
                x,
                y,
                height,
                ResolveGpuChartColor(_gpuChartMetric));
            ShowChartTooltip(GpuChartTooltip, GpuChartCanvas, x, y, width);
        }

        private static void DrawChartHoverMarker(Canvas overlay, double x, double y, double height, Color accent)
        {
            overlay.Children.Clear();

            overlay.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = 0,
                Y2 = height,
                Stroke = ThemeService.GetBrush("ChartHoverLineBrush"),
                StrokeThickness = 1,
                Opacity = 0.55
            });

            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(accent),
                StrokeThickness = 2
            };
            Canvas.SetLeft(dot, x - 3.5);
            Canvas.SetTop(dot, y - 3.5);
            overlay.Children.Add(dot);
        }

        private static void ShowChartTooltip(Popup popup, Canvas target, double x, double y, double chartWidth)
        {
            popup.PlacementTarget = target;
            popup.Placement = PlacementMode.Relative;

            if (popup.Child is FrameworkElement child)
            {
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double popupWidth = child.DesiredSize.Width;
                double popupHeight = child.DesiredSize.Height;
                double maxLeft = chartWidth - popupWidth - 4;
                double left = maxLeft >= 4
                    ? Math.Clamp(x - (popupWidth / 2), 4, maxLeft)
                    : Math.Max(0, (chartWidth - popupWidth) / 2);

                popup.HorizontalOffset = left;
                popup.VerticalOffset = y - popupHeight - 8;
            }

            popup.IsOpen = true;
        }

        private void HideChartTooltips()
        {
            _cpuHoverActive = false;
            _gpuHoverActive = false;
            CpuChartTooltip.IsOpen = false;
            GpuChartTooltip.IsOpen = false;
            SsdChartTooltip.IsOpen = false;
            CpuChartHoverCanvas.Children.Clear();
            CpuSmallChartHoverCanvas.Children.Clear();
            GpuChartHoverCanvas.Children.Clear();
            foreach (var row in _ssdRows)
            {
                row.HoverActive = false;
                row.ChartHoverCanvas?.Children.Clear();
            }
            _activeHoverDriveRow = null;
        }

        private void BtnCoolingToggle_Click(object sender, RoutedEventArgs e)
        {
            PowerPlanService.Instance.ToggleCoolingMode();
        }

        private void BtnStressTest_Click(object sender, RoutedEventArgs e)
        {
            ToggleStressTestDialog();
        }

        private void BtnToolsMenu_Click(object sender, RoutedEventArgs e)
        {
            if (ToolsContextMenu == null)
            {
                return;
            }

            ToolsContextMenu.PlacementTarget = BtnToolsMenu;
            ToolsContextMenu.Placement = PlacementMode.Bottom;
            ToolsContextMenu.HorizontalOffset = 0;
            ToolsContextMenu.VerticalOffset = 4;
            ToolsContextMenu.FlowDirection = FlowDirection;
            ToolsContextMenu.IsOpen = true;
        }

        private void ToolsContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (ToolsContextMenu == null)
            {
                return;
            }

            ToolsContextMenu.FlowDirection = FlowDirection;
            bool isRecordingCsv = TelemetryCsvService.Instance.IsRecording;
            MenuToolsRecordCsv.Header = LocalizationService.Get(isRecordingCsv ? "Main_ToolsStopCsvLog" : "Main_ToolsStartCsvLog");
        }

        private void MenuToolsStress_Click(object sender, RoutedEventArgs e)
        {
            OpenStressTestDialog();
        }

        private void MenuToolsExportCsv_Click(object sender, RoutedEventArgs e)
        {
            ExportRingBufferCsv();
        }

        private void MenuToolsRecordCsv_Click(object sender, RoutedEventArgs e)
        {
            ToggleLiveCsvLogging();
        }

        private void MenuToolsAbout_Click(object sender, RoutedEventArgs e)
        {
            OpenSettingsDialog(true);
        }

        private void RunWithChildDialogGuard(Action action)
        {
            bool previous = _isChildDialogOpen;
            _isChildDialogOpen = true;
            try
            {
                action();
            }
            finally
            {
                UpdateChildDialogState();
                if (previous)
                {
                    _isChildDialogOpen = true;
                }
            }
        }

        private void ExportRingBufferCsv()
        {
            var samples = HardwareMonitorService.Instance.RingBuffer.GetRecentSamples(3600);
            if (samples.Count == 0)
            {
                RunWithChildDialogGuard(() => MessageBox.Show(
                    LocalizationService.Get("Main_CsvNoSamples"),
                    LocalizationService.Get("App_Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information));
                return;
            }

            RunWithChildDialogGuard(() =>
            {
                var sfd = new SaveFileDialog
                {
                    Filter = LocalizationService.Get("Common_CsvFilter"),
                    FileName = $"CryoTray_Telemetry_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
                };

                if (sfd.ShowDialog(this) == true)
                {
                    TelemetryCsvService.ExportSamples(samples, sfd.FileName);
                    MessageBox.Show(
                        LocalizationService.Format("Main_CsvExportDone", samples.Count, sfd.FileName),
                        LocalizationService.Get("App_Title"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            });
        }

        private void ToggleLiveCsvLogging()
        {
            if (TelemetryCsvService.Instance.IsRecording)
            {
                TelemetryCsvService.Instance.StopRecording();
                return;
            }

            RunWithChildDialogGuard(() =>
            {
                var sfd = new SaveFileDialog
                {
                    Filter = LocalizationService.Get("Common_CsvFilter"),
                    FileName = $"CryoTray_LiveLog_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
                };

                if (sfd.ShowDialog(this) == true)
                {
                    TelemetryCsvService.Instance.StartRecording(sfd.FileName);
                }
            });
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            ToggleSettingsDialog();
        }

        private void BtnSystemInfo_Click(object sender, RoutedEventArgs e)
        {
            ToggleSystemInfoDialog();
        }

        public void OpenStressTestDialog()
        {
            if (_stressTestWindow != null)
            {
                _stressTestWindow.Activate();
                return;
            }

            var win = new StressTestWindow { Owner = this };
            DockSideBySide(win);
            _stressTestWindow = win;
            win.Closed += (_, _) => UntrackChildWindow(win);
            UpdateChildDialogState();
            UpdateTitleIconStates();
            win.Show();
            win.Activate();
        }

        private void ToggleStressTestDialog()
        {
            if (_stressTestWindow != null)
            {
                _stressTestWindow.Close();
                return;
            }

            OpenStressTestDialog();
        }

        private void RefreshUpdateBanner()
        {
            if (UpdateBanner == null)
            {
                return;
            }

            var release = (System.Windows.Application.Current as App)?.AvailableUpdate;
            var settings = SettingsService.Instance.Current;
            _updateSnoozeTimer?.Stop();
            _updateSnoozeTimer = null;

            bool dismissed = release != null &&
                string.Equals(settings.DismissedUpdateVersion, release.Version, StringComparison.Ordinal);
            bool snoozed = release != null &&
                string.Equals(settings.UpdateSnoozedVersion, release.Version, StringComparison.Ordinal) &&
                settings.UpdateSnoozedUntilUtc.HasValue &&
                settings.UpdateSnoozedUntilUtc.Value > DateTime.UtcNow;

            if (!snoozed && release != null &&
                string.Equals(settings.UpdateSnoozedVersion, release.Version, StringComparison.Ordinal) &&
                settings.UpdateSnoozedUntilUtc.HasValue)
            {
                settings.UpdateSnoozedVersion = "";
                settings.UpdateSnoozedUntilUtc = null;
                SettingsService.Instance.Save();
            }

            if (release == null || dismissed || snoozed)
            {
                _visibleUpdate = null;
                UpdateBanner.Visibility = Visibility.Collapsed;

                if (snoozed && settings.UpdateSnoozedUntilUtc.HasValue)
                {
                    TimeSpan remaining = settings.UpdateSnoozedUntilUtc.Value - DateTime.UtcNow;
                    if (remaining > TimeSpan.Zero)
                    {
                        _updateSnoozeTimer = new DispatcherTimer { Interval = remaining };
                        _updateSnoozeTimer.Tick += UpdateSnoozeTimer_Tick;
                        _updateSnoozeTimer.Start();
                    }
                }
            }
            else
            {
                _visibleUpdate = release;
                TxtUpdateBannerVersion.Text = LocalizationService.Format("Update_BannerVersion", release.Version);
                UpdateBanner.Visibility = Visibility.Visible;
            }

            UpdateWindowHeight(settings.ShowOverlayRow, settings.ShowAutopilotRow);
        }

        private void UpdateSnoozeTimer_Tick(object? sender, EventArgs e)
        {
            _updateSnoozeTimer?.Stop();
            _updateSnoozeTimer = null;
            RefreshUpdateBanner();
        }

        private void OnUpdateStatusChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnUpdateStatusChanged));
                return;
            }

            RefreshUpdateBanner();
        }

        private void RefreshSuccessBanner()
        {
            if (UpdateSuccessBanner == null || TxtUpdateSuccess == null)
            {
                return;
            }

            var completion = (System.Windows.Application.Current as App)?.PendingUpdateSuccess;
            if (completion == null)
            {
                if (UpdateSuccessBanner.Visibility == Visibility.Visible)
                {
                    HideUpdateSuccessBanner(consume: false);
                }
                return;
            }

            TxtUpdateSuccess.Text = LocalizationService.Format(
                "Update_SuccessBanner", completion.FromVersion, completion.ToVersion);

            if (UpdateSuccessBanner.Visibility == Visibility.Visible)
            {
                return;
            }

            UpdateSuccessBanner.Visibility = Visibility.Visible;
            _updateSuccessTimer?.Stop();
            _updateSuccessTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(20)
            };
            _updateSuccessTimer.Tick += UpdateSuccessTimer_Tick;
            _updateSuccessTimer.Start();
            UpdateWindowHeight(
                SettingsService.Instance.Current.ShowOverlayRow,
                SettingsService.Instance.Current.ShowAutopilotRow);
        }

        private void UpdateSuccessTimer_Tick(object? sender, EventArgs e)
        {
            HideUpdateSuccessBanner(consume: true);
        }

        private void HideUpdateSuccessBanner(bool consume)
        {
            _updateSuccessTimer?.Stop();
            _updateSuccessTimer = null;

            if (UpdateSuccessBanner.Visibility == Visibility.Visible)
            {
                UpdateSuccessBanner.Visibility = Visibility.Collapsed;
                UpdateWindowHeight(
                    SettingsService.Instance.Current.ShowOverlayRow,
                    SettingsService.Instance.Current.ShowAutopilotRow);
            }

            if (consume)
            {
                (System.Windows.Application.Current as App)?.ConsumeUpdateSuccess();
            }
        }

        private void BtnUpdateSuccessClose_Click(object sender, RoutedEventArgs e)
        {
            HideUpdateSuccessBanner(consume: true);
        }

        private void BtnUpdateBannerUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_visibleUpdate != null)
            {
                OpenSettingsDialog(true, _visibleUpdate, true);
            }
        }

        private void BtnUpdateBannerAbout_Click(object sender, RoutedEventArgs e)
        {
            if (_visibleUpdate != null)
            {
                OpenSettingsDialog(true, _visibleUpdate, false);
            }
            else
            {
                OpenSettingsDialog(true);
            }
        }

        private void BtnUpdateBannerLater_Click(object sender, RoutedEventArgs e)
        {
            if (_visibleUpdate != null)
            {
                var settings = SettingsService.Instance.Current;
                settings.UpdateSnoozedVersion = _visibleUpdate.Version;
                settings.UpdateSnoozedUntilUtc = DateTime.UtcNow.AddHours(24);
                SettingsService.Instance.Save();
            }

            RefreshUpdateBanner();
        }

        private void BtnUpdateBannerSkip_Click(object sender, RoutedEventArgs e)
        {
            if (_visibleUpdate != null)
            {
                var settings = SettingsService.Instance.Current;
                settings.DismissedUpdateVersion = _visibleUpdate.Version;
                settings.UpdateSnoozedVersion = "";
                settings.UpdateSnoozedUntilUtc = null;
                SettingsService.Instance.Save();
            }

            RefreshUpdateBanner();
        }

        public void OpenSettingsDialog()
        {
            OpenSettingsDialog(false);
        }

        public void OpenSettingsDialog(bool openAbout)
        {
            ShowSettingsWindow(openAbout, false);
        }

        public void OpenSettingsDialog(bool openAbout, ReleaseInfo? update, bool installUpdate)
        {
            ShowSettingsWindow(openAbout, false, update, installUpdate);
        }

        public void OpenAutopilotSettingsDialog()
        {
            ShowSettingsWindow(false, true);
        }

        private void ToggleSettingsDialog()
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Close();
                return;
            }

            ShowSettingsWindow(false, false);
        }

        private void ShowSettingsWindow(bool openAbout, bool openAutopilot,
            ReleaseInfo? update = null, bool installUpdate = false)
        {
            if (_settingsWindow != null)
            {
                if (openAbout) _settingsWindow.SelectAboutTab();
                if (openAutopilot) _settingsWindow.SelectAutopilotTab();
                _settingsWindow.Activate();
                if (update != null)
                {
                    if (installUpdate) _settingsWindow.BeginUpdateInstall(update);
                    else _settingsWindow.ShowAvailableUpdate(update);
                }
                return;
            }

            var win = new SettingsWindow
            {
                Owner = this,
                OpenAboutOnLoad = openAbout,
                OpenAutopilotOnLoad = openAutopilot,
                PendingUpdate = update,
                PendingUpdateInstall = installUpdate
            };
            DockSideBySide(win);
            _settingsWindow = win;
            win.Closed += (_, _) => UntrackChildWindow(win);
            UpdateChildDialogState();
            UpdateTitleIconStates();
            win.Show();
            win.Activate();
        }

        public void OpenSystemInfoDialog(string? highlightFanKey = null)
        {
            if (_systemInfoWindow != null)
            {
                _systemInfoWindow.Activate();
                if (!string.IsNullOrWhiteSpace(highlightFanKey))
                {
                    _systemInfoWindow.HighlightFan(highlightFanKey);
                }

                return;
            }

            var win = new SystemInfoWindow { Owner = this };
            DockSideBySide(win);
            _systemInfoWindow = win;
            win.Closed += (_, _) => UntrackChildWindow(win);
            UpdateChildDialogState();
            UpdateTitleIconStates();
            win.Show();
            win.Activate();
            if (!string.IsNullOrWhiteSpace(highlightFanKey))
            {
                win.HighlightFan(highlightFanKey);
            }
        }

        private void ToggleSystemInfoDialog()
        {
            if (_systemInfoWindow != null)
            {
                _systemInfoWindow.Close();
                return;
            }

            OpenSystemInfoDialog();
        }

        private void UntrackChildWindow(Window window)
        {
            if (ReferenceEquals(window, _settingsWindow)) _settingsWindow = null;
            if (ReferenceEquals(window, _systemInfoWindow)) _systemInfoWindow = null;
            if (ReferenceEquals(window, _stressTestWindow)) _stressTestWindow = null;

            UpdateChildDialogState();
            UpdateTitleIconStates();
        }

        private void UpdateChildDialogState()
        {
            _isChildDialogOpen = _settingsWindow != null ||
                _systemInfoWindow != null ||
                _stressTestWindow != null;
        }

        private void UpdateTitleIconStates()
        {
            bool settingsOpen = _settingsWindow != null;
            bool systemInfoOpen = _systemInfoWindow != null;
            bool stressTestOpen = _stressTestWindow != null;

            SetTitleIconActive(StressIconRing, Shape.StrokeProperty, stressTestOpen);
            SetTitleIconActive(StressIconBlades, Shape.FillProperty, stressTestOpen);
            SetTitleIconActive(StressIconHub, Shape.FillProperty, stressTestOpen);
            SetTitleIconActive(SettingsIconPath, Shape.FillProperty, settingsOpen);
            SetTitleIconActive(SystemInfoIconPath, Shape.FillProperty, systemInfoOpen);
        }

        private static void SetTitleIconActive(Shape icon, DependencyProperty property, bool active)
        {
            if (active)
            {
                icon.SetResourceReference(property, "WindowBorderBrush");
            }
            else
            {
                icon.SetResourceReference(property, "IconBrush");
            }
        }

        public void DockSideBySide(Window win)
        {
            win.WindowStartupLocation = WindowStartupLocation.Manual;

            Rect workArea = WindowPlacementHelper.GetMonitorWorkAreaDips(this);
            var anchorBounds = new Rect(Left, Top, Width, Height);
            var (targetLeft, targetTop) = WindowPlacementHelper.ComputeSideBySidePosition(
                workArea,
                anchorBounds,
                win.Width,
                win.Height,
                gap: 6);

            win.Left = targetLeft;
            win.Top = targetTop;
        }

        private void BtnOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (System.Windows.Application.Current is App app)
            {
                app.ToggleOverlay();
            }
        }

        private void BtnAutopilot_Click(object sender, RoutedEventArgs e)
        {
            OpenAutopilotSettingsDialog();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        public void SetExitConfirmationOpen(bool isOpen)
        {
            _isExitConfirmationOpen = isOpen;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            ShowKeyboardFocusCues = true;
            if (e.Key == Key.Escape && RamBreakdownToolTip.IsOpen)
            {
                CloseRamBreakdownToolTip();
                e.Handled = true;
            }
        }

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            ShowKeyboardFocusCues = false;
            if (_ramTooltipPinned && e.OriginalSource is DependencyObject source && !IsDescendantOf(source, BtnRamBreakdown))
            {
                CloseRamBreakdownToolTip();
            }
        }

        private static bool IsDescendantOf(DependencyObject node, DependencyObject ancestor)
        {
            DependencyObject? current = node;
            while (current != null)
            {
                if (ReferenceEquals(current, ancestor))
                {
                    return true;
                }
                current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return false;
        }

        private void BtnRamBreakdown_Click(object sender, RoutedEventArgs e)
        {
            if (RamBreakdownToolTip.IsOpen && _ramTooltipPinned)
            {
                CloseRamBreakdownToolTip();
                return;
            }

            CpuCoresPopup.IsOpen = false;
            _ramTooltipPinned = true;
            UpdateMemoryBreakdownUi(HardwareMonitorService.Instance.CurrentSnapshot);
            RamBreakdownToolTip.IsOpen = true;
        }

        private void BtnRamBreakdown_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (_ramTooltipPinned)
            {
                CloseRamBreakdownToolTip();
            }
        }

        private void RamBreakdownToolTip_Opened(object sender, RoutedEventArgs e)
        {
            UpdateMemoryBreakdownUi(HardwareMonitorService.Instance.CurrentSnapshot);
        }

        private void RamBreakdownToolTip_Closed(object sender, RoutedEventArgs e)
        {
            _ramTooltipPinned = false;
        }

        private void CloseRamBreakdownToolTip()
        {
            _ramTooltipPinned = false;
            RamBreakdownToolTip.IsOpen = false;
        }

        internal void ExerciseRamTooltipLifecycleForTest()
        {
            if (RamTelemetryZone.ToolTip != null ||
                TxtRamPercent.ToolTip != null ||
                !ReferenceEquals(BtnRamBreakdown.ToolTip, RamBreakdownToolTip))
            {
                throw new InvalidOperationException("RamBreakdownToolTip must be scoped strictly to BtnRamBreakdown.");
            }

            var clickArgs = new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, BtnRamBreakdown);
            BtnRamBreakdown_Click(BtnRamBreakdown, clickArgs);
            if (!RamBreakdownToolTip.IsOpen || !RamBreakdownToolTip.StaysOpen || !_ramTooltipPinned)
            {
                throw new InvalidOperationException("Click-pin open state invalid on RamBreakdownToolTip.");
            }

            CloseRamBreakdownToolTip();
            if (RamBreakdownToolTip.IsOpen || !RamBreakdownToolTip.StaysOpen || _ramTooltipPinned)
            {
                throw new InvalidOperationException("CloseRamBreakdownToolTip must leave ToolTip.StaysOpen true.");
            }

            var pcsType = typeof(FrameworkElement).Assembly.GetType("System.Windows.Controls.PopupControlService")
                ?? throw new InvalidOperationException("WPF PopupControlService type not found.");
            var pcs = pcsType.GetProperty("Current", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?.GetValue(null)
                ?? throw new InvalidOperationException("WPF PopupControlService.Current is null.");
            var findMethod = pcsType.GetMethod("FindToolTipOwner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? throw new InvalidOperationException("WPF PopupControlService.FindToolTipOwner not found.");
            var showMethod = pcsType.GetMethod("ShowToolTip", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? throw new InvalidOperationException("WPF PopupControlService.ShowToolTip not found.");
            var dismissMethod = pcsType.GetMethod("DismissToolTips", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? throw new InvalidOperationException("WPF PopupControlService.DismissToolTips not found.");

            var triggerType = findMethod.GetParameters()[1].ParameterType;
            object mouseTrigger = Enum.Parse(triggerType, "Mouse");
            if (findMethod.Invoke(pcs, new object[] { RamTelemetryZone, mouseTrigger }) != null ||
                findMethod.Invoke(pcs, new object[] { TxtRamPercent, mouseTrigger }) != null ||
                !ReferenceEquals(findMethod.Invoke(pcs, new object[] { BtnRamBreakdown, mouseTrigger }), BtnRamBreakdown) ||
                !ReferenceEquals(findMethod.Invoke(pcs, new object[] { TxtRamRatio, mouseTrigger }), BtnRamBreakdown))
            {
                throw new InvalidOperationException("FindToolTipOwner did not scope hover exclusively to BtnRamBreakdown.");
            }

            // Hover #1
            showMethod.Invoke(pcs, new object[] { BtnRamBreakdown, false });
            if (!RamBreakdownToolTip.IsOpen || !RamBreakdownToolTip.StaysOpen)
            {
                throw new InvalidOperationException("Hover #1 failed to open RamBreakdownToolTip.");
            }

            dismissMethod.Invoke(pcs, Array.Empty<object>());
            if (RamBreakdownToolTip.IsOpen || !RamBreakdownToolTip.StaysOpen || _ramTooltipPinned)
            {
                throw new InvalidOperationException("Hover #1 dismiss state invalid on RamBreakdownToolTip.");
            }

            // Hover #2 (regression check after closing)
            showMethod.Invoke(pcs, new object[] { BtnRamBreakdown, false });
            if (!RamBreakdownToolTip.IsOpen || !RamBreakdownToolTip.StaysOpen)
            {
                throw new InvalidOperationException("Hover #2 failed to open RamBreakdownToolTip.");
            }

            dismissMethod.Invoke(pcs, Array.Empty<object>());

            // Pinned + Hover
            BtnRamBreakdown_Click(BtnRamBreakdown, clickArgs);
            showMethod.Invoke(pcs, new object[] { BtnRamBreakdown, false });
            if (!RamBreakdownToolTip.IsOpen || !RamBreakdownToolTip.StaysOpen)
            {
                throw new InvalidOperationException("Pinned + Hover state invalid on RamBreakdownToolTip.");
            }

            dismissMethod.Invoke(pcs, Array.Empty<object>());
            CloseRamBreakdownToolTip();
        }

        private async void BtnOptimizeRam_Click(object sender, RoutedEventArgs e)
        {
            await MemoryOptimizerService.Instance.OptimizeAsync(MemoryOptimizationTrigger.Manual);
        }

        private void BtnOptimizeRamMenu_Click(object sender, RoutedEventArgs e)
        {
            if (RamScopeMenu == null)
            {
                return;
            }

            RamScopeMenu.PlacementTarget = BtnOptimizeRam;
            RamScopeMenu.Placement = PlacementMode.Bottom;
            RamScopeMenu.VerticalOffset = 4;
            RamScopeMenu.IsOpen = true;
        }

        private void RamScopeMenu_Opened(object sender, RoutedEventArgs e)
        {
            var settings = SettingsService.Instance.Current;
            RamScopeMenu.FlowDirection = FlowDirection;
            MenuRamFreeWorkingSets.IsChecked = settings.RamFreeWorkingSets;
            MenuRamClearStandby.IsChecked = settings.RamClearStandby;
        }

        internal (System.Drawing.Bitmap ToolsMenu, System.Drawing.Bitmap RamMenu) RenderWpfContextMenusForTest()
        {
            ToolsContextMenu_Opened(ToolsContextMenu, new RoutedEventArgs());
            RamScopeMenu_Opened(RamScopeMenu, new RoutedEventArgs());
            MenuRamFreeWorkingSets.IsChecked = true;
            MenuRamClearStandby.IsChecked = false;

            return (
                RenderContextMenuBitmapForTest(ToolsContextMenu),
                RenderContextMenuBitmapForTest(RamScopeMenu));
        }

        private static System.Drawing.Bitmap RenderContextMenuBitmapForTest(ContextMenu menu)
        {
            menu.ApplyTemplate();
            menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size desired = menu.DesiredSize;
            int width = Math.Max(1, (int)Math.Ceiling(desired.Width));
            int height = Math.Max(1, (int)Math.Ceiling(desired.Height));
            menu.Arrange(new Rect(0, 0, width, height));
            menu.UpdateLayout();

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(menu);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Position = 0;
            using var temp = new System.Drawing.Bitmap(ms);
            return new System.Drawing.Bitmap(temp);
        }

        private void MenuRamScope_Click(object sender, RoutedEventArgs e)
        {
            var settings = SettingsService.Instance.Current;
            settings.RamFreeWorkingSets = MenuRamFreeWorkingSets.IsChecked;
            settings.RamClearStandby = MenuRamClearStandby.IsChecked;
            SettingsService.Instance.Save();
            UpdateMemoryTooltipFooter();
        }

        private void OnRamOptimizingStateChanged(bool isOptimizing)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isClosing)
                {
                    return;
                }

                SetRamOptimizingVisualState(isOptimizing);
                UpdateMemoryTooltipFooter();
            }));
        }

        private void OnRamOptimizationCompleted(MemoryOptimizationResult result)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isClosing)
                {
                    return;
                }

                UpdateMemoryBreakdownUi(HardwareMonitorService.Instance.CurrentSnapshot);

                if (result.Succeeded)
                {
                    FlashRamSuccessCheckmark();
                }
            }));
        }

        private void SetRamOptimizingVisualState(bool isOptimizing)
        {
            BtnOptimizeRam.IsEnabled = !isOptimizing;
            if (isOptimizing)
            {
                RamCheckIcon.Visibility = Visibility.Collapsed;
                RamCleanIcon.Visibility = Visibility.Visible;
                var pulse = new DoubleAnimation(1.0, 0.35, TimeSpan.FromMilliseconds(350))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                };
                OptGlyphHost.BeginAnimation(OpacityProperty, pulse);
            }
            else
            {
                OptGlyphHost.BeginAnimation(OpacityProperty, null);
                OptGlyphHost.Opacity = 1.0;
            }
        }

        private void FlashRamSuccessCheckmark()
        {
            _ramFeedbackTimer?.Stop();
            RamCleanIcon.Visibility = Visibility.Collapsed;
            RamCheckIcon.Visibility = Visibility.Visible;

            _ramFeedbackTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            _ramFeedbackTimer.Tick += (_, _) =>
            {
                _ramFeedbackTimer?.Stop();
                if (!_isClosing)
                {
                    RamCheckIcon.Visibility = Visibility.Collapsed;
                    RamCleanIcon.Visibility = Visibility.Visible;
                }
            };
            _ramFeedbackTimer.Start();
        }

        private void UpdateMemoryBreakdownUi(HardwareSnapshot snapshot)
        {
            MemoryBreakdown? mem = snapshot.Memory;
            if (mem == null && MemoryTelemetryService.TryReadBreakdown(out var live))
            {
                mem = live;
            }

            if (mem == null || mem.TotalUsableBytes == 0)
            {
                RamBarInUseCol.Width = new GridLength(0, GridUnitType.Star);
                RamBarStandbyCol.Width = new GridLength(0, GridUnitType.Star);
                RamBarFreeCol.Width = new GridLength(1, GridUnitType.Star);

                TxtTipInstalled.Text = LocalizationService.Format("Ram_InstalledFormat", "--");
                TxtTipInUse.Text = "--";
                TxtTipAvailable.Text = "--";
                TxtTipFree.Text = "--";
                TxtTipStandby.Text = "--";
                TxtTipModified.Text = "--";
                TxtTipCached.Text = "--";
                TxtTipReserved.Text = "--";
                TipBarInUseCol.Width = new GridLength(0, GridUnitType.Star);
                TipBarModifiedCol.Width = new GridLength(0, GridUnitType.Star);
                TipBarStandbyCol.Width = new GridLength(0, GridUnitType.Star);
                TipBarFreeCol.Width = new GridLength(1, GridUnitType.Star);
                UpdateMemoryTooltipFooter();
                return;
            }

            double totalUsable = mem.TotalUsableBytes;
            double rowInUseRatio = mem.InUseBytes / totalUsable;
            double rowStandbyRatio = mem.HasPageListBreakdown
                ? Math.Min(mem.StandbyBytes / totalUsable, Math.Max(0.0, 1.0 - rowInUseRatio))
                : 0.0;
            double rowFreeRatio = Math.Max(0.0, 1.0 - rowInUseRatio - rowStandbyRatio);

            RamBarInUseCol.Width = new GridLength(rowInUseRatio, GridUnitType.Star);
            RamBarStandbyCol.Width = new GridLength(rowStandbyRatio, GridUnitType.Star);
            RamBarFreeCol.Width = new GridLength(rowFreeRatio, GridUnitType.Star);

            ulong barModified = mem.HasPageListBreakdown ? mem.ModifiedBytes : 0UL;
            ulong barStandby = mem.HasPageListBreakdown ? mem.StandbyBytes : 0UL;
            ulong barFree = mem.HasPageListBreakdown ? mem.FreeBytes : mem.AvailableBytes;
            double barDenom = Math.Max(1.0, (double)mem.InUseBytes + barModified + barStandby + barFree);

            TipBarInUseCol.Width = new GridLength(mem.InUseBytes / barDenom, GridUnitType.Star);
            TipBarModifiedCol.Width = new GridLength(barModified / barDenom, GridUnitType.Star);
            TipBarStandbyCol.Width = new GridLength(barStandby / barDenom, GridUnitType.Star);
            TipBarFreeCol.Width = new GridLength(barFree / barDenom, GridUnitType.Star);

            TxtTipInstalled.Text = LocalizationService.Format(
                "Ram_InstalledFormat",
                MemoryTelemetryService.FormatBytes(mem.InstalledBytes));

            int inUsePct = (int)Math.Round(mem.InUsePercent);
            int availPct = Math.Max(0, 100 - inUsePct);

            TxtTipInUse.Text = $"{MemoryTelemetryService.FormatBytes(mem.InUseBytes)} ({inUsePct}%)";
            TxtTipAvailable.Text = $"{MemoryTelemetryService.FormatBytes(mem.AvailableBytes)} ({availPct}%)";
            TxtTipFree.Text = mem.HasPageListBreakdown ? MemoryTelemetryService.FormatBytes(mem.FreeBytes) : "--";
            TxtTipStandby.Text = mem.HasPageListBreakdown ? MemoryTelemetryService.FormatBytes(mem.StandbyBytes) : "--";
            TxtTipModified.Text = mem.HasPageListBreakdown ? MemoryTelemetryService.FormatBytes(mem.ModifiedBytes) : "--";
            TxtTipCached.Text = mem.HasPageListBreakdown ? MemoryTelemetryService.FormatBytes(mem.CachedBytes) : "--";
            TxtTipReserved.Text = MemoryTelemetryService.FormatBytes(mem.HardwareReservedBytes);

            UpdateMemoryTooltipFooter();
        }

        private void UpdateMemoryTooltipFooter()
        {
            if (TxtTipFooter == null)
            {
                return;
            }

            TxtTipFooter.Inlines.Clear();
            var optimizer = MemoryOptimizerService.Instance;

            if (optimizer.IsOptimizing)
            {
                TxtTipFooter.Inlines.Add(new System.Windows.Documents.Run(
                    LocalizationService.Get("Ram_FooterOptimizing"))
                {
                    Foreground = RamAccentBrush,
                    FontWeight = FontWeights.SemiBold
                });
                return;
            }

            if (optimizer.LastResult is { RequiresElevation: true })
            {
                TxtTipFooter.Inlines.Add(new System.Windows.Documents.Run(
                    LocalizationService.Get("Ram_FooterAdminRequired"))
                {
                    Foreground = RamWarnBrush
                });
                return;
            }

            var settings = SettingsService.Instance.Current;
            if ((!settings.RamFreeWorkingSets && !settings.RamClearStandby) ||
                optimizer.LastResult is { NoScopeSelected: true })
            {
                TxtTipFooter.Inlines.Add(new System.Windows.Documents.Run(
                    LocalizationService.Get("Ram_FooterNoScope"))
                {
                    Foreground = RamWarnBrush
                });
                return;
            }

            if (optimizer.LastSuccessfulResult is { } lastOk)
            {
                string prefix = LocalizationService.Get("Ram_LastFreedPrefix").TrimEnd();
                string atTime = LocalizationService.Format(
                    "Ram_LastFreedAt",
                    lastOk.TimestampLocal.ToString("hh:mm tt", CultureInfo.CurrentCulture)).TrimStart();

                TxtTipFooter.Inlines.Add(new System.Windows.Documents.Run($"{prefix} ")
                {
                    Foreground = RamFooterTextBrush
                });
                TxtTipFooter.Inlines.Add(new System.Windows.Documents.Run(
                    MemoryTelemetryService.FormatBytes(lastOk.FreedBytes))
                {
                    Foreground = RamAccentBrush,
                    FontWeight = FontWeights.SemiBold
                });
                TxtTipFooter.Inlines.Add(new System.Windows.Documents.Run($" {atTime}")
                {
                    Foreground = RamFooterTextBrush
                });
                return;
            }

            TxtTipFooter.Inlines.Add(new System.Windows.Documents.Run(
                LocalizationService.Get("Ram_FooterHint"))
            {
                Foreground = RamFooterMutedBrush
            });
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void BtnInstallPawnIo_Click(object sender, RoutedEventArgs e)
        {
            HardwareMonitorService.InstallOrOpenPawnIo();
        }

        private void CpuTempHoverZone_MouseEnter(object sender, MouseEventArgs e)
        {
            UpdateCpuCoreItems(HardwareMonitorService.Instance.CurrentSnapshot);
            CpuCoresPopup.IsOpen = true;
        }

        private void CpuTempHoverZone_MouseLeave(object sender, MouseEventArgs e)
        {
            CpuCoresPopup.IsOpen = false;
        }

        private void BtnCpuTempCores_Click(object sender, RoutedEventArgs e)
        {
            if (CpuCoresPopup.IsOpen)
            {
                CpuCoresPopup.IsOpen = false;
                return;
            }

            UpdateCpuCoreItems(HardwareMonitorService.Instance.CurrentSnapshot);
            CpuCoresPopup.IsOpen = true;
        }

        private void BtnCpuTempCores_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!ShowKeyboardFocusCues)
            {
                return;
            }

            UpdateCpuCoreItems(HardwareMonitorService.Instance.CurrentSnapshot);
            CpuCoresPopup.IsOpen = true;
        }

        private void BtnCpuTempCores_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            CpuCoresPopup.IsOpen = false;
        }

        private void UpdateCpuCoreItems(HardwareSnapshot snapshot)
        {
            TxtCpuPackagePowerPopup.Text = FormatPackagePower(snapshot.CpuPackageWatts);

            var cores = snapshot.CpuCoresTelemetry;
            if (cores.Count == 0)
            {
                CoreItems.Clear();
                TxtCpuCoresUnavailable.Visibility = Visibility.Visible;
                PositionCorePopup();
                return;
            }

            TxtCpuCoresUnavailable.Visibility = Visibility.Collapsed;

            var s = SettingsService.Instance.Current;
            bool isFahrenheit = s.TemperatureUnit == "Fahrenheit";
            string unit = isFahrenheit ? "°F" : "°C";

            CoreColumns = cores.Count <= 16 ? 4 : 6;

            CoreItems.Clear();
            foreach (var core in cores)
            {
                int displayTemp = isFahrenheit
                    ? (int)Math.Round((core.DisplayTemp * 9 / 5.0) + 32)
                    : core.DisplayTemp;

                CoreItems.Add(new CoreReadout
                {
                    Text = $"{core.Name} {displayTemp}{unit}",
                    IsHottest = core.IsHottest
                });
            }

            PositionCorePopup();
        }

        private static string FormatPackagePower(int? watts)
        {
            string value = watts.HasValue
                ? watts.Value.ToString(CultureInfo.InvariantCulture)
                : "--";
            return LocalizationService.Format("Main_PackagePower", value);
        }

        internal static string CompactGpuName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
            {
                return "";
            }

            string trimmed = fullName.Trim();
            string[] prefixes =
            {
                "NVIDIA ",
                "AMD ",
                "Intel(R) ",
                "Intel ",
                "Qualcomm(R) ",
                "Qualcomm "
            };

            foreach (string prefix in prefixes)
            {
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed[prefix.Length..].Trim();
                }
            }

            return trimmed;
        }

        private void ApplyTelemetryVisibility(AppSettings settings)
        {
            CpuLargeChartContainer.Visibility = settings.ShowCpuGraph && !settings.CpuGraphSmall
                ? Visibility.Visible
                : Visibility.Collapsed;
            CpuSmallChartRow.Visibility = settings.ShowCpuGraph && settings.CpuGraphSmall
                ? Visibility.Visible
                : Visibility.Collapsed;
            CpuSmallChartSeparator.Visibility = CpuSmallChartRow.Visibility;

            CpuSpeedRow.Visibility = settings.ShowCpuClock
                ? Visibility.Visible
                : Visibility.Collapsed;
            CpuSpeedSeparator.Visibility = CpuSpeedRow.Visibility;
            SsdRowsContainer.Visibility = settings.ShowDiskTemp
                ? Visibility.Visible
                : Visibility.Collapsed;

            TxtCpuPackageWatts.Visibility = settings.ShowCpuPower
                ? Visibility.Visible
                : Visibility.Collapsed;
            CpuPackagePowerFooter.Visibility = settings.ShowCpuPower
                ? Visibility.Visible
                : Visibility.Collapsed;
            TxtGpuTemp.Visibility = settings.ShowGpuTemp
                ? Visibility.Visible
                : Visibility.Collapsed;
            TxtGpuPower.Visibility = settings.ShowGpuPower
                ? Visibility.Visible
                : Visibility.Collapsed;
            TxtGpuName.Visibility = settings.ShowGpuName &&
                !string.IsNullOrWhiteSpace(TxtGpuName.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
            GpuHotspotGroup.Visibility = settings.ShowGpuHotspot
                ? Visibility.Visible
                : Visibility.Collapsed;
            GpuVramGroup.Visibility = settings.ShowGpuVram
                ? Visibility.Visible
                : Visibility.Collapsed;
            GpuFanGroup.Visibility = settings.ShowGpuFan
                ? Visibility.Visible
                : Visibility.Collapsed;
            GpuHealthRow.Visibility = settings.ShowGpuHotspot || settings.ShowGpuVram || settings.ShowGpuFan
                ? Visibility.Visible
                : Visibility.Collapsed;
            GpuDetailsRow.Visibility = settings.ShowGpuDetails
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void CpuCoresPopup_Opened(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PositionCorePopup));
        }

        private void PositionCorePopup()
        {
            if (!CpuCoresPopup.IsOpen)
            {
                return;
            }

            if (CpuCoresPopup.Child is not FrameworkElement popupRoot)
            {
                return;
            }

            popupRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double popupWidth = popupRoot.DesiredSize.Width;
            double popupHeight = popupRoot.DesiredSize.Height;
            if (popupWidth <= 0 || popupHeight <= 0)
            {
                return;
            }

            double desiredOffsetX = CpuTempHoverZone.ActualWidth - popupWidth;
            var placement = PlacementMode.Top;

            if (TryGetMonitorWorkArea(out Rect workArea))
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                Point targetScreen = CpuTempHoverZone.PointToScreen(new Point(0, 0));
                double targetLeft = targetScreen.X / dpi.DpiScaleX;
                double targetTop = targetScreen.Y / dpi.DpiScaleY;

                double popupLeft = targetLeft + desiredOffsetX;
                double minLeft = workArea.Left + 4;
                double maxLeft = Math.Max(minLeft, workArea.Right - popupWidth - 4);
                popupLeft = Math.Clamp(popupLeft, minLeft, maxLeft);

                bool fitsAbove = targetTop - popupHeight - 6 >= workArea.Top + 4;
                if (!fitsAbove)
                {
                    placement = PlacementMode.Bottom;
                }

                CpuCoresPopup.Placement = placement;
                CpuCoresPopup.HorizontalOffset = popupLeft - targetLeft;
                CpuCoresPopup.VerticalOffset = 6;
                return;
            }

            CpuCoresPopup.Placement = PlacementMode.Top;
            CpuCoresPopup.HorizontalOffset = desiredOffsetX;
            CpuCoresPopup.VerticalOffset = 6;
        }

        private bool TryGetMonitorWorkArea(out Rect workArea)
        {
            workArea = Rect.Empty;

            try
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero)
                {
                    return false;
                }

                var screen = WinForms.Screen.FromHandle(handle);
                var dpi = VisualTreeHelper.GetDpi(this);
                var bounds = screen.WorkingArea;

                workArea = new Rect(
                    bounds.Left / dpi.DpiScaleX,
                    bounds.Top / dpi.DpiScaleY,
                    bounds.Width / dpi.DpiScaleX,
                    bounds.Height / dpi.DpiScaleY);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void OnLanguageChanged()
        {
            LocalizationService.Instance.ApplyWindow(this);
            _ssdRowSignature = null;
            ApplySettings();
            UpdateTheme(PowerPlanService.Instance.IsCoolingModeActive, false);
            UpdateTelemetryUI(HardwareMonitorService.Instance.CurrentSnapshot);
        }

        private void OnThemeChanged(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _ssdRowSignature = null;
                UpdateTheme(PowerPlanService.Instance.IsCoolingModeActive, false);
                UpdateTitleIconStates();
                UpdateTelemetryUI(HardwareMonitorService.Instance.CurrentSnapshot);
            }));
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
