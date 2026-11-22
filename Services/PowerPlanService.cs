using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CryoTray.Models;

namespace CryoTray.Services
{
    public class PowerSchemeInfo
    {
        public Guid Guid { get; set; }
        public string Name { get; set; } = "";
        public bool IsActive { get; set; }
    }

    public class PowerPlanService
    {
        private static PowerPlanService? _instance;
        public static PowerPlanService Instance => _instance ??= new PowerPlanService();

        public const string CryoTrayCoolingSchemeName = "CryoTray Cooling";
        public static readonly Guid BalancedGuid = NativeMethods.GUID_POWERSCHEME_BALANCED;
        private const int PowerCfgTimeoutMs = 15000;
        private static readonly string PowerCfgExePath = Path.Combine(Environment.SystemDirectory, "powercfg.exe");
        private static readonly Regex PowerCfgSchemeLineRegex = new(
            @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\(([^)]*)\)",
            RegexOptions.Compiled);

        public event Action<bool>? CoolingModeChanged;

        private Guid? _cryoTrayCoolingGuid;

        public bool IsCoolingModeActive { get; private set; }

        public PowerPlanService()
        {
            RefreshState();
        }

        public List<PowerSchemeInfo> GetInstalledSchemes()
        {
            var schemes = new List<PowerSchemeInfo>();
            uint index = 0;
            uint bufferSize = 16;
            IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);

            Guid activeGuid = GetActiveSchemeGuid();

            try
            {
                while (NativeMethods.PowerEnumerate(
                           IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                           NativeMethods.ACCESS_SCHEME, index, buffer, ref bufferSize) == 0)
                {
                    byte[] guidBytes = new byte[16];
                    Marshal.Copy(buffer, guidBytes, 0, 16);
                    Guid schemeGuid = new(guidBytes);

                    string name = ReadSchemeFriendlyName(schemeGuid);
                    schemes.Add(new PowerSchemeInfo
                    {
                        Guid = schemeGuid,
                        Name = name,
                        IsActive = (schemeGuid == activeGuid)
                    });

                    index++;
                    bufferSize = 16;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            // Fallback to powercfg parsing if Win32 API returned empty
            if (schemes.Count == 0)
            {
                schemes = ParsePowerCfgList();
            }

            return schemes;
        }

        public Guid GetActiveSchemeGuid()
        {
            uint result = NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out IntPtr pGuid);
            if (result == 0 && pGuid != IntPtr.Zero)
            {
                try
                {
                    Guid active = Marshal.PtrToStructure<Guid>(pGuid);
                    return active;
                }
                finally
                {
                    NativeMethods.LocalFree(pGuid);
                }
            }

            return BalancedGuid;
        }

        private string ReadSchemeFriendlyName(Guid schemeGuid)
        {
            uint bufferSize = 512;
            IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                uint result = NativeMethods.PowerReadFriendlyName(
                    IntPtr.Zero, ref schemeGuid, IntPtr.Zero, IntPtr.Zero, buffer, ref bufferSize);

                if (result == 0 && bufferSize > 0)
                {
                    return Marshal.PtrToStringUni(buffer) ?? schemeGuid.ToString();
                }
            }
            catch
            {
                // Ignore
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return schemeGuid.ToString();
        }

        public Guid EnsureCryoTrayCoolingScheme()
        {
            if (_cryoTrayCoolingGuid.HasValue)
            {
                // Verify it still exists in system
                var existing = GetInstalledSchemes().FirstOrDefault(s => s.Guid == _cryoTrayCoolingGuid.Value);
                if (existing != null)
                {
                    return _cryoTrayCoolingGuid.Value;
                }
            }

            // Check installed schemes by name
            var schemes = GetInstalledSchemes();
            var found = schemes.FirstOrDefault(s => s.Name.Equals(CryoTrayCoolingSchemeName, StringComparison.OrdinalIgnoreCase));
            if (found != null)
            {
                _cryoTrayCoolingGuid = found.Guid;
                return found.Guid;
            }

            // Not found -> import from embedded Resources\CryoTray.pow
            Guid newGuid = Guid.NewGuid();
            string tempPowPath = Path.Combine(Path.GetTempPath(), $"CryoTray_{Guid.NewGuid():N}.pow");

            try
            {
                // Extract embedded resource
                byte[]? powBytes = null;
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("CryoTray.Resources.CryoTray.pow")
                                   ?? assembly.GetManifestResourceNames()
                                       .Where(n => n.EndsWith("CryoTray.pow", StringComparison.OrdinalIgnoreCase))
                                       .Select(n => assembly.GetManifestResourceStream(n))
                                       .FirstOrDefault())
                {
                    if (stream != null)
                    {
                        using var ms = new MemoryStream();
                        stream.CopyTo(ms);
                        powBytes = ms.ToArray();
                    }
                }

                // Fallback to local file if not found in assembly stream
                if (powBytes == null)
                {
                    string localFile = Path.Combine(AppContext.BaseDirectory, "Resources", "CryoTray.pow");
                    if (File.Exists(localFile))
                    {
                        powBytes = File.ReadAllBytes(localFile);
                    }
                }

                if (powBytes != null)
                {
                    File.WriteAllBytes(tempPowPath, powBytes);

                    // Import scheme silently
                    var psiImport = new ProcessStartInfo
                    {
                        FileName = PowerCfgExePath,
                        Arguments = $"/import \"{tempPowPath}\" {newGuid}",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var procImport = Process.Start(psiImport);
                    bool importExited = procImport != null && procImport.WaitForExit(PowerCfgTimeoutMs);
                    if (!importExited)
                    {
                        try { procImport?.Kill(); } catch { }
                        return Guid.Empty;
                    }

                    if (procImport!.ExitCode != 0)
                    {
                        return Guid.Empty;
                    }

                    // Rename to CryoTray Cooling
                    var psiRename = new ProcessStartInfo
                    {
                        FileName = PowerCfgExePath,
                        Arguments = $"-changename {newGuid} \"{CryoTrayCoolingSchemeName}\" \"Low-temperature CPU power plan\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var procRename = Process.Start(psiRename);
                    if (procRename != null && !procRename.WaitForExit(PowerCfgTimeoutMs))
                    {
                        try { procRename.Kill(); } catch { }
                    }

                    if (GetInstalledSchemes().Any(s => s.Guid == newGuid))
                    {
                        _cryoTrayCoolingGuid = newGuid;
                        return newGuid;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to import CryoTray.pow: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPowPath)) File.Delete(tempPowPath);
                }
                catch
                {
                    // Ignore
                }
            }

            return Guid.Empty;
        }

        public void RefreshState()
        {
            Guid activeGuid = GetActiveSchemeGuid();
            Guid coolingGuid = EnsureCryoTrayCoolingScheme();
            SetCoolingState(coolingGuid != Guid.Empty && activeGuid == coolingGuid);
        }

        public void SyncCoolingStateFromSystem()
        {
            Guid coolingGuid = _cryoTrayCoolingGuid ?? EnsureCryoTrayCoolingScheme();
            SetCoolingState(coolingGuid != Guid.Empty && GetActiveSchemeGuid() == coolingGuid);
        }

        public Guid GetNormalPowerPlanGuid()
        {
            Guid coolingGuid = EnsureCryoTrayCoolingScheme();
            var schemes = GetInstalledSchemes();
            string configured = SettingsService.Instance.Current.NormalPowerPlanGuid;

            if (Guid.TryParse(configured, out Guid configuredGuid) &&
                configuredGuid != coolingGuid &&
                schemes.Any(scheme => scheme.Guid == configuredGuid))
            {
                return configuredGuid;
            }

            return BalancedGuid;
        }

        private void SetCoolingState(bool isCooling)
        {
            if (IsCoolingModeActive == isCooling)
            {
                return;
            }

            IsCoolingModeActive = isCooling;
            CoolingModeChanged?.Invoke(IsCoolingModeActive);
        }

        public void SetCoolingMode(bool enable)
        {
            if (enable)
            {
                Guid coolingGuid = EnsureCryoTrayCoolingScheme();
                if (coolingGuid == Guid.Empty)
                {
                    SetCoolingState(false);
                    return;
                }

                uint status = NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref coolingGuid);

                // Write current settings parameters
                var s = SettingsService.Instance.Current;
                ApplyCpuCoolingParameters(s.MaxCpuThrottlePercent, s.PerfBoostMode);

                SetCoolingState(status == 0 || GetActiveSchemeGuid() == coolingGuid);
            }
            else
            {
                Guid target = GetNormalPowerPlanGuid();
                uint status = NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref target);
                Guid coolingGuid = _cryoTrayCoolingGuid ?? Guid.Empty;
                bool stillCooling = status != 0 && coolingGuid != Guid.Empty && GetActiveSchemeGuid() == coolingGuid;
                SetCoolingState(stillCooling);
            }
        }

        public void ToggleCoolingMode()
        {
            SetCoolingMode(!IsCoolingModeActive);
        }

        public void ApplyCpuCoolingParameters(int maxThrottlePercent, int perfBoostMode)
        {
            Guid coolingGuid = EnsureCryoTrayCoolingScheme();
            if (coolingGuid == Guid.Empty)
            {
                return;
            }

            Guid subProcessor = NativeMethods.GUID_PROCESSOR_SETTINGS_SUBGROUP;
            Guid throttleMax = NativeMethods.GUID_PROCESSOR_THROTTLE_MAX;
            Guid perfBoost = NativeMethods.GUID_PROCESSOR_PERFBOOST_MODE;

            uint throttleVal = (uint)Math.Clamp(maxThrottlePercent, 50, 100);
            uint boostVal = (uint)Math.Clamp(perfBoostMode, 0, 1);

            // Write PROCTHROTTLEMAX AC & DC
            NativeMethods.PowerWriteACValueIndex(IntPtr.Zero, ref coolingGuid, ref subProcessor, ref throttleMax, throttleVal);
            NativeMethods.PowerWriteDCValueIndex(IntPtr.Zero, ref coolingGuid, ref subProcessor, ref throttleMax, throttleVal);

            // Write PERFBOOSTMODE AC & DC
            NativeMethods.PowerWriteACValueIndex(IntPtr.Zero, ref coolingGuid, ref subProcessor, ref perfBoost, boostVal);
            NativeMethods.PowerWriteDCValueIndex(IntPtr.Zero, ref coolingGuid, ref subProcessor, ref perfBoost, boostVal);

            // If Cooling Mode is currently active, immediately re-apply active scheme
            if (IsCoolingModeActive || GetActiveSchemeGuid() == coolingGuid)
            {
                NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref coolingGuid);
            }
        }

        public void ResetCryoTraySchemeToDefault()
        {
            // Restore exact baseline .pow parameters: PERFBOOSTMODE = Disabled (0), PROCTHROTTLEMAX = 99% (0x63)
            ApplyCpuCoolingParameters(99, 0);
        }

        /// <summary>Restores a non-CryoTray power plan and removes CryoTray Cooling schemes on uninstall.</summary>
        public void CleanupForUninstall()
        {
            var schemes = GetInstalledSchemes();
            var coolingSchemes = schemes
                .Where(s => s.Name.Equals(CryoTrayCoolingSchemeName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var coolingIds = coolingSchemes.Select(s => s.Guid).ToHashSet();

            Guid fallback = BalancedGuid;
            if (Guid.TryParse(SettingsService.Instance.Current.NormalPowerPlanGuid, out Guid configured) &&
                !coolingIds.Contains(configured) &&
                schemes.Any(s => s.Guid == configured))
            {
                fallback = configured;
            }
            else if (schemes.All(s => s.Guid != fallback) || coolingIds.Contains(fallback))
            {
                var other = schemes.FirstOrDefault(s => !coolingIds.Contains(s.Guid));
                if (other != null)
                {
                    fallback = other.Guid;
                }
            }

            NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref fallback);

            foreach (var scheme in coolingSchemes)
            {
                Guid guid = scheme.Guid;
                if (NativeMethods.PowerDeleteScheme(IntPtr.Zero, ref guid) != 0)
                {
                    TryDeleteSchemeViaPowerCfg(guid);
                }
            }

            _cryoTrayCoolingGuid = null;
            SetCoolingState(false);
        }

        private static void TryDeleteSchemeViaPowerCfg(Guid schemeGuid)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = PowerCfgExePath,
                    Arguments = $"/delete {schemeGuid}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                if (proc != null && !proc.WaitForExit(PowerCfgTimeoutMs))
                {
                    try { proc.Kill(); } catch { }
                }
            }
            catch
            {
            }
        }

        private List<PowerSchemeInfo> ParsePowerCfgList()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = PowerCfgExePath,
                    Arguments = "/list",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return new List<PowerSchemeInfo>();

                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                if (!proc.WaitForExit(PowerCfgTimeoutMs))
                {
                    try { proc.Kill(); } catch { }
                    return new List<PowerSchemeInfo>();
                }

                string output = stdoutTask.GetAwaiter().GetResult();
                return ParsePowerCfgOutput(output);
            }
            catch
            {
                // Ignore
            }

            return new List<PowerSchemeInfo>();
        }

        internal static List<PowerSchemeInfo> ParsePowerCfgOutput(string? output)
        {
            var list = new List<PowerSchemeInfo>();
            if (string.IsNullOrWhiteSpace(output))
            {
                return list;
            }

            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawLine in lines)
            {
                string line = rawLine.TrimEnd();
                var match = PowerCfgSchemeLineRegex.Match(line);
                if (!match.Success || !Guid.TryParse(match.Groups[1].Value, out Guid guid))
                {
                    continue;
                }

                list.Add(new PowerSchemeInfo
                {
                    Guid = guid,
                    Name = match.Groups[2].Value.Trim(),
                    IsActive = line.EndsWith("*", StringComparison.Ordinal)
                });
            }

            return list;
        }
    }
}
