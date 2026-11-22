using System;
using System.IO;
using Microsoft.Win32;

namespace CryoTray.Services
{
    public static class UpdateInstallationService
    {
        private const string UninstallKey =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8F3B9A12-4D6E-4C2B-9A1F-7E5D3C1B8A90}_is1";

        public static bool IsInstalled()
        {
            return IsInstalledAt(AppContext.BaseDirectory);
        }

        public static bool IsInstalledAt(string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                return false;
            }

            string normalized = Normalize(baseDirectory);

            if (MatchesUninstallLocation(RegistryHive.LocalMachine, RegistryView.Registry64, normalized) ||
                MatchesUninstallLocation(RegistryHive.LocalMachine, RegistryView.Registry32, normalized) ||
                MatchesUninstallLocation(RegistryHive.CurrentUser, RegistryView.Registry64, normalized) ||
                MatchesUninstallLocation(RegistryHive.CurrentUser, RegistryView.Registry32, normalized))
            {
                return true;
            }

            // A portable copy may live under Program Files for protection. Only
            // treat that path as an installed copy when Inno's uninstaller marker
            // is present, so portable copies keep using the update archive.
            return IsUnderProgramFiles(normalized) && HasInnoUninstaller(normalized);
        }

        private static bool HasInnoUninstaller(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return false;
                }

                foreach (string file in Directory.EnumerateFiles(directory, "unins*.exe", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name.Length == 8 &&
                        name.StartsWith("unins", StringComparison.OrdinalIgnoreCase) &&
                        char.IsDigit(name[5]) &&
                        char.IsDigit(name[6]) &&
                        char.IsDigit(name[7]))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool MatchesUninstallLocation(
            RegistryHive root, RegistryView view, string baseDirectory)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(root, view);
                using var key = baseKey.OpenSubKey(UninstallKey);
                if (key == null)
                {
                    return false;
                }

                string? location = key.GetValue("InstallLocation") as string;
                return !string.IsNullOrWhiteSpace(location) &&
                       string.Equals(Normalize(location), baseDirectory, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsUnderProgramFiles(string directory)
        {
            return IsUnder(directory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) ||
                   IsUnder(directory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        }

        private static bool IsUnder(string directory, string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            string normalizedRoot = Normalize(root);
            return directory.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                   directory.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            try
            {
                return Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
    }
}
