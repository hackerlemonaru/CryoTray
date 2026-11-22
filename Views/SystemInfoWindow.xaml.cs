using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Threading.Tasks;
using CryoTray.Models;
using CryoTray.Services;
using Microsoft.Win32;

namespace CryoTray.Views
{
    public partial class SystemInfoWindow : Window
    {
        private SystemInfoData _data = new();
        private readonly DispatcherTimer _uptimeTimer;
        private TextBlock? _uptimeTextBlock;
        private StackPanel? _fanHealthRowsPanel;
        private ToggleButton? _fanHealthToggleButton;
        private TextBlock? _fanHealthChevron;
        private CheckBox? _fanHealthIdleFilter;
        private Button? _fanHealthTestButton;
        private bool _fanHealthExpanded;
        private bool _fanHealthHideIdle = true;
        private bool _syncingFanHealthToggle;
        private DispatcherTimer? _copyFeedbackTimer;
        private DispatcherTimer? _processTimer;
        private ToggleButton? _processToggleButton;
        private TextBlock? _processChevron;
        private StackPanel? _processPanel;
        private StackPanel? _processRowsHost;
        private bool _processExpanded;
        private bool _processRefreshInFlight;
        private readonly Dictionary<string, Border> _fanHealthRows = new(StringComparer.OrdinalIgnoreCase);
        private string? _highlightFanKey;
        private bool _highlightScrolled;

        public SystemInfoWindow()
        {
            InitializeComponent();
            LocalizationService.Instance.ApplyWindow(this);

            _uptimeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _uptimeTimer.Tick += (s, e) =>
            {
                if (_uptimeTextBlock != null)
                {
                    _uptimeTextBlock.Text = SystemInfoQueryService.FormatUptime(
                        TimeSpan.FromMilliseconds(Environment.TickCount64));
                }

                RefreshFanHealthRows();
            };

            _processTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _processTimer.Tick += ProcessTimer_Tick;

            Loaded += SystemInfoWindow_Loaded;
            Closed += SystemInfoWindow_Closed;
            PreviewKeyDown += SystemInfoWindow_PreviewKeyDown;
            LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
            ThemeService.ThemeChanged += OnThemeChanged;
            SettingsService.Instance.SettingsChanged += OnNotificationSettingsChanged;
        }

        private void SystemInfoWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        }

        private void SystemInfoWindow_Closed(object? sender, EventArgs e)
        {
            Loaded -= SystemInfoWindow_Loaded;
            Closed -= SystemInfoWindow_Closed;
            PreviewKeyDown -= SystemInfoWindow_PreviewKeyDown;
            LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
            ThemeService.ThemeChanged -= OnThemeChanged;
            SettingsService.Instance.SettingsChanged -= OnNotificationSettingsChanged;
            _uptimeTimer.Stop();
            _uptimeTextBlock = null;
            _fanHealthRowsPanel = null;
            _fanHealthToggleButton = null;
            _fanHealthChevron = null;
            _fanHealthIdleFilter = null;
            _fanHealthTestButton = null;
            _fanHealthRows.Clear();
            _copyFeedbackTimer?.Stop();
            _copyFeedbackTimer = null;
            _processTimer?.Stop();
            _processTimer = null;
            _processToggleButton = null;
            _processChevron = null;
            _processPanel = null;
            _processRowsHost = null;
            InfoContainer.Children.Clear();
            _data = new SystemInfoData();
            Content = null;
            DataContext = null;

            if (System.Windows.Application.Current is App app)
            {
                app.TrimWorkingSet();
            }
        }

        private void SystemInfoWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _data = SystemInfoQueryService.Instance.QuerySystemInfo();
            RenderRows();
            _uptimeTimer.Start();
        }

        private void OnThemeChanged(object? sender, EventArgs e)
        {
            if (Dispatcher.CheckAccess())
            {
                RenderRows();
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(RenderRows));
            }
        }

        private void OnLanguageChanged()
        {
            LocalizationService.Instance.ApplyWindow(this);
            _copyFeedbackTimer?.Stop();
            TxtCopyDiagnostics.Text = LocalizationService.Get("System_Copy");
            _data = SystemInfoQueryService.Instance.QuerySystemInfo();
            RenderRows();
        }

        private void RenderRows()
        {
            _processToggleButton = null;
            _processChevron = null;
            _processPanel = null;
            _processRowsHost = null;
            InfoContainer.Children.Clear();
            _fanHealthRowsPanel = null;
            _fanHealthToggleButton = null;
            _fanHealthChevron = null;
            _fanHealthIdleFilter = null;
            _fanHealthTestButton = null;
            _fanHealthRows.Clear();
            int rowIndex = 0;

            void AddRow(string label, string value, string? subtitle = null, bool isUptime = false, string? detail = null)
            {
                bool isAlternate = (rowIndex % 2 == 1);
                var border = new Border
                {
                    Background = isAlternate ? ThemeService.GetBrush("AlternateRowBrush") : Brushes.Transparent,
                    CornerRadius = new CornerRadius(0),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 0, 0)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // Label
                var txtLabel = new TextBlock
                {
                    Text = label,
                    Foreground = ThemeService.GetBrush("TextSecondaryBrush"),
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Top,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                Grid.SetColumn(txtLabel, 0);
                grid.Children.Add(txtLabel);

                // Value stack
                var valStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var txtValue = new TextBlock
                {
                    Text = value,
                    Foreground = ThemeService.GetBrush("TextPrimaryBrush"),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    FlowDirection = System.Windows.FlowDirection.LeftToRight
                };
                valStack.Children.Add(txtValue);

                if (isUptime)
                {
                    _uptimeTextBlock = txtValue;
                }

                if (!string.IsNullOrEmpty(subtitle))
                {
                    var txtSub = new TextBlock
                    {
                        Text = subtitle,
                        Foreground = ThemeService.GetBrush("TextSecondaryBrush"),
                        FontSize = 12,
                        Margin = new Thickness(0, 2, 0, 0),
                        TextWrapping = TextWrapping.Wrap
                    };
                    valStack.Children.Add(txtSub);
                }

                if (!string.IsNullOrEmpty(detail))
                {
                    var txtDetail = new TextBlock
                    {
                        Text = detail,
                        Foreground = ThemeService.GetBrush("TextMutedBrush"),
                        FontSize = 11.5,
                        Margin = new Thickness(0, 2, 0, 0),
                        TextWrapping = TextWrapping.Wrap
                    };
                    valStack.Children.Add(txtDetail);
                }

                Grid.SetColumn(valStack, 1);
                grid.Children.Add(valStack);

                border.Child = grid;
                InfoContainer.Children.Add(border);
                rowIndex++;
            }

            AddRow(LocalizationService.Get("System_Pc"), _data.PcName);
            AddRow(LocalizationService.Get("System_Processor"), _data.ProcessorName, _data.ProcessorCores);
            AddRow(LocalizationService.Get("System_Memory"), _data.Memory);
            AddRow(LocalizationService.Get("System_GraphicsCard"), _data.GraphicsCard);

            foreach (var d in _data.StorageDevices)
            {
                AddRow(d.Header, d.Title, d.Subtitle, false, d.FreeSpace);
            }

            AddRow(LocalizationService.Get("System_OperatingSystem"), _data.OperatingSystem);
            AddRow(LocalizationService.Get("System_Uptime"), _data.WindowsUptime, null, true);
            AddRow(
                LocalizationService.Get("System_StartupSpeed"),
                _data.StartupSpeed,
                detail: LocalizationService.Get("System_StartupSpeedDetail"));
            AddRow(
                LocalizationService.Get("System_TempStats"),
                _data.TempStats,
                detail: LocalizationService.Get("System_TempStatsDetail"));
            bool showFanSpeeds = SettingsService.Instance.Current.ShowFanSpeeds;
            if (showFanSpeeds || !string.IsNullOrWhiteSpace(_highlightFanKey))
            {
                if (showFanSpeeds)
                {
                    AddRow(LocalizationService.Get("System_FanSpeeds"), _data.FanSpeeds);
                }

                AddFanHealthSection();
            }
            AddProcessSection();
            AddRow(LocalizationService.Get("System_Monitors"), _data.Monitors);
            AddRow(LocalizationService.Get("System_Network"), _data.Network);
            AddRow(LocalizationService.Get("System_SystemErrors"), _data.SystemErrors);
        }

        private void AddFanHealthSection()
        {
            var headerContent = new Grid();
            headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _fanHealthChevron = new TextBlock
            {
                Text = _fanHealthExpanded ? "▾" : "›",
                Foreground = ThemeService.GetBrush("TextTertiaryBrush"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_fanHealthChevron, 0);
            headerContent.Children.Add(_fanHealthChevron);

            var headerLabel = new TextBlock
            {
                Text = LocalizationService.Get("System_FanHealth"),
                Foreground = ThemeService.GetBrush("TextPrimaryBrush"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(headerLabel, 1);
            headerContent.Children.Add(headerLabel);

            _fanHealthToggleButton = new ToggleButton
            {
                Style = (Style)FindResource("FanHealthHeaderToggleStyle"),
                Content = headerContent,
                IsChecked = _fanHealthExpanded
            };
            AutomationProperties.SetName(
                _fanHealthToggleButton, LocalizationService.Get("System_FanHealth"));
            AutomationProperties.SetAutomationId(_fanHealthToggleButton, "FanHealthToggle");
            _fanHealthToggleButton.Checked += FanHealthToggle_Changed;
            _fanHealthToggleButton.Unchecked += FanHealthToggle_Changed;
            InfoContainer.Children.Add(_fanHealthToggleButton);

            _fanHealthIdleFilter = new CheckBox
            {
                Content = LocalizationService.Get("System_FanHealthHideIdle"),
                Style = (Style)FindResource("FanHealthIdleFilterStyle"),
                IsChecked = _fanHealthHideIdle,
                Visibility = _fanHealthExpanded ? Visibility.Visible : Visibility.Collapsed
            };
            AutomationProperties.SetAutomationId(_fanHealthIdleFilter, "FanHealthHideIdle");
            AutomationProperties.SetName(
                _fanHealthIdleFilter, LocalizationService.Get("System_FanHealthHideIdle"));
            _fanHealthIdleFilter.Checked += FanHealthIdleFilter_Changed;
            _fanHealthIdleFilter.Unchecked += FanHealthIdleFilter_Changed;
            InfoContainer.Children.Add(_fanHealthIdleFilter);

            _fanHealthRowsPanel = new StackPanel
            {
                Visibility = _fanHealthExpanded ? Visibility.Visible : Visibility.Collapsed
            };
            AutomationProperties.SetAutomationId(_fanHealthRowsPanel, "FanHealthRows");
            InfoContainer.Children.Add(_fanHealthRowsPanel);

            _fanHealthTestButton = new Button
            {
                Content = LocalizationService.Get("System_TestFanAlert"),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = ThemeService.GetBrush("AccentTextBrush"),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 4, 12, 10),
                FontSize = 12.5,
                IsEnabled = !SettingsService.Instance.Current.DisableAllNotifications,
                Visibility = _fanHealthExpanded ? Visibility.Visible : Visibility.Collapsed
            };
            AutomationProperties.SetAutomationId(_fanHealthTestButton, "FanHealthTestButton");
            _fanHealthTestButton.Click += BtnTestFanNotification_Click;
            InfoContainer.Children.Add(_fanHealthTestButton);

            ApplyFanHealthExpansionState();
        }

        public void HighlightFan(string fanKey)
        {
            _highlightFanKey = fanKey;
            _highlightScrolled = false;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _fanHealthExpanded = true;
                    ApplyFanHealthExpansionState();
                }));
                return;
            }

            _fanHealthExpanded = true;
            ApplyFanHealthExpansionState();
        }

        private void FanHealthIdleFilter_Changed(object sender, RoutedEventArgs e)
        {
            _fanHealthHideIdle = _fanHealthIdleFilter?.IsChecked == true;
            RefreshFanHealthRows();
        }

        private void FanHealthToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingFanHealthToggle)
            {
                return;
            }

            _fanHealthExpanded = _fanHealthToggleButton?.IsChecked == true;
            ApplyFanHealthExpansionState();
        }

        private void ApplyFanHealthExpansionState()
        {
            _syncingFanHealthToggle = true;
            try
            {
                if (_fanHealthToggleButton != null &&
                    _fanHealthToggleButton.IsChecked != _fanHealthExpanded)
                {
                    _fanHealthToggleButton.IsChecked = _fanHealthExpanded;
                }
            }
            finally
            {
                _syncingFanHealthToggle = false;
            }

            if (_fanHealthChevron != null)
            {
                _fanHealthChevron.Text = _fanHealthExpanded ? "▾" : "›";
            }

            if (_fanHealthIdleFilter != null)
            {
                _fanHealthIdleFilter.Visibility = _fanHealthExpanded
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (_fanHealthRowsPanel != null)
            {
                _fanHealthRowsPanel.Visibility = _fanHealthExpanded
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (_fanHealthTestButton != null)
            {
                _fanHealthTestButton.Visibility = _fanHealthExpanded
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (_fanHealthExpanded)
            {
                RefreshFanHealthRows();
            }
        }

        private void AddProcessSection()
        {
            var headerContent = new Grid();
            headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _processChevron = new TextBlock
            {
                Text = _processExpanded ? "▾" : "›",
                Foreground = ThemeService.GetBrush("TextTertiaryBrush"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_processChevron, 0);
            headerContent.Children.Add(_processChevron);

            var headerLabel = new TextBlock
            {
                Text = LocalizationService.Get("System_Processes"),
                Foreground = ThemeService.GetBrush("TextPrimaryBrush"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(headerLabel, 1);
            headerContent.Children.Add(headerLabel);

            _processToggleButton = new ToggleButton
            {
                Style = (Style)FindResource("FanHealthHeaderToggleStyle"),
                Content = headerContent,
                IsChecked = _processExpanded
            };
            AutomationProperties.SetName(
                _processToggleButton, LocalizationService.Get("System_Processes"));
            AutomationProperties.SetAutomationId(_processToggleButton, "ProcessUsageToggle");
            _processToggleButton.Checked += ProcessToggle_Changed;
            _processToggleButton.Unchecked += ProcessToggle_Changed;
            InfoContainer.Children.Add(_processToggleButton);

            _processPanel = new StackPanel
            {
                Visibility = _processExpanded ? Visibility.Visible : Visibility.Collapsed
            };
            _processPanel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Get("System_ProcessesDesc"),
                Foreground = ThemeService.GetBrush("TextMutedBrush"),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 2, 12, 8)
            });

            _processRowsHost = new StackPanel();
            _processRowsHost.Children.Add(CreateProcessMessage("System_ProcessesMeasuring"));
            _processPanel.Children.Add(_processRowsHost);
            InfoContainer.Children.Add(_processPanel);

            ApplyProcessExpansionState();
        }

        private void ProcessToggle_Changed(object sender, RoutedEventArgs e)
        {
            _processExpanded = _processToggleButton?.IsChecked == true;
            ApplyProcessExpansionState();
        }

        private void ApplyProcessExpansionState()
        {
            if (_processChevron != null)
            {
                _processChevron.Text = _processExpanded ? "▾" : "›";
            }

            if (_processPanel != null)
            {
                _processPanel.Visibility = _processExpanded
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (_processExpanded)
            {
                if (_processRowsHost != null && _processRowsHost.Children.Count == 0)
                {
                    _processRowsHost.Children.Add(CreateProcessMessage("System_ProcessesMeasuring"));
                }

                _processTimer?.Start();
                _ = RefreshProcessRowsAsync();
            }
            else
            {
                _processTimer?.Stop();
                _processRowsHost?.Children.Clear();
            }
        }

        private async void ProcessTimer_Tick(object? sender, EventArgs e)
        {
            await RefreshProcessRowsAsync();
        }

        private async Task RefreshProcessRowsAsync()
        {
            if (!_processExpanded || _processRowsHost == null || _processRefreshInFlight)
            {
                return;
            }

            _processRefreshInFlight = true;
            var host = _processRowsHost;
            try
            {
                var snapshot = await ProcessUsageService.CaptureAsync();
                if (!_processExpanded || host != _processRowsHost || !IsLoaded)
                {
                    return;
                }

                RenderProcessRows(host, snapshot);
            }
            catch
            {
            }
            finally
            {
                _processRefreshInFlight = false;
            }
        }

        private void RenderProcessRows(StackPanel host, ProcessUsageSnapshot snapshot)
        {
            host.Children.Clear();

            if (!snapshot.CpuMeasured && snapshot.Entries.Count == 0)
            {
                host.Children.Add(CreateProcessMessage("System_ProcessesMeasuring"));
                return;
            }

            if (snapshot.Entries.Count == 0)
            {
                host.Children.Add(CreateProcessMessage("System_ProcessesEmpty"));
                return;
            }

            host.Children.Add(CreateProcessHeaderRow());
            int index = 0;
            foreach (var entry in snapshot.Entries)
            {
                host.Children.Add(CreateProcessRow(entry, snapshot, index));
                index++;
            }

            if (!snapshot.GpuAvailable)
            {
                host.Children.Add(CreateProcessMessage("System_ProcessesGpuUnavailable"));
            }
        }

        private Grid CreateProcessHeaderRow()
        {
            var grid = CreateProcessGrid();
            grid.Margin = new Thickness(12, 0, 12, 4);
            AddProcessCell(grid, 0, LocalizationService.Get("System_ProcessesProcess"),
                ThemeService.GetBrush("TextMutedBrush"), TextAlignment.Left, false);
            AddProcessCell(grid, 1, LocalizationService.Get("System_ProcessesCpu"),
                ThemeService.GetBrush("TextMutedBrush"), TextAlignment.Right, false);
            AddProcessCell(grid, 2, LocalizationService.Get("System_ProcessesGpu"),
                ThemeService.GetBrush("TextMutedBrush"), TextAlignment.Right, false);
            AddProcessCell(grid, 3, LocalizationService.Get("System_ProcessesMemory"),
                ThemeService.GetBrush("TextMutedBrush"), TextAlignment.Right, false);
            return grid;
        }

        private Border CreateProcessRow(
            ProcessUsageEntry entry, ProcessUsageSnapshot snapshot, int index)
        {
            var border = new Border
            {
                Background = index % 2 == 1
                    ? ThemeService.GetBrush("AlternateRowBrush")
                    : Brushes.Transparent,
                Padding = new Thickness(12, 6, 12, 6)
            };

            var grid = CreateProcessGrid();
            grid.Margin = new Thickness(0);

            var name = new TextBlock
            {
                Text = entry.Name,
                Foreground = ThemeService.GetBrush("TextSecondaryBrush"),
                FontSize = 12.5,
                FlowDirection = System.Windows.FlowDirection.LeftToRight,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = entry.Name,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            AddProcessCell(
                grid,
                1,
                snapshot.CpuMeasured ? $"{Math.Round(entry.CpuPercent)}%" : "--",
                ThemeService.GetBrush("TextPrimaryBrush"),
                TextAlignment.Right,
                true);
            AddProcessCell(
                grid,
                2,
                snapshot.GpuAvailable ? $"{Math.Round(entry.GpuPercent)}%" : "--",
                ThemeService.GetBrush("TextPrimaryBrush"),
                TextAlignment.Right,
                true);
            AddProcessCell(
                grid,
                3,
                entry.MemoryMb > 0 ? $"{Math.Round(entry.MemoryMb)} MB" : "--",
                ThemeService.GetBrush("TextSecondaryBrush"),
                TextAlignment.Right,
                false);

            border.Child = grid;
            return border;
        }

        private static Grid CreateProcessGrid()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            return grid;
        }

        private static void AddProcessCell(
            Grid grid,
            int column,
            string text,
            Brush brush,
            TextAlignment alignment,
            bool semiBold)
        {
            var cell = new TextBlock
            {
                Text = text,
                Foreground = brush,
                FontSize = 12.5,
                FontWeight = semiBold ? FontWeights.SemiBold : FontWeights.Normal,
                TextAlignment = alignment,
                VerticalAlignment = VerticalAlignment.Center,
                FlowDirection = System.Windows.FlowDirection.LeftToRight,
                Margin = new Thickness(column == 0 ? 0 : 6, 0, 0, 0)
            };
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }

        private static TextBlock CreateProcessMessage(string key)
        {
            return new TextBlock
            {
                Text = LocalizationService.Get(key),
                Foreground = ThemeService.GetBrush("TextMutedBrush"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 2, 12, 8)
            };
        }

        private void RefreshFanHealthRows()
        {
            if (_fanHealthRowsPanel == null || !_fanHealthExpanded)
            {
                return;
            }

            _fanHealthRowsPanel.Children.Clear();
            _fanHealthRows.Clear();

            if (!SettingsService.Instance.Current.FanHealthAlertsEnabled)
            {
                AddFanHealthMessage("System_FanHealthDisabled");
                return;
            }

            var statuses = (System.Windows.Application.Current as App)?.FanHealth?.GetStatuses();
            if (statuses == null || statuses.Count == 0)
            {
                AddFanHealthMessage("System_FanHealthNoData");
                return;
            }

            int index = 0;
            foreach (var status in statuses)
            {
                bool isHighlighted = string.Equals(
                    status.Key, _highlightFanKey, StringComparison.OrdinalIgnoreCase);
                if (_fanHealthHideIdle &&
                    status.State == FanHealthState.IdleAtZero &&
                    !isHighlighted)
                {
                    continue;
                }

                var row = CreateFanHealthRow(status, index++);
                _fanHealthRows[status.Key] = row;
                _fanHealthRowsPanel.Children.Add(row);
            }

            if (index == 0)
            {
                AddFanHealthMessage("System_FanHealthNoActive");
                return;
            }

            if (string.IsNullOrWhiteSpace(_highlightFanKey) ||
                !_fanHealthRows.TryGetValue(_highlightFanKey, out var highlighted))
            {
                return;
            }

            highlighted.Background = ThemeService.GetBrush("HighlightRowBrush");
            highlighted.BorderBrush = ThemeService.GetBrush("HighlightRowBorderBrush");
            highlighted.BorderThickness = new Thickness(1);
            if (!_highlightScrolled)
            {
                _highlightScrolled = true;
                highlighted.Dispatcher.BeginInvoke(new Action(() => highlighted.BringIntoView()));
            }
        }

        private void AddFanHealthMessage(string key)
        {
            if (_fanHealthRowsPanel == null)
            {
                return;
            }

            _fanHealthRowsPanel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Get(key),
                Foreground = ThemeService.GetBrush("TextMutedBrush"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 6, 12, 8)
            });
        }

        private Border CreateFanHealthRow(FanHealthStatus status, int index)
        {
            var border = new Border
            {
                Background = index % 2 == 1
                    ? ThemeService.GetBrush("AlternateRowBrush")
                    : Brushes.Transparent,
                Padding = new Thickness(12, 8, 12, 8)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = FanHealthService.GetDisplayName(status),
                Foreground = ThemeService.GetBrush("TextSecondaryBrush"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Top,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var valStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            valStack.Children.Add(new TextBlock
            {
                Text = $"{status.Rpm} {LocalizationService.Get("Main_RpmUnit")}",
                Foreground = ThemeService.GetBrush("TextPrimaryBrush"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                FlowDirection = System.Windows.FlowDirection.LeftToRight
            });
            valStack.Children.Add(new TextBlock
            {
                Text = GetFanStateLabel(status.State),
                Foreground = ThemeService.GetBrush("TextSecondaryBrush"),
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            if (status.State == FanHealthState.Watching && status.SpinningSinceUtc.HasValue)
            {
                double remaining = FanHealthService.SpinArmDuration.TotalSeconds -
                                   (DateTime.UtcNow - status.SpinningSinceUtc.Value).TotalSeconds;
                valStack.Children.Add(new TextBlock
                {
                    Text = LocalizationService.Format(
                        "System_FanHealthArming",
                        Math.Max(0, (int)Math.Ceiling(remaining))),
                    Foreground = ThemeService.GetBrush("TextMutedBrush"),
                    FontSize = 11.5,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }
            else if (status.State == FanHealthState.Stopped && status.ZeroSinceUtc.HasValue && !status.LastAlertUtc.HasValue)
            {
                double remaining = FanHealthService.StoppedAlertDuration.TotalSeconds -
                                   (DateTime.UtcNow - status.ZeroSinceUtc.Value).TotalSeconds;
                if (remaining > 0)
                {
                    valStack.Children.Add(new TextBlock
                    {
                        Text = LocalizationService.Format(
                            "System_FanHealthStopPending",
                            (int)Math.Ceiling(remaining)),
                        Foreground = ThemeService.GetBrush("WarningTextBrush"),
                        FontSize = 11.5,
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                }
            }

            if (status.LastAlertUtc.HasValue)
            {
                valStack.Children.Add(new TextBlock
                {
                    Text = LocalizationService.Format(
                        "System_FanHealthLastAlert",
                        status.LastAlertUtc.Value.ToLocalTime().ToString("HH:mm:ss")),
                    Foreground = ThemeService.GetBrush("TextMutedBrush"),
                    FontSize = 11.5,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            Grid.SetColumn(valStack, 1);
            grid.Children.Add(valStack);
            border.Child = grid;
            return border;
        }

        private static string GetFanStateLabel(FanHealthState state)
        {
            return state switch
            {
                FanHealthState.Watching => LocalizationService.Get("System_FanHealthStateWatching"),
                FanHealthState.Armed => LocalizationService.Get("System_FanHealthStateArmed"),
                FanHealthState.IdleAtZero => LocalizationService.Get("System_FanHealthStateIdle"),
                _ => LocalizationService.Get("System_FanHealthStateStopped")
            };
        }

        private void BtnCopyDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var statuses = (System.Windows.Application.Current as App)?.FanHealth?.GetStatuses();
                string snapshot = SystemInfoQueryService.Instance.GenerateDiagnosticsSnapshot(
                    _data,
                    HardwareMonitorService.Instance.CurrentSnapshot,
                    statuses,
                    SettingsService.Instance.Current.TemperatureUnit == "Fahrenheit");
                Clipboard.SetText(snapshot);
                ShowCopyFeedback("System_Copied");
            }
            catch
            {
                ShowCopyFeedback("System_CopyFailed");
            }
        }

        private void ShowCopyFeedback(string key)
        {
            _copyFeedbackTimer?.Stop();
            TxtCopyDiagnostics.Text = LocalizationService.Get(key);
            _copyFeedbackTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.6)
            };
            _copyFeedbackTimer.Tick += (s, e) =>
            {
                _copyFeedbackTimer?.Stop();
                TxtCopyDiagnostics.Text = LocalizationService.Get("System_Copy");
            };
            _copyFeedbackTimer.Start();
        }

        private void BtnTestFanNotification_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsService.Instance.Current.DisableAllNotifications)
            {
                return;
            }

            (System.Windows.Application.Current as App)?.ShowTrayNotification(
                LocalizationService.Get("Alert_FanTitle"),
                LocalizationService.Get("Alert_FanTestBody"),
                null,
                System.Windows.Forms.ToolTipIcon.Info);
        }

        private void OnNotificationSettingsChanged()
        {
            if (_fanHealthTestButton != null)
            {
                _fanHealthTestButton.IsEnabled =
                    !SettingsService.Instance.Current.DisableAllNotifications;
            }
        }

        private void BtnSaveTo_Click(object sender, RoutedEventArgs e)
        {
            var sfd = new SaveFileDialog
            {
                Filter = LocalizationService.Get("Common_TextFilter"),
                FileName = $"CryoTray_SystemInfo_{Environment.MachineName}.txt"
            };

            if (sfd.ShowDialog() == true)
            {
                string report = SystemInfoQueryService.Instance.GenerateTextReport(_data);
                File.WriteAllText(sfd.FileName, report);
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
