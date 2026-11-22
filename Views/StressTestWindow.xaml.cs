using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CryoTray.Models;
using CryoTray.Services;
using Microsoft.Win32;

namespace CryoTray.Views
{
    public partial class StressTestWindow : Window
    {
        private bool _isRunning;
        private bool _isClosing;
        private CancellationTokenSource? _testCts;
        private readonly DispatcherTimer _timer;
        private Stopwatch _stopwatch = new();
        private readonly List<double> _testTemps = new();
        private readonly List<bool> _testCoolingStates = new();
        private readonly List<TelemetrySample> _recordedSamples = new();
        private long[] _workerIterations = Array.Empty<long>();
        private long _lastIterationTotal;
        private double _lastIterationSampleSeconds;

        internal readonly record struct StressCoolingComparison(
            bool HasOffPhase,
            double OffTemp,
            double OffWatts,
            bool HasOnPhase,
            double OnTemp,
            double OnWatts,
            double DeltaTemp,
            double DeltaWatts,
            double OffIterationsPerSecond,
            double OnIterationsPerSecond,
            double PerformanceRetainedPercent);

        private const int DefaultFpsReference = 100;
        internal static readonly int[] FpsPresets = { 60, 100, 120, 144, 165, 240 };
        private int _fpsReference = DefaultFpsReference;
        private bool _isRestoringFpsReference;

        private static Brush PhaseOffChartBrush => ThemeService.GetBrush("CoolingOffTextBrush");
        private static Brush PhaseOnChartBrush => ThemeService.GetBrush("AccentBrush");
        private static Brush DeltaGoodBrush => ThemeService.GetBrush("SuccessTextBrush");
        private static Brush DeltaWarmBrush => ThemeService.GetBrush("CoolingOffTextBrush");
        private static Brush DeltaHintBrush => ThemeService.GetBrush("TextSecondaryBrush");

        private static Brush CreateFrozenBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        public StressTestWindow()
        {
            InitializeComponent();
            LocalizationService.Instance.ApplyWindow(this);

            int savedFpsReference = NormalizeFpsReference(
                SettingsService.Instance.Current.StressFpsReference);
            _fpsReference = savedFpsReference;
            _isRestoringFpsReference = true;
            CmbFpsReference.SelectedIndex = Array.IndexOf(FpsPresets, savedFpsReference);
            _isRestoringFpsReference = false;

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += Timer_Tick;

            Loaded += StressTestWindow_Loaded;
            Closing += StressTestWindow_Closing;
            Closed += StressTestWindow_Closed;
            PreviewKeyDown += StressTestWindow_PreviewKeyDown;
            LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
            ThemeService.ThemeChanged += OnThemeChanged;
        }

        private void StressTestWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !_isRunning)
            {
                e.Handled = true;
                Close();
            }
        }

        private void StressTestWindow_Loaded(object sender, RoutedEventArgs e)
        {
            HardwareMonitorService.Instance.TelemetryUpdated += OnTelemetry;
            PowerPlanService.Instance.CoolingModeChanged += OnCoolingChanged;
            UpdateCoolingDisplay(PowerPlanService.Instance.IsCoolingModeActive);
            ApplyTelemetrySnapshot(HardwareMonitorService.Instance.CurrentSnapshot);
        }

        private void StressTestWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _isClosing = true;
            StopTest();
        }

        private void StressTestWindow_Closed(object? sender, EventArgs e)
        {
            _isClosing = true;
            StopTest();
            Loaded -= StressTestWindow_Loaded;
            Closing -= StressTestWindow_Closing;
            Closed -= StressTestWindow_Closed;
            PreviewKeyDown -= StressTestWindow_PreviewKeyDown;
            LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
            ThemeService.ThemeChanged -= OnThemeChanged;
            HardwareMonitorService.Instance.TelemetryUpdated -= OnTelemetry;
            PowerPlanService.Instance.CoolingModeChanged -= OnCoolingChanged;
            _timer.Stop();
            _testCts?.Dispose();
            _testCts = null;
            DataContext = null;
            Content = null;
        }

        private void OnCoolingChanged(bool active)
        {
            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isClosing)
                {
                    UpdateCoolingDisplay(active);
                }
            }));
        }

        private void OnThemeChanged(object? sender, EventArgs e)
        {
            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isClosing)
                {
                    UpdateCoolingDisplay(PowerPlanService.Instance.IsCoolingModeActive);
                    DrawChart();
                }
            }));
        }

        private void UpdateCoolingDisplay(bool active)
        {
            TxtCoolingStatus.Text = LocalizationService.Get(active ? "Common_On" : "Common_Off");
            TxtCoolingStatus.Foreground = active
                ? ThemeService.GetBrush("CoolingOnTextBrush")
                : ThemeService.GetBrush("CoolingOffTextBrush");
        }

        private void OnTelemetry(HardwareSnapshot snapshot)
        {
            if (_isClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isClosing)
                {
                    ApplyTelemetrySnapshot(snapshot);
                }
            }));
        }

        private void ApplyTelemetrySnapshot(HardwareSnapshot snapshot)
        {
            bool isFahrenheit = SettingsService.Instance.Current.TemperatureUnit == "Fahrenheit";
            string unit = isFahrenheit ? "°F" : "°C";
            bool hasTemp = double.IsFinite(snapshot.CpuTemp) && snapshot.CpuTemp > 0;
            if (hasTemp)
            {
                double displayTemperature = isFahrenheit
                    ? (snapshot.CpuTemp * 9 / 5) + 32
                    : snapshot.CpuTemp;
                TxtTestTemp.Text = $"{Math.Round(displayTemperature)} {unit}";
            }
            else
            {
                TxtTestTemp.Text = $"-- {unit}";
            }

            TxtTestSpeed.Text = double.IsFinite(snapshot.CpuGhz) && snapshot.CpuGhz > 0
                ? $"{snapshot.CpuGhz:0.00} GHz"
                : "-- GHz";

            if (_isRunning)
            {
                var sample = TelemetrySample.FromSnapshot(snapshot);
                sample.StressIterationsPerSecond = TakeIterationRate(sample.CoolingModeActive);
                _recordedSamples.Add(sample);
                BtnExportCsv.IsEnabled = _recordedSamples.Count > 0;
                if (hasTemp)
                {
                    _testTemps.Add(snapshot.CpuTemp);
                    _testCoolingStates.Add(snapshot.CoolingModeActive);
                    DrawChart();
                }
                UpdateComparisonDisplay();
            }
        }

        internal static StressCoolingComparison ComputeCoolingComparison(
            IReadOnlyList<TelemetrySample> samples,
            bool isFahrenheit)
        {
            const int trailingWindow = 5;

            var offSamples = new List<TelemetrySample>();
            var onSamples = new List<TelemetrySample>();
            for (int i = 0; i < samples.Count; i++)
            {
                var sample = samples[i];
                if (!double.IsFinite(sample.CpuTemp) || sample.CpuTemp <= 0)
                {
                    continue;
                }

                if (sample.CoolingModeActive)
                {
                    onSamples.Add(sample);
                }
                else
                {
                    offSamples.Add(sample);
                }
            }

            (bool hasOff, double offTemp, double offWatts, double offIterations) =
                SummarizePhase(offSamples, trailingWindow, isFahrenheit);
            (bool hasOn, double onTemp, double onWatts, double onIterations) =
                SummarizePhase(onSamples, trailingWindow, isFahrenheit);

            double deltaTemp = hasOff && hasOn ? onTemp - offTemp : double.NaN;
            double deltaWatts = hasOff && hasOn && double.IsFinite(offWatts) && double.IsFinite(onWatts)
                ? onWatts - offWatts
                : double.NaN;
            double performanceRetained = hasOff && hasOn &&
                                         double.IsFinite(offIterations) && offIterations > 0 &&
                                         double.IsFinite(onIterations) && onIterations > 0
                ? onIterations / offIterations * 100.0
                : double.NaN;

            return new StressCoolingComparison(
                hasOff, offTemp, offWatts,
                hasOn, onTemp, onWatts,
                deltaTemp, deltaWatts,
                offIterations, onIterations, performanceRetained);
        }

        private static (bool HasSamples, double AvgTemp, double AvgWatts, double AvgIterations) SummarizePhase(
            List<TelemetrySample> samples,
            int trailingWindow,
            bool isFahrenheit)
        {
            if (samples.Count == 0)
            {
                return (false, double.NaN, double.NaN, double.NaN);
            }

            int start = Math.Max(0, samples.Count - trailingWindow);
            int count = samples.Count - start;
            double tempSum = 0;
            double wattsSum = 0;
            int wattsCount = 0;
            double iterationsSum = 0;
            int iterationsCount = 0;

            for (int i = start; i < samples.Count; i++)
            {
                double temp = isFahrenheit ? (samples[i].CpuTemp * 9 / 5) + 32 : samples[i].CpuTemp;
                tempSum += temp;

                int? cpuPower = samples[i].CpuPowerWatts;
                if (cpuPower.HasValue && cpuPower.Value > 0)
                {
                    wattsSum += cpuPower.Value;
                    wattsCount++;
                }

                double iterations = samples[i].StressIterationsPerSecond;
                if (double.IsFinite(iterations) && iterations > 0)
                {
                    iterationsSum += iterations;
                    iterationsCount++;
                }
            }

            double avgTemp = tempSum / count;
            double avgWatts = wattsCount > 0 ? wattsSum / wattsCount : double.NaN;
            double avgIterations = iterationsCount > 0 ? iterationsSum / iterationsCount : double.NaN;
            return (true, avgTemp, avgWatts, avgIterations);
        }

        private double TakeIterationRate(bool coolingModeActive)
        {
            double nowSeconds = _stopwatch.Elapsed.TotalSeconds;
            long totalIterations = 0;
            var counts = _workerIterations;
            for (int i = 0; i < counts.Length; i++)
            {
                totalIterations += Volatile.Read(ref counts[i]);
            }

            double elapsedSeconds = nowSeconds - _lastIterationSampleSeconds;
            long deltaIterations = totalIterations - _lastIterationTotal;
            bool phaseChanged = _recordedSamples.Count > 0 &&
                                _recordedSamples[_recordedSamples.Count - 1].CoolingModeActive != coolingModeActive;

            _lastIterationSampleSeconds = nowSeconds;
            _lastIterationTotal = totalIterations;

            if (elapsedSeconds < 0.5 || deltaIterations < 0 || phaseChanged)
            {
                return double.NaN;
            }

            return deltaIterations / elapsedSeconds;
        }

        private void UpdateComparisonDisplay()
        {
            bool isFahrenheit = SettingsService.Instance.Current.TemperatureUnit == "Fahrenheit";
            string unit = isFahrenheit ? "°F" : "°C";
            var cmp = ComputeCoolingComparison(_recordedSamples, isFahrenheit);

            TxtPhaseOff.Text = cmp.HasOffPhase
                ? FormatPhaseText(cmp.OffTemp, cmp.OffWatts, unit)
                : "--";
            TxtPhaseOn.Text = cmp.HasOnPhase
                ? FormatPhaseText(cmp.OnTemp, cmp.OnWatts, unit)
                : "--";

            if (cmp.HasOffPhase && cmp.HasOnPhase)
            {
                long deltaTempRounded = (long)Math.Round(cmp.DeltaTemp);
                string tempSign = deltaTempRounded > 0 ? "+" : "";
                string deltaText = $"{tempSign}{deltaTempRounded} {unit}";
                if (double.IsFinite(cmp.DeltaWatts))
                {
                    long deltaWattsRounded = (long)Math.Round(cmp.DeltaWatts);
                    string wattSign = deltaWattsRounded > 0 ? "+" : "";
                    deltaText += $" · {wattSign}{deltaWattsRounded} W";
                }

                TxtPhaseDelta.Text = deltaText;
                TxtPhaseDelta.Foreground = cmp.DeltaTemp <= 0 ? DeltaGoodBrush : DeltaWarmBrush;
            }
            else
            {
                TxtPhaseDelta.Text = LocalizationService.Get("Stress_DeltaHint");
                TxtPhaseDelta.Foreground = DeltaHintBrush;
            }

            if (double.IsFinite(cmp.PerformanceRetainedPercent))
            {
                long retainedPercent = (long)Math.Round(cmp.PerformanceRetainedPercent);
                TxtPerformanceRetained.Text = LocalizationService.Format(
                    "Stress_PerformanceRetained",
                    retainedPercent);

                var fpsImpact = ComputeFpsImpact(cmp.PerformanceRetainedPercent, _fpsReference);
                TxtFpsImpact.Text = LocalizationService.Format(
                    "Stress_FpsImpact",
                    _fpsReference,
                    fpsImpact.ProjectedFps,
                    fpsImpact.LostFps);
            }
            else
            {
                TxtPerformanceRetained.Text = string.Empty;
                TxtFpsImpact.Text = string.Empty;
            }
        }

        internal static (long ProjectedFps, long LostFps) ComputeFpsImpact(
            double retainedPercent,
            int referenceFps = DefaultFpsReference)
        {
            if (!double.IsFinite(retainedPercent) || referenceFps <= 0)
            {
                return (0, 0);
            }

            double clampedPercent = Math.Clamp(retainedPercent, 0.0, 100.0);
            long projected = (long)Math.Round(referenceFps * clampedPercent / 100.0);
            return (projected, referenceFps - projected);
        }

        internal static int NormalizeFpsReference(int fps)
        {
            return Array.IndexOf(FpsPresets, fps) >= 0 ? fps : DefaultFpsReference;
        }

        private void CmbFpsReference_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRestoringFpsReference)
            {
                return;
            }

            int index = CmbFpsReference.SelectedIndex;
            if (index < 0 || index >= FpsPresets.Length)
            {
                return;
            }

            _fpsReference = FpsPresets[index];
            var settings = SettingsService.Instance.Current;
            if (settings.StressFpsReference != _fpsReference)
            {
                settings.StressFpsReference = _fpsReference;
                SettingsService.Instance.Save();
            }

            UpdateComparisonDisplay();
        }

        private static string FormatPhaseText(double temp, double watts, string unit)
        {
            string tempPart = $"{Math.Round(temp)} {unit}";
            return double.IsFinite(watts) && watts > 0
                ? $"{tempPart} · {Math.Round(watts)} W"
                : tempPart;
        }

        private void DrawChart()
        {
            ChartCanvas.Children.Clear();
            double w = ChartCanvas.ActualWidth;
            double h = ChartCanvas.ActualHeight;
            if (w <= 0 || h <= 0 || _testTemps.Count < 2) return;

            // Draw horizontal reference grid
            for (int i = 1; i <= 3; i++)
            {
                double y = (h / 4.0) * i;
                var line = new Line
                {
                    X1 = 0,
                    X2 = w,
                    Y1 = y,
                    Y2 = y,
                    Stroke = ThemeService.GetBrush("ChartGridBrush"),
                    StrokeThickness = 1
                };
                ChartCanvas.Children.Add(line);
            }

            double minT = 30;
            double maxT = 95;

            int maxCount = 60;
            int startIdx = Math.Max(0, _testTemps.Count - maxCount);
            int count = _testTemps.Count - startIdx;
            double stepX = w / Math.Max(1, count - 1);

            double prevX = 0;
            double prevY = 0;
            for (int i = 0; i < count; i++)
            {
                double t = _testTemps[startIdx + i];
                double norm = (t - minT) / (maxT - minT);
                norm = Math.Clamp(norm, 0, 1);
                double y = h - (norm * (h - 10) + 5);
                double x = i * stepX;

                if (i > 0)
                {
                    bool segmentCoolingOn = startIdx + i < _testCoolingStates.Count && _testCoolingStates[startIdx + i];
                    ChartCanvas.Children.Add(new Line
                    {
                        X1 = prevX,
                        Y1 = prevY,
                        X2 = x,
                        Y2 = y,
                        Stroke = segmentCoolingOn ? PhaseOnChartBrush : PhaseOffChartBrush,
                        StrokeThickness = 2,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    });
                }

                prevX = x;
                prevY = y;
            }
        }

        private void BtnStartStop_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRunning)
            {
                StartTest();
            }
            else
            {
                StopTest();
            }
        }

        private void StartTest()
        {
            _isRunning = true;
            _testTemps.Clear();
            _testCoolingStates.Clear();
            _recordedSamples.Clear();
            _lastIterationTotal = 0;
            _lastIterationSampleSeconds = 0;
            _recordedSamples.Add(TelemetrySample.FromSnapshot(HardwareMonitorService.Instance.CurrentSnapshot));
            BtnExportCsv.IsEnabled = _recordedSamples.Count > 0;
            UpdateComparisonDisplay();
            _stopwatch.Restart();
            _timer.Start();

            BtnStartStop.Content = LocalizationService.Get("Stress_Stop");
            BtnStartStop.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D32F2F"));

            _testCts?.Cancel();
            _testCts?.Dispose();
            _testCts = new CancellationTokenSource();
            var token = _testCts.Token;

            // Use dedicated BelowNormal OS threads so full CPU load never starves ThreadPool polling.
            int cores = Environment.ProcessorCount;
            var iterationCounts = new long[cores];
            _workerIterations = iterationCounts;
            for (int c = 0; c < cores; c++)
            {
                int workerIndex = c;
                var worker = new Thread(() =>
                {
                    double value = 1.0 + workerIndex;
                    long iterations = 0;
                    while (!token.IsCancellationRequested)
                    {
                        value = Math.Sqrt(value + 1.0) + Math.Sin(value) * 1e-9;
                        iterations++;
                        if ((iterations & 0x3FF) == 0)
                        {
                            Volatile.Write(ref iterationCounts[workerIndex], iterations);
                        }
                    }

                    Volatile.Write(ref iterationCounts[workerIndex], iterations);
                })
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                    Name = $"CryoTray-Stress-{c}"
                };
                worker.Start();
            }
        }

        private void StopTest()
        {
            if (!_isRunning) return;

            _isRunning = false;
            var cts = _testCts;
            _testCts = null;
            try
            {
                cts?.Cancel();
            }
            finally
            {
                cts?.Dispose();
            }
            _stopwatch.Stop();
            _timer.Stop();

            BtnStartStop.Content = LocalizationService.Get("Stress_Start");
            BtnStartStop.Background = ThemeService.GetBrush("AccentBrush");
        }

        private void OnLanguageChanged()
        {
            LocalizationService.Instance.ApplyWindow(this);
            UpdateCoolingDisplay(PowerPlanService.Instance.IsCoolingModeActive);
            UpdateComparisonDisplay();
            BtnStartStop.Content = LocalizationService.Get(_isRunning ? "Stress_Stop" : "Stress_Start");
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            TxtElapsed.Text = _stopwatch.Elapsed.ToString(@"mm\:ss");
        }

        private void BtnToggleCooling_Click(object sender, RoutedEventArgs e)
        {
            PowerPlanService.Instance.ToggleCoolingMode();
        }

        private void BtnExportCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_recordedSamples.Count == 0)
            {
                MessageBox.Show(
                    LocalizationService.Get("Main_CsvNoSamples"),
                    LocalizationService.Get("Stress_Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var sfd = new SaveFileDialog
            {
                Filter = LocalizationService.Get("Common_CsvFilter"),
                FileName = $"CryoTray_StressTest_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (sfd.ShowDialog(this) == true)
            {
                var exportCopy = new List<TelemetrySample>(_recordedSamples);
                TelemetryCsvService.ExportSamples(exportCopy, sfd.FileName);
                MessageBox.Show(
                    LocalizationService.Format("Main_CsvExportDone", exportCopy.Count, sfd.FileName),
                    LocalizationService.Get("Stress_Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
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
