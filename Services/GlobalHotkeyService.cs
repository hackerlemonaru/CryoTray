using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;
using System.Windows.Input;

namespace CryoTray.Services
{
    public class GlobalHotkeyService
    {
        private static GlobalHotkeyService? _instance;
        public static GlobalHotkeyService Instance => _instance ??= new GlobalHotkeyService();

        public const int CoolingHotkeyId = 9001;
        public const int RamOptimizeHotkeyId = 9002;
        private IntPtr _hWnd = IntPtr.Zero;
        private bool _isRegistered;
        private bool _isRamRegistered;

        public bool IsRegistered => _isRegistered;
        public bool IsRamHotkeyRegistered => _isRamRegistered;

        public bool Register(IntPtr hWnd, string modifiers, string key)
        {
            Unregister();
            _hWnd = hWnd;
            _isRegistered = RegisterInternal(hWnd, CoolingHotkeyId, modifiers, key);
            return _isRegistered;
        }

        public bool RegisterRamHotkey(IntPtr hWnd, string modifiers, string key)
        {
            UnregisterRamHotkey();
            _hWnd = hWnd;
            _isRamRegistered = RegisterInternal(hWnd, RamOptimizeHotkeyId, modifiers, key);
            return _isRamRegistered;
        }

        private static bool RegisterInternal(IntPtr hWnd, int hotkeyId, string modifiers, string key)
        {
            if (hWnd == IntPtr.Zero) return false;

            uint fsModifiers = 0;
            string safeModifiers = modifiers ?? "";
            if (safeModifiers.Contains("Ctrl", StringComparison.OrdinalIgnoreCase)) fsModifiers |= NativeMethods.MOD_CONTROL;
            if (safeModifiers.Contains("Shift", StringComparison.OrdinalIgnoreCase)) fsModifiers |= NativeMethods.MOD_SHIFT;
            if (safeModifiers.Contains("Alt", StringComparison.OrdinalIgnoreCase)) fsModifiers |= NativeMethods.MOD_ALT;
            if (safeModifiers.Contains("Win", StringComparison.OrdinalIgnoreCase)) fsModifiers |= NativeMethods.MOD_WIN;

            fsModifiers |= NativeMethods.MOD_NOREPEAT;

            uint vk = ResolveVirtualKey(key);
            if (vk == 0)
            {
                return false;
            }

            bool ok = NativeMethods.RegisterHotKey(hWnd, hotkeyId, fsModifiers, vk);
            Debug.WriteLine($"RegisterHotKey ({hotkeyId}) for {safeModifiers}+{key} result: {ok}");
            return ok;
        }

        public void Unregister()
        {
            if (_isRegistered && _hWnd != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(_hWnd, CoolingHotkeyId);
                _isRegistered = false;
            }
        }

        public void UnregisterRamHotkey()
        {
            if (_isRamRegistered && _hWnd != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(_hWnd, RamOptimizeHotkeyId);
                _isRamRegistered = false;
            }
        }

        /// <summary>Captures a modifier+key or standalone F1-F12 hotkey into normalized tokens.</summary>
        public static bool TryCaptureShortcut(Key rawKey, Key systemKey, ModifierKeys modifiers,
            out string modifiersToken, out string keyToken)
        {
            modifiersToken = "";
            keyToken = "";

            Key actualKey = rawKey == Key.System ? systemKey : rawKey;
            if (IsModifierOnlyKey(actualKey))
            {
                return false;
            }

            bool isFunctionKey = actualKey >= Key.F1 && actualKey <= Key.F12;
            bool isLetterKey = actualKey >= Key.A && actualKey <= Key.Z;
            bool isDigitKey = actualKey >= Key.D0 && actualKey <= Key.D9;

            if (!isFunctionKey && !isLetterKey && !isDigitKey)
            {
                return false;
            }

            var parts = new System.Collections.Generic.List<string>(4);
            if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

            // Require a modifier for letter or digit keys so typing cannot trigger globally.
            if (!isFunctionKey && parts.Count == 0)
            {
                return false;
            }

            modifiersToken = string.Join("+", parts);
            keyToken = actualKey.ToString();
            return true;
        }

        /// <summary>Formats stored hotkey tokens for display in Settings.</summary>
        public static string FormatShortcutDisplay(string? modifiers, string? key)
        {
            var parts = new System.Collections.Generic.List<string>(5);
            string safeMods = modifiers ?? "";
            if (safeMods.Contains("Ctrl", StringComparison.OrdinalIgnoreCase)) parts.Add("Ctrl");
            if (safeMods.Contains("Alt", StringComparison.OrdinalIgnoreCase)) parts.Add("Alt");
            if (safeMods.Contains("Shift", StringComparison.OrdinalIgnoreCase)) parts.Add("Shift");
            if (safeMods.Contains("Win", StringComparison.OrdinalIgnoreCase)) parts.Add("Win");

            string cleanKey = NormalizeKeyLabel(key);
            if (!string.IsNullOrEmpty(cleanKey))
            {
                parts.Add(cleanKey);
            }

            return string.Join(" + ", parts);
        }

        private static string NormalizeKeyLabel(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return "";
            }

            string trimmed = key.Trim();
            if (trimmed.Length == 2 && trimmed[0] == 'D' && char.IsDigit(trimmed[1]))
            {
                return trimmed[1].ToString();
            }

            return trimmed.ToUpperInvariant();
        }

        private static uint ResolveVirtualKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return 0;
            }

            string trimmed = key.Trim();
            if (trimmed.Length == 1 && char.IsDigit(trimmed[0]))
            {
                return (uint)('0' + (trimmed[0] - '0'));
            }

            if (Enum.TryParse<Keys>(trimmed, true, out var keyVal) && keyVal != Keys.None)
            {
                return (uint)keyVal;
            }

            if (trimmed.Length == 1 && char.IsAsciiLetter(trimmed[0]))
            {
                return (uint)char.ToUpperInvariant(trimmed[0]);
            }

            return 0;
        }

        private static bool IsModifierOnlyKey(Key key)
        {
            return key == Key.None ||
                key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LWin || key == Key.RWin ||
                key == Key.System || key == Key.DeadCharProcessed;
        }
    }
}
