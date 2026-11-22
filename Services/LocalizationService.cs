using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Markup;

namespace CryoTray.Services
{
    public sealed class LocalizationService
    {
        private static readonly Lazy<LocalizationService> LazyInstance =
            new(() => new LocalizationService());

        public static LocalizationService Instance => LazyInstance.Value;

        private string _resourceCode = "";

        public event Action? LanguageChanged;

        public bool IsArabic => _resourceCode == "ar";

        public FlowDirection FlowDirection => IsArabic
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;

        private LocalizationService()
        {
        }

        public void Apply(string? language)
        {
            string code = string.Equals(language, "العربية", StringComparison.Ordinal)
                ? "ar"
                : "en";

            if (string.Equals(code, _resourceCode, StringComparison.Ordinal))
            {
                return;
            }

            var application = Application.Current;
            if (application == null)
            {
                _resourceCode = code;
                return;
            }

            var dictionaries = application.Resources.MergedDictionaries;
            foreach (var dictionary in dictionaries
                         .Where(item => item.Source?.OriginalString.Contains(
                             "Resources/Strings.", StringComparison.OrdinalIgnoreCase) == true)
                         .ToList())
            {
                dictionaries.Remove(dictionary);
            }

            var strings = new ResourceDictionary
            {
                Source = new Uri($"Resources/Strings.{code}.xaml", UriKind.Relative)
            };

            dictionaries.Insert(0, strings);
            _resourceCode = code;
            LanguageChanged?.Invoke();
        }

        public void ApplyWindow(Window window)
        {
            window.FlowDirection = FlowDirection;
            window.Language = XmlLanguage.GetLanguage(IsArabic ? "ar-SA" : "en-US");
        }

        public static string Get(string key)
        {
            if (Application.Current?.TryFindResource(key) is string text)
            {
                return text;
            }

            return key;
        }

        public static string Format(string key, params object?[] arguments)
        {
            string pattern = Get(key);
            try
            {
                return string.Format(CultureInfo.CurrentCulture, pattern, arguments);
            }
            catch (FormatException)
            {
                return pattern;
            }
        }

        public static string Health(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return Get("Common_Unknown");
            }

            string key = "Health_" + status;
            string translated = Get(key);
            return translated == key ? Get("Common_Unknown") : translated;
        }
    }
}
