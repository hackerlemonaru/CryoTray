using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;
using CryoTray.Models;
using CryoTray.Services;

namespace CryoTray.Tray
{
    public class TrayIconManager : IDisposable
    {
        private const int IconSize = 32;
        private const int IconHalfHeight = IconSize / 2;
        private const string CheckableMenuItemTag = "check-toggle";
        private const int MainMenuFloorWidth = 212;
        private const int SubMenuFloorWidth = 196;
        private const int MenuTextHorizontalReserve = 64;

        private readonly NotifyIcon _gpuIcon;
        private readonly NotifyIcon _cpuIcon;
        private readonly ContextMenuStrip _contextMenu;
        private readonly Font _menuFont = new("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font _menuPrimaryFont = new("Segoe UI Semibold", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly ToolStripMenuItem _itemOpen = null!;
        private readonly ToolStripMenuItem _itemCooling = null!;
        private readonly ToolStripMenuItem _itemPauseCooling = null!;
        private readonly ToolStripMenuItem _itemPause15m = null!;
        private readonly ToolStripMenuItem _itemPause30m = null!;
        private readonly ToolStripMenuItem _itemPause60m = null!;
        private readonly ToolStripMenuItem _itemResumeNow = null!;
        private readonly ToolStripMenuItem _itemStress = null!;
        private readonly ToolStripMenuItem _itemOverlayClickThrough = null!;
        private readonly ToolStripMenuItem _itemSettings = null!;
        private readonly ToolStripMenuItem _itemExit = null!;

        private readonly Bitmap _iconBitmap;
        private readonly Graphics _iconGraphics;
        private readonly SolidBrush _backgroundBrush;
        private readonly SolidBrush _textBrush;
        private readonly SolidBrush _whiteTextBrush;
        private readonly SolidBrush _dividerBrush;
        private readonly Font _singleDigitFont;
        private readonly Font _doubleDigitFont;
        private readonly Font _tripleDigitFont;
        private readonly Font _mergedFont;
        private readonly StringFormat _centerFormat;
        private readonly GraphicsPath _roundedPath;

        private Icon? _currentGpuIcon;
        private Icon? _currentCpuIcon;
        private IntPtr _currentGpuHIcon;
        private IntPtr _currentCpuHIcon;
        private Action? _notificationAction;
        private NotifyIcon? _notificationIcon;
        private DateTime _notificationHoldUntilUtc;
        private DateTime? _coolingPausedUntilUtc;
        private double _lastGpuTemp = double.NaN;
        private double _lastCpuTemp = double.NaN;
        private double _lastGpuLoad = double.NaN;
        private double _lastCpuLoad = double.NaN;
        private double _lastRamPercent = double.NaN;
        private bool _lastCoolingModeActive;
        private string _lastBadgeSignature = "";
        private bool _disposed;
        private readonly object _renderLock = new();

        public event Action? OnOpenDashboardRequested;
        public event Action? OnToggleCoolingRequested;
        public event Action<TimeSpan>? OnPauseCoolingRequested;
        public event Action? OnCancelCoolingPauseRequested;
        public event Action? OnOpenStressTestRequested;
        public event Action? OnToggleOverlayClickThroughRequested;
        public event Action? OnOpenSettingsRequested;
        public event Action? OnExitRequested;

        // Camomile tray badge palette (pastel for black digits, deeper contrast-safe tones for white digits).
        private static readonly Color GpuGreen = ColorTranslator.FromHtml("#B3D872");
        private static readonly Color CpuAmber = ColorTranslator.FromHtml("#FFBDAF");
        private static readonly Color CpuCyan = ColorTranslator.FromHtml("#8AD2F1");
        private static readonly Color MergedCpuOff = ColorTranslator.FromHtml("#E5987E");
        private static readonly Color MergedCpuOn = ColorTranslator.FromHtml("#4FC3F7");
        private static readonly Color MergedGpuGreen = ColorTranslator.FromHtml("#7CB342");
        private static readonly Color GpuGreenWhite = ColorTranslator.FromHtml("#4C8028");
        private static readonly Color CpuAmberWhite = ColorTranslator.FromHtml("#C44830");
        private static readonly Color CpuCyanWhite = ColorTranslator.FromHtml("#1B6CB3");
        private static readonly Color DividerColor = ColorTranslator.FromHtml("#141414");

        public TrayIconManager()
        {
            var menuItems = CreateDarkContextMenu();
            _contextMenu = menuItems.Menu;
            _itemOpen = menuItems.Open;
            _itemCooling = menuItems.Cooling;
            _itemPauseCooling = menuItems.PauseCooling;
            _itemPause15m = menuItems.Pause15m;
            _itemPause30m = menuItems.Pause30m;
            _itemPause60m = menuItems.Pause60m;
            _itemResumeNow = menuItems.ResumeNow;
            _itemStress = menuItems.Stress;
            _itemOverlayClickThrough = menuItems.OverlayClickThrough;
            _itemSettings = menuItems.Settings;
            _itemExit = menuItems.Exit;
            _contextMenu.Opening += (_, _) =>
            {
                _itemOverlayClickThrough.Checked = SettingsService.Instance.Current.OverlayClickThrough;
                RefreshPauseMenuText();
            };

            _iconBitmap = new Bitmap(IconSize, IconSize);
            _iconGraphics = Graphics.FromImage(_iconBitmap);
            _iconGraphics.SmoothingMode = SmoothingMode.AntiAlias;
            _iconGraphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            _iconGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            _iconGraphics.CompositingQuality = CompositingQuality.HighQuality;

            _backgroundBrush = new SolidBrush(Color.Transparent);
            _textBrush = new SolidBrush(Color.FromArgb(20, 20, 20));
            _whiteTextBrush = new SolidBrush(Color.White);
            _dividerBrush = new SolidBrush(DividerColor);
            _singleDigitFont = new Font("Segoe UI", 22f, FontStyle.Bold, GraphicsUnit.Pixel);
            _doubleDigitFont = new Font("Segoe UI", 18f, FontStyle.Bold, GraphicsUnit.Pixel);
            _tripleDigitFont = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
            _mergedFont = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
            _centerFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.None
            };
            _roundedPath = CreateRoundedPath();

            _gpuIcon = new NotifyIcon
            {
                Text = "CryoTray",
                Visible = true,
                ContextMenuStrip = _contextMenu
            };
            _gpuIcon.MouseClick += OnTrayIconClick;
            _gpuIcon.BalloonTipClicked += OnBalloonTipClicked;

            _cpuIcon = new NotifyIcon
            {
                Text = "CryoTray",
                Visible = true,
                ContextMenuStrip = _contextMenu
            };
            _cpuIcon.MouseClick += OnTrayIconClick;
            _cpuIcon.BalloonTipClicked += OnBalloonTipClicked;

            SettingsService.Instance.SettingsChanged += OnSettingsChanged;

            ApplyLanguage();
            UpdateBadges(double.NaN, double.NaN, PowerPlanService.Instance.IsCoolingModeActive, true);
        }

        private static GraphicsPath CreateRoundedPath()
        {
            const int radius = 5;
            var path = new GraphicsPath();
            path.AddArc(0, 0, radius * 2, radius * 2, 180, 90);
            path.AddArc(IconSize - (radius * 2), 0, radius * 2, radius * 2, 270, 90);
            path.AddArc(IconSize - (radius * 2), IconSize - (radius * 2),
                radius * 2, radius * 2, 0, 90);
            path.AddArc(0, IconSize - (radius * 2), radius * 2, radius * 2, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void OnSettingsChanged()
        {
            if (_disposed)
            {
                return;
            }

            UpdateBadges(_lastGpuTemp, _lastCpuTemp, _lastCoolingModeActive, true,
                _lastGpuLoad, _lastCpuLoad, _lastRamPercent);
        }

        public void ApplyLanguage()
        {
            RightToLeft rtl = LocalizationService.Instance.IsArabic
                ? RightToLeft.Yes
                : RightToLeft.No;
            _contextMenu.RightToLeft = rtl;
            _itemPauseCooling.DropDown.RightToLeft = rtl;

            _itemOpen.Text = LocalizationService.Get("Tray_OpenDashboard");
            _itemCooling.Text = LocalizationService.Get("Tray_ToggleCooling");
            _itemPause15m.Text = LocalizationService.Get("Tray_Pause15m");
            _itemPause30m.Text = LocalizationService.Get("Tray_Pause30m");
            _itemPause60m.Text = LocalizationService.Get("Tray_Pause60m");
            _itemResumeNow.Text = LocalizationService.Get("Tray_ResumeCoolingNow");
            RefreshPauseMenuText();
            _itemStress.Text = LocalizationService.Get("Tray_CpuTest");
            _itemOverlayClickThrough.Text = LocalizationService.Get("Tray_OverlayClickThrough");
            _itemOverlayClickThrough.Checked = SettingsService.Instance.Current.OverlayClickThrough;
            _itemSettings.Text = LocalizationService.Get("Tray_Settings");
            _itemExit.Text = LocalizationService.Get("Tray_Exit");

            UpdateDropDownSizes(_contextMenu, MainMenuFloorWidth, MenuTextHorizontalReserve);
            UpdateDropDownSizes(_itemPauseCooling.DropDown, SubMenuFloorWidth, MenuTextHorizontalReserve);
            UpdateBadges(_lastGpuTemp, _lastCpuTemp, _lastCoolingModeActive, true,
                _lastGpuLoad, _lastCpuLoad, _lastRamPercent);
        }

        public void ApplyTheme()
        {
            DarkMenuRenderer.ApplyTheme(ThemeService.IsLight);
            ApplyMenuTheme(_contextMenu);
            ApplyMenuTheme(_itemPauseCooling.DropDown);
            _contextMenu.Invalidate(true);
        }

        private static void ApplyMenuTheme(ToolStripDropDown dropDown)
        {
            dropDown.BackColor = DarkMenuRenderer.SurfaceBg;
            dropDown.ForeColor = DarkMenuRenderer.TextNormal;

            foreach (ToolStripItem item in dropDown.Items)
            {
                item.ForeColor = item.Enabled
                    ? DarkMenuRenderer.TextNormal
                    : DarkMenuRenderer.TextDisabled;
                if (item is ToolStripMenuItem menuItem && menuItem.DropDown.Items.Count > 0)
                {
                    ApplyMenuTheme(menuItem.DropDown);
                }
            }
        }

        public void SetCoolingPauseUntil(DateTime? pauseUntilUtc)
        {
            lock (_renderLock)
            {
                _coolingPausedUntilUtc = pauseUntilUtc;
            }

            RefreshPauseMenuText();
            UpdateBadges(_lastGpuTemp, _lastCpuTemp, _lastCoolingModeActive, false,
                _lastGpuLoad, _lastCpuLoad, _lastRamPercent);
        }

        private int GetPauseRemainingMinutes()
        {
            DateTime? until;
            lock (_renderLock)
            {
                until = _coolingPausedUntilUtc;
            }

            if (!until.HasValue || until.Value <= DateTime.UtcNow)
            {
                return 0;
            }

            return Math.Max(1, (int)Math.Ceiling((until.Value - DateTime.UtcNow).TotalMinutes));
        }

        private void RefreshPauseMenuText()
        {
            if (_disposed)
            {
                return;
            }

            if (_contextMenu.InvokeRequired)
            {
                try
                {
                    _contextMenu.BeginInvoke(new Action(RefreshPauseMenuText));
                }
                catch
                {
                }
                return;
            }

            int mins = GetPauseRemainingMinutes();
            _itemPauseCooling.Text = mins > 0
                ? LocalizationService.Format("Tray_PauseCoolingActive", mins)
                : LocalizationService.Get("Tray_PauseCooling");
            _itemResumeNow.Enabled = mins > 0;
            UpdateDropDownSizes(_contextMenu, MainMenuFloorWidth, MenuTextHorizontalReserve);
        }

        public void ShowNotification(string title, string message, Action? onClick = null,
            ToolTipIcon icon = ToolTipIcon.Info)
        {
            NotifyIcon notifyIcon = _cpuIcon.Visible
                ? _cpuIcon
                : _gpuIcon.Visible
                    ? _gpuIcon
                    : _cpuIcon;

            notifyIcon.Visible = true;
            _notificationIcon = notifyIcon;
            _notificationHoldUntilUtc = DateTime.UtcNow.AddSeconds(12);

            _notificationAction = onClick;
            notifyIcon.BalloonTipTitle = title;
            notifyIcon.BalloonTipText = message;
            notifyIcon.BalloonTipIcon = icon;
            notifyIcon.ShowBalloonTip(10000);
        }

        private void OnBalloonTipClicked(object? sender, EventArgs e)
        {
            Action? action = _notificationAction;
            _notificationAction = null;
            action?.Invoke();
        }

        private void OnTrayIconClick(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                OnOpenDashboardRequested?.Invoke();
            }
        }

        private sealed record DarkContextMenuItems(
            ContextMenuStrip Menu,
            ToolStripMenuItem Open,
            ToolStripMenuItem Cooling,
            ToolStripMenuItem PauseCooling,
            ToolStripMenuItem Pause15m,
            ToolStripMenuItem Pause30m,
            ToolStripMenuItem Pause60m,
            ToolStripMenuItem ResumeNow,
            ToolStripMenuItem Stress,
            ToolStripMenuItem OverlayClickThrough,
            ToolStripMenuItem Settings,
            ToolStripMenuItem Exit);

        private DarkContextMenuItems CreateDarkContextMenu()
        {
            var renderer = new DarkMenuRenderer();
            var menu = new ContextMenuStrip();
            ConfigureDarkDropDown(menu, renderer);

            var itemOpen = CreateMenuItem("Open Dashboard", () => OnOpenDashboardRequested?.Invoke(), _menuPrimaryFont);
            var itemCooling = CreateMenuItem("Toggle Cooling Mode", () => OnToggleCoolingRequested?.Invoke());
            var itemPauseCooling = new ToolStripMenuItem("Pause Cooling Mode")
            {
                Padding = new Padding(0, 5, 0, 5)
            };
            if (itemPauseCooling.DropDown is ToolStripDropDownMenu pauseDropDown)
            {
                ConfigureDarkDropDown(pauseDropDown, renderer);
            }

            var itemPause15 = CreateMenuItem("15 minutes", () => OnPauseCoolingRequested?.Invoke(TimeSpan.FromMinutes(15)));
            var itemPause30 = CreateMenuItem("30 minutes", () => OnPauseCoolingRequested?.Invoke(TimeSpan.FromMinutes(30)));
            var itemPause60 = CreateMenuItem("1 hour", () => OnPauseCoolingRequested?.Invoke(TimeSpan.FromHours(1)));
            var itemResumeNow = CreateMenuItem("Resume Cooling Now", () => OnCancelCoolingPauseRequested?.Invoke());
            itemResumeNow.Enabled = false;
            itemPauseCooling.DropDownItems.AddRange(new ToolStripItem[]
            {
                itemPause15,
                itemPause30,
                itemPause60,
                CreateSeparator(),
                itemResumeNow
            });

            var itemStress = CreateMenuItem("CPU Temperature Test", () => OnOpenStressTestRequested?.Invoke());
            var itemOverlayClickThrough = CreateMenuItem("Click-Through Overlay", () => OnToggleOverlayClickThroughRequested?.Invoke(), tag: CheckableMenuItemTag);
            var itemSettings = CreateMenuItem("Settings", () => OnOpenSettingsRequested?.Invoke());
            var itemExit = CreateMenuItem("Exit", () => OnExitRequested?.Invoke());

            menu.Items.AddRange(new ToolStripItem[]
            {
                itemOpen,
                CreateSeparator(),
                itemCooling,
                itemPauseCooling,
                itemStress,
                itemOverlayClickThrough,
                itemSettings,
                CreateSeparator(),
                itemExit
            });

            return new DarkContextMenuItems(
                menu,
                itemOpen,
                itemCooling,
                itemPauseCooling,
                itemPause15,
                itemPause30,
                itemPause60,
                itemResumeNow,
                itemStress,
                itemOverlayClickThrough,
                itemSettings,
                itemExit);
        }

        private void ConfigureDarkDropDown(ToolStripDropDownMenu dropDown, ToolStripRenderer renderer)
        {
            dropDown.Renderer = renderer;
            dropDown.ShowImageMargin = false;
            dropDown.ShowCheckMargin = false;
            dropDown.BackColor = DarkMenuRenderer.SurfaceBg;
            dropDown.ForeColor = DarkMenuRenderer.TextNormal;
            dropDown.Font = _menuFont;
            dropDown.Padding = new Padding(1, 5, 1, 5);
        }

        private static ToolStripMenuItem CreateMenuItem(string text, Action onClick, Font? font = null, string? tag = null)
        {
            var item = new ToolStripMenuItem(text)
            {
                Padding = new Padding(0, 5, 0, 5),
                Tag = tag
            };
            if (font != null)
            {
                item.Font = font;
            }
            item.Click += (_, _) => onClick();
            return item;
        }

        private static ToolStripSeparator CreateSeparator() => new()
        {
            AutoSize = false,
            Height = 9,
            Margin = new Padding(0, 2, 0, 2)
        };

        private void UpdateDropDownSizes(ToolStripDropDown dropDown, int floorWidth, int horizontalExtra)
        {
            if (_disposed)
            {
                return;
            }

            int maxTextWidth = 0;
            foreach (ToolStripItem item in dropDown.Items)
            {
                if (item is ToolStripMenuItem menuItem && !string.IsNullOrEmpty(menuItem.Text))
                {
                    Size measured = TextRenderer.MeasureText(
                        menuItem.Text,
                        menuItem.Font ?? _menuFont,
                        Size.Empty,
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                    if (measured.Width > maxTextWidth)
                    {
                        maxTextWidth = measured.Width;
                    }
                }
            }

            int targetWidth = Math.Max(floorWidth, maxTextWidth + horizontalExtra);
            dropDown.MinimumSize = new Size(targetWidth, 0);

            foreach (ToolStripItem item in dropDown.Items)
            {
                if (item is ToolStripMenuItem menuItem)
                {
                    int textH = TextRenderer.MeasureText("Ag", menuItem.Font ?? _menuFont).Height;
                    int itemHeight = Math.Max(30, textH + 12);
                    menuItem.AutoSize = false;
                    menuItem.Size = new Size(targetWidth - 2, itemHeight);
                }
            }
        }

        internal (Bitmap MainMenu, Bitmap SubMenu) RenderContextMenusForTest(bool selectFirstItem, bool overlayChecked)
        {
            _itemOverlayClickThrough.Checked = overlayChecked;
            RefreshPauseMenuText();
            UpdateDropDownSizes(_contextMenu, MainMenuFloorWidth, MenuTextHorizontalReserve);
            UpdateDropDownSizes(_itemPauseCooling.DropDown, SubMenuFloorWidth, MenuTextHorizontalReserve);

            Bitmap mainBmp = RenderMenuToBitmap(_contextMenu, selectFirstItem ? _itemOpen : null);
            Bitmap subBmp = RenderMenuToBitmap(_itemPauseCooling.DropDown, selectFirstItem ? _itemPause15m : null);
            return (mainBmp, subBmp);
        }

        private static Bitmap RenderMenuToBitmap(ToolStripDropDown dropDown, ToolStripMenuItem? selectedItem)
        {
            if (!dropDown.IsHandleCreated)
            {
                dropDown.CreateControl();
            }

            dropDown.Size = dropDown.GetPreferredSize(Size.Empty);
            dropDown.PerformLayout();
            selectedItem?.Select();

            var bitmap = new Bitmap(Math.Max(1, dropDown.Width), Math.Max(1, dropDown.Height));
            dropDown.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            return bitmap;
        }

        public void UpdateBadges(double gpuTemp, double cpuTemp, bool coolingModeActive,
            bool force = false, double gpuLoad = double.NaN, double cpuLoad = double.NaN,
            double ramPercent = double.NaN)
        {
            lock (_renderLock)
            {
                if (_disposed)
                {
                    return;
                }

                var settings = SettingsService.Instance.Current;
            bool holdCpu = _notificationIcon == _cpuIcon && DateTime.UtcNow < _notificationHoldUntilUtc;
            bool holdGpu = _notificationIcon == _gpuIcon && DateTime.UtcNow < _notificationHoldUntilUtc;

            if (!holdCpu && !holdGpu)
            {
                _notificationIcon = null;
            }

            bool isFahrenheit = settings.TemperatureUnit == "Fahrenheit";
            var battery = BatteryTelemetryService.Read();
            string batteryText = settings.ShowBatteryStatus && battery.HasBattery
                ? LocalizationService.Format(
                    "Tray_BatteryTooltip",
                    battery.Percent,
                    BatteryTelemetryService.GetStateLabel(battery))
                : "";
            TrayBadgeMetric gpuMetric = NormalizeBadgeMetric(settings.GpuTrayBadgeMetric);
            TrayBadgeMetric cpuMetric = NormalizeBadgeMetric(settings.CpuTrayBadgeMetric);
            string gpuUnit = GetBadgeMetricUnit(gpuMetric, isFahrenheit);
            string cpuUnit = GetBadgeMetricUnit(cpuMetric, isFahrenheit);
            int? gpuInt = ResolveBadgeValue(gpuMetric, gpuTemp, gpuLoad, ramPercent, isFahrenheit);
            int? cpuInt = ResolveBadgeValue(cpuMetric, cpuTemp, cpuLoad, ramPercent, isFahrenheit);
            string gpuText = gpuInt.HasValue ? gpuInt.Value.ToString(CultureInfo.InvariantCulture) : "--";
            string cpuText = cpuInt.HasValue ? cpuInt.Value.ToString(CultureInfo.InvariantCulture) : "--";
            int pauseMins = (!_coolingPausedUntilUtc.HasValue || _coolingPausedUntilUtc.Value <= DateTime.UtcNow)
                ? 0
                : Math.Max(1, (int)Math.Ceiling((_coolingPausedUntilUtc.Value - DateTime.UtcNow).TotalMinutes));
            string signature = string.Join(
                "|",
                gpuText, cpuText, coolingModeActive, settings.MergeTrayIcons,
                settings.ShowGpuBadge, settings.ShowCpuBadge, isFahrenheit, holdCpu, holdGpu, pauseMins,
                settings.CpuTrayWhiteDigits, settings.GpuTrayWhiteDigits,
                cpuMetric, gpuMetric, cpuUnit, gpuUnit, batteryText);

            _lastGpuTemp = gpuTemp;
            _lastCpuTemp = cpuTemp;
            _lastGpuLoad = gpuLoad;
            _lastCpuLoad = cpuLoad;
            _lastRamPercent = ramPercent;
            _lastCoolingModeActive = coolingModeActive;

            if (!force && signature == _lastBadgeSignature)
            {
                return;
            }

            _lastBadgeSignature = signature;
            Color cpuColor = settings.CpuTrayWhiteDigits
                ? (coolingModeActive ? CpuCyanWhite : CpuAmberWhite)
                : (coolingModeActive ? CpuCyan : CpuAmber);
            Color gpuColor = settings.GpuTrayWhiteDigits ? GpuGreenWhite : GpuGreen;

            if (settings.MergeTrayIcons)
            {
                _gpuIcon.Visible = false;
                _cpuIcon.Visible = settings.ShowCpuBadge || settings.ShowGpuBadge || holdCpu || holdGpu;
                ClearGpuIcon();

                if (_cpuIcon.Visible)
                {
                    Color mergedCpuBg = settings.CpuTrayWhiteDigits
                        ? (coolingModeActive ? CpuCyanWhite : CpuAmberWhite)
                        : (coolingModeActive ? MergedCpuOn : MergedCpuOff);
                    Color mergedGpuBg = settings.GpuTrayWhiteDigits ? GpuGreenWhite : MergedGpuGreen;
                    DrawMergedBadge(cpuInt, gpuInt, mergedCpuBg, mergedGpuBg,
                        settings.CpuTrayWhiteDigits, settings.GpuTrayWhiteDigits);
                    ReplaceCpuIcon();
                    string mergedTooltip = string.Equals(cpuUnit, gpuUnit, StringComparison.Ordinal)
                        ? LocalizationService.Format(
                            "Tray_MergedTooltip", cpuText, cpuUnit, gpuText)
                        : LocalizationService.Format(
                            "Tray_MergedTooltipMixed", cpuText, cpuUnit, gpuText, gpuUnit);
                    _cpuIcon.Text = ComposeTooltip(mergedTooltip, batteryText);
                }
                else
                {
                    ClearCpuIcon();
                }

                return;
            }

            _gpuIcon.Visible = settings.ShowGpuBadge || holdGpu;
            _cpuIcon.Visible = settings.ShowCpuBadge || holdCpu;

            if (_gpuIcon.Visible)
            {
                DrawSingleBadge(gpuInt, gpuColor, settings.GpuTrayWhiteDigits);
                ReplaceGpuIcon();
                _gpuIcon.Text = ComposeTooltip(
                    LocalizationService.Format("Tray_GpuTooltip", gpuText, gpuUnit), batteryText);
            }
            else
            {
                ClearGpuIcon();
            }

            if (_cpuIcon.Visible)
            {
                DrawSingleBadge(cpuInt, cpuColor, settings.CpuTrayWhiteDigits);
                ReplaceCpuIcon();
                string coolingText = LocalizationService.Get(
                    coolingModeActive ? "Common_On" : "Common_Off");
                string cpuTooltip = pauseMins > 0 && !coolingModeActive
                    ? LocalizationService.Format("Tray_CpuTooltipPaused", cpuText, cpuUnit, pauseMins)
                    : LocalizationService.Format("Tray_CpuTooltip", cpuText, cpuUnit, coolingText);
                _cpuIcon.Text = ComposeTooltip(cpuTooltip, batteryText);
            }
            else
            {
                ClearCpuIcon();
            }
            }
        }

        private static string ComposeTooltip(string baseText, string batteryText)
        {
            string text = string.IsNullOrEmpty(batteryText)
                ? baseText
                : $"{baseText} | {batteryText}";
            const int maxLength = 63;
            return text.Length <= maxLength
                ? text
                : text[..(maxLength - 1)] + "…";
        }

        internal enum TrayBadgeMetric
        {
            Temperature,
            Load,
            Ram
        }

        internal static TrayBadgeMetric NormalizeBadgeMetric(string? value)
        {
            if (string.Equals(value, "Load", StringComparison.OrdinalIgnoreCase))
            {
                return TrayBadgeMetric.Load;
            }

            if (string.Equals(value, "Ram", StringComparison.OrdinalIgnoreCase))
            {
                return TrayBadgeMetric.Ram;
            }

            return TrayBadgeMetric.Temperature;
        }

        internal static int? ResolveBadgeValue(TrayBadgeMetric metric, double temperature,
            double load, double ramPercent, bool isFahrenheit)
        {
            double value = metric switch
            {
                TrayBadgeMetric.Load => load,
                TrayBadgeMetric.Ram => ramPercent,
                _ => double.IsFinite(temperature) && temperature > 0
                    ? (isFahrenheit ? (temperature * 9 / 5) + 32 : temperature)
                    : double.NaN
            };

            if (!double.IsFinite(value))
            {
                return null;
            }

            if (metric == TrayBadgeMetric.Temperature && value <= 0)
            {
                return null;
            }

            return (int)Math.Round(Math.Clamp(value, 0, 999));
        }

        internal static string GetBadgeMetricUnit(TrayBadgeMetric metric, bool isFahrenheit)
        {
            return metric == TrayBadgeMetric.Temperature
                ? (isFahrenheit ? "°F" : "°C")
                : "%";
        }

        private void DrawSingleBadge(int? temp, Color background, bool whiteDigits)
        {
            PrepareCanvas();
            _backgroundBrush.Color = background;
            _iconGraphics.FillRectangle(_backgroundBrush, 0, 0, IconSize, IconSize);
            DrawTemperature(temp, GetSingleDigitFont(temp),
                new RectangleF(0, 2, IconSize, IconSize - 2), whiteDigits);
        }

        private void DrawMergedBadge(int? cpuTemp, int? gpuTemp, Color cpuBackground, Color gpuBackground, bool cpuWhiteDigits, bool gpuWhiteDigits)
        {
            PrepareCanvas();
            _iconGraphics.SetClip(_roundedPath);

            _backgroundBrush.Color = cpuBackground;
            _iconGraphics.FillRectangle(_backgroundBrush, 0, 0, IconSize, IconHalfHeight);
            _backgroundBrush.Color = gpuBackground;
            _iconGraphics.FillRectangle(_backgroundBrush, 0, IconHalfHeight,
                IconSize, IconHalfHeight);
            _iconGraphics.FillRectangle(_dividerBrush, 0, IconHalfHeight - 1,
                IconSize, 1);

            DrawTemperature(cpuTemp, _mergedFont,
                new RectangleF(0, 0, IconSize, IconHalfHeight - 1), cpuWhiteDigits);
            DrawTemperature(gpuTemp, _mergedFont,
                new RectangleF(0, IconHalfHeight, IconSize, IconHalfHeight), gpuWhiteDigits);
            _iconGraphics.ResetClip();
        }

        internal Bitmap RenderSingleBadgeForTest(int? temp, bool isCpu, bool coolingModeActive, bool whiteDigits)
        {
            lock (_renderLock)
            {
                Color bg = isCpu
                    ? (whiteDigits ? (coolingModeActive ? CpuCyanWhite : CpuAmberWhite) : (coolingModeActive ? CpuCyan : CpuAmber))
                    : (whiteDigits ? GpuGreenWhite : GpuGreen);
                DrawSingleBadge(temp, bg, whiteDigits);
                return (Bitmap)_iconBitmap.Clone();
            }
        }

        internal Bitmap RenderMergedBadgeForTest(int? cpuTemp, int? gpuTemp, bool coolingModeActive, bool cpuWhiteDigits, bool gpuWhiteDigits)
        {
            lock (_renderLock)
            {
                Color cpuBg = cpuWhiteDigits
                    ? (coolingModeActive ? CpuCyanWhite : CpuAmberWhite)
                    : (coolingModeActive ? MergedCpuOn : MergedCpuOff);
                Color gpuBg = gpuWhiteDigits ? GpuGreenWhite : MergedGpuGreen;
                DrawMergedBadge(cpuTemp, gpuTemp, cpuBg, gpuBg, cpuWhiteDigits, gpuWhiteDigits);
                return (Bitmap)_iconBitmap.Clone();
            }
        }

        private void PrepareCanvas()
        {
            _iconGraphics.ResetClip();
            _iconGraphics.Clear(Color.Transparent);
        }

        private Font GetSingleDigitFont(int? temp)
        {
            if (!temp.HasValue)
            {
                return _doubleDigitFont;
            }

            if (temp.Value >= 100 || temp.Value <= -10)
            {
                return _tripleDigitFont;
            }

            return temp.Value >= 10 || temp.Value < 0 ? _doubleDigitFont : _singleDigitFont;
        }

        private void DrawTemperature(int? temp, Font font, RectangleF bounds, bool whiteDigits)
        {
            string text = temp.HasValue
                ? temp.Value.ToString(CultureInfo.InvariantCulture)
                : "--";
            Brush fg = whiteDigits ? _whiteTextBrush : _textBrush;
            _iconGraphics.DrawString(text, font, fg, bounds, _centerFormat);
        }

        private void ReplaceCpuIcon()
        {
            IntPtr newHandle = _iconBitmap.GetHicon();
            Icon newIcon = Icon.FromHandle(newHandle);
            Icon? oldIcon = _currentCpuIcon;
            IntPtr oldHandle = _currentCpuHIcon;

            _cpuIcon.Icon = newIcon;
            _currentCpuIcon = newIcon;
            _currentCpuHIcon = newHandle;

            if (oldHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(oldHandle);
            }

            oldIcon?.Dispose();
        }

        private void ReplaceGpuIcon()
        {
            IntPtr newHandle = _iconBitmap.GetHicon();
            Icon newIcon = Icon.FromHandle(newHandle);
            Icon? oldIcon = _currentGpuIcon;
            IntPtr oldHandle = _currentGpuHIcon;

            _gpuIcon.Icon = newIcon;
            _currentGpuIcon = newIcon;
            _currentGpuHIcon = newHandle;

            if (oldHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(oldHandle);
            }

            oldIcon?.Dispose();
        }

        private void ClearCpuIcon()
        {
            var oldIcon = _currentCpuIcon;
            IntPtr oldHandle = _currentCpuHIcon;
            _cpuIcon.Icon = null;
            _currentCpuIcon = null;
            _currentCpuHIcon = IntPtr.Zero;

            if (oldHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(oldHandle);
            }

            oldIcon?.Dispose();
        }

        private void ClearGpuIcon()
        {
            var oldIcon = _currentGpuIcon;
            IntPtr oldHandle = _currentGpuHIcon;
            _gpuIcon.Icon = null;
            _currentGpuIcon = null;
            _currentGpuHIcon = IntPtr.Zero;

            if (oldHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(oldHandle);
            }

            oldIcon?.Dispose();
        }

        public void Dispose()
        {
            lock (_renderLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                SettingsService.Instance.SettingsChanged -= OnSettingsChanged;

                _gpuIcon.Visible = false;
                _cpuIcon.Visible = false;
                ClearGpuIcon();
                ClearCpuIcon();
                _notificationIcon = null;

                _gpuIcon.Dispose();
                _cpuIcon.Dispose();
                _contextMenu.Dispose();
                _menuFont.Dispose();
                _menuPrimaryFont.Dispose();

                _centerFormat.Dispose();
                _roundedPath.Dispose();
                _singleDigitFont.Dispose();
                _doubleDigitFont.Dispose();
                _tripleDigitFont.Dispose();
                _mergedFont.Dispose();
                _backgroundBrush.Dispose();
                _textBrush.Dispose();
                _whiteTextBrush.Dispose();
                _dividerBrush.Dispose();
                _iconGraphics.Dispose();
                _iconBitmap.Dispose();
            }
        }

        // Custom owner-drawn renderer avoids .NET 8 ToolStripProfessionalRenderer white ImageMargin and VisualStyle hover overrides.
        private sealed class DarkMenuRenderer : ToolStripRenderer
        {
            public static Color SurfaceBg = Color.FromArgb(30, 30, 30);
            public static Color SurfaceBorder = Color.FromArgb(58, 58, 58);
            public static Color HoverFill = Color.FromArgb(45, 45, 45);
            public static Color PressedFill = Color.FromArgb(53, 53, 53);
            public static Color HoverBorder = Color.FromArgb(66, 66, 66);
            public static Color SeparatorLine = Color.FromArgb(51, 51, 51);
            public static Color TextNormal = Color.FromArgb(234, 234, 234);
            public static Color TextSelected = Color.White;
            public static Color TextDisabled = Color.FromArgb(120, 120, 120);
            public static Color ChevronNormal = Color.FromArgb(190, 190, 190);
            public static Color ChevronSelected = Color.White;
            public static Color CheckBoxFill = Color.FromArgb(20, 20, 20);
            public static Color CheckBoxUncheckedBorder = Color.FromArgb(120, 120, 120);
            public static Color CheckBoxCheckedBorder = Color.FromArgb(41, 182, 246);
            public static Color CheckMarkColor = Color.White;

            public static void ApplyTheme(bool light)
            {
                if (light)
                {
                    SurfaceBg = Color.FromArgb(255, 255, 255);
                    SurfaceBorder = Color.FromArgb(207, 199, 186);
                    HoverFill = Color.FromArgb(240, 237, 231);
                    PressedFill = Color.FromArgb(232, 228, 220);
                    HoverBorder = Color.FromArgb(185, 177, 165);
                    SeparatorLine = Color.FromArgb(221, 215, 205);
                    TextNormal = Color.FromArgb(28, 26, 23);
                    TextSelected = Color.FromArgb(28, 26, 23);
                    TextDisabled = Color.FromArgb(163, 155, 144);
                    ChevronNormal = Color.FromArgb(92, 86, 77);
                    ChevronSelected = Color.FromArgb(28, 26, 23);
                    CheckBoxFill = Color.FromArgb(255, 255, 255);
                    CheckBoxUncheckedBorder = Color.FromArgb(169, 161, 150);
                    CheckBoxCheckedBorder = Color.FromArgb(27, 108, 179);
                    CheckMarkColor = Color.FromArgb(27, 108, 179);
                    return;
                }

                SurfaceBg = Color.FromArgb(30, 30, 30);
                SurfaceBorder = Color.FromArgb(58, 58, 58);
                HoverFill = Color.FromArgb(45, 45, 45);
                PressedFill = Color.FromArgb(53, 53, 53);
                HoverBorder = Color.FromArgb(66, 66, 66);
                SeparatorLine = Color.FromArgb(51, 51, 51);
                TextNormal = Color.FromArgb(234, 234, 234);
                TextSelected = Color.White;
                TextDisabled = Color.FromArgb(120, 120, 120);
                ChevronNormal = Color.FromArgb(190, 190, 190);
                ChevronSelected = Color.White;
                CheckBoxFill = Color.FromArgb(20, 20, 20);
                CheckBoxUncheckedBorder = Color.FromArgb(120, 120, 120);
                CheckBoxCheckedBorder = Color.FromArgb(41, 182, 246);
                CheckMarkColor = Color.White;
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using var bg = new SolidBrush(SurfaceBg);
                e.Graphics.FillRectangle(bg, e.AffectedBounds);
            }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
            {
                // Intentionally empty so WinForms never paints a bright image/check margin strip.
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                SmoothingMode prev = e.Graphics.SmoothingMode;
                e.Graphics.SmoothingMode = SmoothingMode.None;
                using var pen = new Pen(SurfaceBorder, 1f);
                e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
                e.Graphics.SmoothingMode = prev;
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                int stripW = e.ToolStrip != null && e.ToolStrip.Width > 0
                    ? e.ToolStrip.Width
                    : e.Item.Width + 2;
                int itemX = e.Item.Bounds.Left;
                bool rtl = IsRtl(e.ToolStrip, e.Item);

                if (e.Item.Enabled && (e.Item.Selected || e.Item.Pressed))
                {
                    int pillLeft = 5 - itemX;
                    int pillRight = (stripW - 6) - itemX;
                    var pillRect = new Rectangle(pillLeft, 1, Math.Max(8, pillRight - pillLeft), Math.Max(8, e.Item.Height - 3));
                    Color fillColor = e.Item.Pressed ? PressedFill : HoverFill;

                    SmoothingMode prev = e.Graphics.SmoothingMode;
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using var path = CreateRoundedRectPath(pillRect, 4);
                    using var fill = new SolidBrush(fillColor);
                    using var border = new Pen(HoverBorder, 1f);
                    e.Graphics.FillPath(fill, path);
                    e.Graphics.DrawPath(border, path);
                    e.Graphics.SmoothingMode = prev;
                }

                if (e.Item is ToolStripMenuItem menuItem && Equals(menuItem.Tag, CheckableMenuItemTag))
                {
                    float cx = rtl ? 19f - itemX : (stripW - 19f) - itemX;
                    float cy = e.Item.Height / 2f;
                    var boxRect = new Rectangle((int)Math.Round(cx - 6.5f), (int)Math.Round(cy - 6.5f), 13, 13);

                    SmoothingMode prev = e.Graphics.SmoothingMode;
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using var boxPath = CreateRoundedRectPath(boxRect, 2);
                    using var boxBg = new SolidBrush(CheckBoxFill);
                    Color borderColor = menuItem.Checked ? CheckBoxCheckedBorder : CheckBoxUncheckedBorder;
                    using var boxBorder = new Pen(borderColor, 1f);
                    e.Graphics.FillPath(boxBg, boxPath);
                    e.Graphics.DrawPath(boxBorder, boxPath);

                    if (menuItem.Checked)
                    {
                        using var checkPen = new Pen(CheckMarkColor, 1.75f)
                        {
                            StartCap = LineCap.Round,
                            EndCap = LineCap.Round,
                            LineJoin = LineJoin.Round
                        };
                        e.Graphics.DrawLines(checkPen, new[]
                        {
                            new PointF(boxRect.Left + 2.8f, boxRect.Top + 6.7f),
                            new PointF(boxRect.Left + 5.5f, boxRect.Top + 9.5f),
                            new PointF(boxRect.Left + 10.3f, boxRect.Top + 3.8f)
                        });
                    }

                    e.Graphics.SmoothingMode = prev;
                }
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                // Drawn cleanly on the trailing edge inside OnRenderMenuItemBackground.
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                int stripW = e.ToolStrip != null && e.ToolStrip.Width > 0
                    ? e.ToolStrip.Width
                    : e.Item.Width + 2;
                int itemX = e.Item.Bounds.Left;
                bool rtl = IsRtl(e.ToolStrip, e.Item);

                int textLeft = rtl ? 36 - itemX : 14 - itemX;
                int textRight = rtl ? (stripW - 14) - itemX : (stripW - 36) - itemX;
                var textBounds = new Rectangle(textLeft, 0, Math.Max(20, textRight - textLeft), e.Item.Height);

                Color textColor = !e.Item.Enabled
                    ? TextDisabled
                    : (e.Item.Selected || e.Item.Pressed ? TextSelected : TextNormal);

                TextFormatFlags flags = TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine
                    | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.NoPrefix;
                if (rtl)
                {
                    flags |= TextFormatFlags.Right | TextFormatFlags.RightToLeft;
                }

                TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, textBounds, textColor, flags);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                ToolStrip? owner = e.Item?.Owner;
                int stripW = owner != null && owner.Width > 0
                    ? owner.Width
                    : (e.Item?.Width ?? 0) + 2;
                int itemX = e.Item?.Bounds.Left ?? 0;
                bool rtl = IsRtl(owner, e.Item);

                float cx = rtl ? 18.5f - itemX : (stripW - 18.5f) - itemX;
                float cy = (e.Item?.Height ?? e.ArrowRectangle.Height) / 2f;
                Color arrowColor = e.Item != null && !e.Item.Enabled
                    ? TextDisabled
                    : (e.Item != null && (e.Item.Selected || e.Item.Pressed) ? ChevronSelected : ChevronNormal);

                SmoothingMode prev = e.Graphics.SmoothingMode;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(arrowColor, 1.55f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                };

                PointF[] pts = rtl
                    ? new[]
                    {
                        new PointF(cx + 2.0f, cy - 3.6f),
                        new PointF(cx - 2.0f, cy),
                        new PointF(cx + 2.0f, cy + 3.6f)
                    }
                    : new[]
                    {
                        new PointF(cx - 2.0f, cy - 3.6f),
                        new PointF(cx + 2.0f, cy),
                        new PointF(cx - 2.0f, cy + 3.6f)
                    };
                e.Graphics.DrawLines(pen, pts);
                e.Graphics.SmoothingMode = prev;
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int stripW = e.ToolStrip != null && e.ToolStrip.Width > 0
                    ? e.ToolStrip.Width
                    : e.Item.Width + 4;
                int sepX = e.Item.Bounds.Left;
                int left = 10 - sepX;
                int right = (stripW - 11) - sepX;
                int y = e.Item.Height / 2;

                SmoothingMode prev = e.Graphics.SmoothingMode;
                e.Graphics.SmoothingMode = SmoothingMode.None;
                using var pen = new Pen(SeparatorLine, 1f);
                e.Graphics.DrawLine(pen, left, y, right, y);
                e.Graphics.SmoothingMode = prev;
            }

            private static bool IsRtl(ToolStrip? strip, ToolStripItem? item) =>
                (item?.RightToLeft == RightToLeft.Yes) || (strip?.RightToLeft == RightToLeft.Yes);

            private static GraphicsPath CreateRoundedRectPath(Rectangle bounds, int radius)
            {
                int diameter = radius * 2;
                var path = new GraphicsPath();
                path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
                path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
                path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                return path;
            }
        }
    }
}
