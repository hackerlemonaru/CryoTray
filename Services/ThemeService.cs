using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace CryoTray.Services
{
    public static class ThemeService
    {
        private const string PersonalizeKey =
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        private static readonly Dictionary<string, string> DarkPalette = new()
        {
            ["WindowBackgroundBrush"] = "#141414",
            ["TitleBarBackgroundBrush"] = "#141414",
            ["HeaderBackgroundBrush"] = "#1B1B1B",
            ["PanelBackgroundBrush"] = "#1E1E1E",
            ["PanelAltBackgroundBrush"] = "#1A1A1A",
            ["CardBackgroundBrush"] = "#242424",
            ["CardAltBackgroundBrush"] = "#262626",
            ["InsetBackgroundBrush"] = "#121212",
            ["ChartBackgroundBrush"] = "#0D0D0D",
            ["DividerBrush"] = "#262626",
            ["BorderBrush"] = "#333333",
            ["BorderStrongBrush"] = "#383838",
            ["BorderSubtleBrush"] = "#303030",
            ["InputBackgroundBrush"] = "#1E1E1E",
            ["InputBorderBrush"] = "#4A4A4A",
            ["HoverBackgroundBrush"] = "#2A2A2A",
            ["ActiveBackgroundBrush"] = "#2D2D2D",
            ["TextPrimaryBrush"] = "#FFFFFF",
            ["TextTitleBrush"] = "#D5D5D5",
            ["TextSecondaryBrush"] = "#B8B8B8",
            ["TextTertiaryBrush"] = "#9A9A9A",
            ["TextMutedBrush"] = "#888888",
            ["TextDisabledBrush"] = "#787878",
            ["TextDimBrush"] = "#A6A6A6",
            ["IconBrush"] = "#C4C4C4",
            ["TooltipBackgroundBrush"] = "#222222",
            ["TooltipBorderBrush"] = "#444444",
            ["MenuBackgroundBrush"] = "#1E1E1E",
            ["MenuBorderBrush"] = "#525252",
            ["MenuHoverBrush"] = "#2D2D2D",
            ["MenuHoverBorderBrush"] = "#6E6E6E",
            ["CheckboxBackgroundBrush"] = "#1A1A1A",
            ["CheckboxBorderBrush"] = "#4A4A4A",
            ["AccentBrush"] = "#2B88D8",
            ["AccentHoverBrush"] = "#3A9AE8",
            ["AccentPressedBrush"] = "#1E6FAE",
            ["AccentSoftBrush"] = "#1E5C91",
            ["AccentTextBrush"] = "#2B88D8",
            ["CpuAccentBrush"] = "#4FC3F7",
            ["GpuAccentBrush"] = "#7CB342",
            ["RamAccentBrush"] = "#62B0E8",
            ["BatteryAccentBrush"] = "#F3C765",
            ["CoolingOnTextBrush"] = "#76C6ED",
            ["CoolingOffTextBrush"] = "#E5987E",
            ["SuccessTextBrush"] = "#ABD068",
            ["WarningTextBrush"] = "#F3C765",
            ["DangerTextBrush"] = "#FAAA9A",
            ["DangerSoftBackgroundBrush"] = "#3A2424",
            ["DangerSoftHoverBrush"] = "#4A2929",
            ["PawnIoBackgroundBrush"] = "#261A14",
            ["PawnIoBorderBrush"] = "#7A462D",
            ["PawnIoTextBrush"] = "#F2DFD5",
            ["PawnIoButtonBackgroundBrush"] = "#2D2D2D",
            ["PawnIoButtonHoverBrush"] = "#3A3A3A",
            ["PawnIoButtonPressedBrush"] = "#242424",
            ["ToggleOffTrackBrush"] = "#4A2A1E",
            ["ToggleOffTextBrush"] = "#D9835B",
            ["ToggleOffThumbBrush"] = "#E68A57",
            ["OverlayBackgroundBrush"] = "#E6161616",
            ["OverlayLabelBrush"] = "#AAAAAA",
            ["OverlaySecondaryBrush"] = "#A8A8A8",
            ["OverlayMutedBrush"] = "#9A9A9A",
            ["OverlayIconBrush"] = "#888888",
            ["ChartGridBrush"] = "#333333",
            ["ChartLineBrush"] = "#BDBDBD",
            ["ChartHoverLineBrush"] = "#A5A5A5",
            ["AlternateRowBrush"] = "#272727",
            ["HighlightRowBrush"] = "#1E3A4A",
            ["HighlightRowBorderBrush"] = "#2B88D8",
            ["SelectionBrush"] = "#1E5C91",
            ["SelectionTextBrush"] = "#FFFFFF",
            ["RamFooterMutedBrush"] = "#A6A6A6",
            ["RamFooterTextBrush"] = "#B8B8B8"
        };

        private static readonly Dictionary<string, string> LightPalette = new()
        {
            ["WindowBackgroundBrush"] = "#F5F3EF",
            ["TitleBarBackgroundBrush"] = "#F5F3EF",
            ["HeaderBackgroundBrush"] = "#EDEAE4",
            ["PanelBackgroundBrush"] = "#FFFFFF",
            ["PanelAltBackgroundBrush"] = "#FAF8F4",
            ["CardBackgroundBrush"] = "#F7F4EF",
            ["CardAltBackgroundBrush"] = "#F1EDE6",
            ["InsetBackgroundBrush"] = "#FBFAF7",
            ["ChartBackgroundBrush"] = "#FBF9F5",
            ["DividerBrush"] = "#E7E1D8",
            ["BorderBrush"] = "#DDD7CD",
            ["BorderStrongBrush"] = "#CFC7BA",
            ["BorderSubtleBrush"] = "#E6E0D7",
            ["InputBackgroundBrush"] = "#FFFFFF",
            ["InputBorderBrush"] = "#B9B1A5",
            ["HoverBackgroundBrush"] = "#F0EDE7",
            ["ActiveBackgroundBrush"] = "#E8E4DC",
            ["TextPrimaryBrush"] = "#1C1A17",
            ["TextTitleBrush"] = "#2A2723",
            ["TextSecondaryBrush"] = "#5C564D",
            ["TextTertiaryBrush"] = "#746E64",
            ["TextMutedBrush"] = "#7D776D",
            ["TextDisabledBrush"] = "#A39B90",
            ["TextDimBrush"] = "#6B655C",
            ["IconBrush"] = "#4E4A43",
            ["TooltipBackgroundBrush"] = "#FFFFFF",
            ["TooltipBorderBrush"] = "#CFC7BA",
            ["MenuBackgroundBrush"] = "#FFFFFF",
            ["MenuBorderBrush"] = "#C9C1B5",
            ["MenuHoverBrush"] = "#F0EDE7",
            ["MenuHoverBorderBrush"] = "#B9B1A5",
            ["CheckboxBackgroundBrush"] = "#FFFFFF",
            ["CheckboxBorderBrush"] = "#A9A196",
            ["AccentBrush"] = "#1B6CB3",
            ["AccentHoverBrush"] = "#2B88D8",
            ["AccentPressedBrush"] = "#0F5C95",
            ["AccentSoftBrush"] = "#DCEAF6",
            ["AccentTextBrush"] = "#1B6CB3",
            ["CpuAccentBrush"] = "#0B6E99",
            ["GpuAccentBrush"] = "#4C7A22",
            ["RamAccentBrush"] = "#1D6FA5",
            ["BatteryAccentBrush"] = "#8A6500",
            ["CoolingOnTextBrush"] = "#146C92",
            ["CoolingOffTextBrush"] = "#B4472A",
            ["SuccessTextBrush"] = "#4F7A16",
            ["WarningTextBrush"] = "#8A6500",
            ["DangerTextBrush"] = "#A8321B",
            ["DangerSoftBackgroundBrush"] = "#FBE7E3",
            ["DangerSoftHoverBrush"] = "#F5D6D0",
            ["PawnIoBackgroundBrush"] = "#FDF0E8",
            ["PawnIoBorderBrush"] = "#D9B39B",
            ["PawnIoTextBrush"] = "#5C3A26",
            ["PawnIoButtonBackgroundBrush"] = "#FFFFFF",
            ["PawnIoButtonHoverBrush"] = "#F3E7DE",
            ["PawnIoButtonPressedBrush"] = "#EADDD3",
            ["ToggleOffTrackBrush"] = "#EFE4DC",
            ["ToggleOffTextBrush"] = "#A6532E",
            ["ToggleOffThumbBrush"] = "#C26A47",
            ["OverlayBackgroundBrush"] = "#F2F4F2EE",
            ["OverlayLabelBrush"] = "#5F5A53",
            ["OverlaySecondaryBrush"] = "#6F6962",
            ["OverlayMutedBrush"] = "#8A847A",
            ["OverlayIconBrush"] = "#7D776D",
            ["ChartGridBrush"] = "#E2DDD4",
            ["ChartLineBrush"] = "#6E675E",
            ["ChartHoverLineBrush"] = "#8F887E",
            ["AlternateRowBrush"] = "#F6F2EC",
            ["HighlightRowBrush"] = "#E3F1F9",
            ["HighlightRowBorderBrush"] = "#1B6CB3",
            ["SelectionBrush"] = "#DCEAF6",
            ["SelectionTextBrush"] = "#1C1A17",
            ["RamFooterMutedBrush"] = "#6B655C",
            ["RamFooterTextBrush"] = "#5C564D"
        };

        private static bool _systemEventsHooked;
        private static string _configuredMode = "Dark";
        private static string _effectiveMode = "Dark";

        public static event EventHandler? ThemeChanged;

        public static bool IsLight => _effectiveMode == "Light";

        public static string ConfiguredMode => _configuredMode;

        public static void Initialize(string? mode)
        {
            if (!_systemEventsHooked)
            {
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                _systemEventsHooked = true;
            }

            Apply(mode, force: true);
        }

        public static void Apply(string? mode, bool force = false)
        {
            string normalized = Normalize(mode);
            string effective = normalized == "System"
                ? (IsSystemLight() ? "Light" : "Dark")
                : normalized;

            _configuredMode = normalized;
            if (!force && string.Equals(effective, _effectiveMode, StringComparison.Ordinal))
            {
                return;
            }

            _effectiveMode = effective;
            var palette = effective == "Light" ? LightPalette : DarkPalette;
            var resources = Application.Current?.Resources;
            if (resources != null)
            {
                foreach (var entry in palette)
                {
                    var brush = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(entry.Value));
                    brush.Freeze();
                    resources[entry.Key] = brush;
                }
            }

            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        public static string Normalize(string? mode)
        {
            if (string.Equals(mode, "Light", StringComparison.OrdinalIgnoreCase))
            {
                return "Light";
            }
            if (string.Equals(mode, "System", StringComparison.OrdinalIgnoreCase))
            {
                return "System";
            }
            return "Dark";
        }

        public static Color GetColor(string key)
        {
            if (Application.Current?.Resources[key] is SolidColorBrush brush)
            {
                return brush.Color;
            }

            return Colors.Transparent;
        }

        public static Brush GetBrush(string key)
        {
            if (Application.Current?.Resources[key] is Brush brush)
            {
                return brush;
            }

            return Brushes.Transparent;
        }

        public static void Shutdown()
        {
            if (!_systemEventsHooked)
            {
                return;
            }

            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _systemEventsHooked = false;
        }

        private static bool IsSystemLight()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                object? value = key?.GetValue("AppsUseLightTheme");
                return value != null && Convert.ToInt32(value) != 0;
            }
            catch
            {
                return false;
            }
        }

        private static void OnUserPreferenceChanged(
            object sender,
            UserPreferenceChangedEventArgs e)
        {
            if (_configuredMode != "System")
            {
                return;
            }

            if (e.Category != UserPreferenceCategory.General &&
                e.Category != UserPreferenceCategory.VisualStyle &&
                e.Category != UserPreferenceCategory.Accessibility)
            {
                return;
            }

            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                Apply(_configuredMode)));
        }
    }
}
