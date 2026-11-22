using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CryoTray.Services
{
    public static class UpdateStorage
    {
        private const long MaxLogBytes = 1024 * 1024;
        private const string DeveloperModeMarker = "dev-mode";
        private static readonly SecurityIdentifier AdministratorsSid = new(
            WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier LocalSystemSid = new(
            WellKnownSidType.LocalSystemSid, null);
        private static readonly SecurityIdentifier BuiltinUsersSid = new(
            WellKnownSidType.BuiltinUsersSid, null);
        private static readonly SecurityIdentifier AuthenticatedUsersSid = new(
            WellKnownSidType.AuthenticatedUserSid, null);
        private static readonly SecurityIdentifier EveryoneSid = new(
            WellKnownSidType.WorldSid, null);
        private static readonly SecurityIdentifier InteractiveSid = new(
            WellKnownSidType.InteractiveSid, null);
        private static readonly SecurityIdentifier TrustedInstallerSid = new(
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

        private const FileSystemRights DangerousWriteRights =
            FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;

        private static readonly string DefaultRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CryoTray");
        private static readonly object RootGate = new();
        private static string? _rootPathOverride;
        private static string? _securedRollbackRoot;

        private static string Root => _rootPathOverride ?? DefaultRoot;

        internal static string? RootPathOverride
        {
            get => _rootPathOverride;
            set
            {
                lock (RootGate)
                {
                    _rootPathOverride = string.IsNullOrWhiteSpace(value) ? null : value;
                    _securedRollbackRoot = null;
                }
            }
        }

        public static string RootPath => Root;

        public static bool IsDeveloperModeEnabled
        {
            get
            {
                lock (RootGate)
                {
                    // Secure the root first so a marker planted by a standard user is cleared.
                    EnsureUpdateRoot();
                    return File.Exists(Path.Combine(Root, DeveloperModeMarker));
                }
            }
        }

        public static string UpdateRoot => Path.Combine(Root, "Update");
        public static string DownloadsRoot => Path.Combine(UpdateRoot, "Downloads");
        public static string RollbackRoot => Path.Combine(Root, "Rollback");
        public static string CompletionStatePath => Path.Combine(UpdateRoot, "last-update.json");
        public static string LogPath => Path.Combine(UpdateRoot, "update.log");

        public static void EnsureUpdateRoot()
        {
            lock (RootGate)
            {
                bool rootWasTrusted = PathExists(Root) && IsTrustedDirectory(Root);
                SecureDirectory(Root);
                if (!rootWasTrusted)
                {
                    _securedRollbackRoot = null;
                }

                SecureDirectory(UpdateRoot);
                SecureDirectory(DownloadsRoot);
                RotateLogIfNeeded();
            }
        }

        public static void EnsureRollbackRoot()
        {
            lock (RootGate)
            {
                string root = Root;
                if (string.Equals(
                    _securedRollbackRoot,
                    root,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                SecureDirectory(Root);
                SecureDirectory(RollbackRoot);
                _securedRollbackRoot = root;
            }
        }

        public static bool IsSafeApplicationLocation(string applicationDirectory)
        {
            try
            {
                string fullPath = Path.GetFullPath(applicationDirectory).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!Directory.Exists(fullPath))
                {
                    return false;
                }

                EnsureNoReparsePoints(fullPath);
                if (!IsApplicationDirectorySafe(fullPath))
                {
                    return false;
                }

                string? current = Path.GetDirectoryName(fullPath);
                while (!string.IsNullOrEmpty(current))
                {
                    if (PathExists(current))
                    {
                        if (!Directory.Exists(current) ||
                            !IsDirectorySafeForElevation(current))
                        {
                            return false;
                        }
                    }

                    string? parent = Path.GetDirectoryName(current);
                    if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    current = parent;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void EnsureApplicationDirectoryProtected(string applicationDirectory)
        {
            try
            {
                EnsureNoReparsePoints(applicationDirectory);
                if (!Directory.Exists(applicationDirectory))
                {
                    throw new DirectoryNotFoundException(applicationDirectory);
                }

                ApplyDirectorySecurity(applicationDirectory, grantUsersRead: true);
                foreach (string entry in Directory.EnumerateFileSystemEntries(applicationDirectory))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException(
                            LocalizationService.Get("Update_ApplicationFolderSecurityFailed"));
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        EnsureApplicationDirectoryProtected(entry);
                    }
                    else
                    {
                        ApplyFileSecurity(entry, grantUsersRead: true);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    LocalizationService.Get("Update_ApplicationFolderSecurityFailed"), ex);
            }
        }

        public static void EnsureNoReparsePoints(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                string? current = fullPath;
                while (!string.IsNullOrEmpty(current))
                {
                    if (PathExists(current))
                    {
                        FileAttributes attributes = File.GetAttributes(current);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            throw new InvalidOperationException(
                                LocalizationService.Get("Update_SecureStorageFailed"));
                        }
                    }

                    current = Path.GetDirectoryName(current);
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    LocalizationService.Get("Update_SecureStorageFailed"), ex);
            }
        }

        public static void SecureDirectory(string path)
        {
            SecureDirectory(path, clearUntrusted: true);
        }

        private static void SecureDirectory(string path, bool clearUntrusted)
        {
            try
            {
                EnsureNoReparsePoints(path);

                bool existed = PathExists(path);
                if (existed && !Directory.Exists(path))
                {
                    throw new IOException($"{path} is not a directory.");
                }

                bool trusted = existed && IsTrustedDirectory(path);
                Directory.CreateDirectory(path);
                EnsureNoReparsePoints(path);

                if (existed && clearUntrusted && !trusted)
                {
                    ClearDirectoryContents(path);
                }

                ApplyDirectorySecurity(path);
                SecureChildren(path);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    LocalizationService.Get("Update_SecureStorageFailed"), ex);
            }
        }

        private static void SecureChildren(string path)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(path))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        LocalizationService.Get("Update_SecureStorageFailed"));
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    SecureDirectory(entry, clearUntrusted: false);
                }
                else
                {
                    ApplyFileSecurity(entry);
                }
            }
        }

        private static bool IsTrustedDirectory(string path)
        {
            var security = new DirectoryInfo(path).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access);
            var owner = security.GetOwner(typeof(SecurityIdentifier));
            return security.AreAccessRulesProtected &&
                   owner != null &&
                   (owner.Equals(AdministratorsSid) || owner.Equals(LocalSystemSid));
        }

        private static bool IsDirectorySafeForElevation(string path)
        {
            var security = new DirectoryInfo(path).GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner);
            var owner = security.GetOwner(typeof(SecurityIdentifier));
            if (owner == null ||
                (!owner.Equals(AdministratorsSid) &&
                 !owner.Equals(LocalSystemSid) &&
                 !owner.Equals(TrustedInstallerSid)))
            {
                return false;
            }

            foreach (FileSystemAccessRule rule in security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow ||
                    !IsUnprivilegedIdentity(rule.IdentityReference) ||
                    rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
                {
                    continue;
                }

                if ((rule.FileSystemRights & DangerousWriteRights) != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsApplicationDirectorySafe(string path)
        {
            var security = new DirectoryInfo(path).GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner);
            var owner = security.GetOwner(typeof(SecurityIdentifier));
            if (owner == null ||
                (!owner.Equals(AdministratorsSid) &&
                 !owner.Equals(LocalSystemSid) &&
                 !owner.Equals(TrustedInstallerSid)))
            {
                return false;
            }

            const FileSystemRights unsafeRights = FileSystemRights.Write |
                                                  DangerousWriteRights;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow ||
                    !IsUnprivilegedIdentity(rule.IdentityReference))
                {
                    continue;
                }

                if ((rule.FileSystemRights & unsafeRights) != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsUnprivilegedIdentity(IdentityReference identity)
        {
            return identity.Equals(BuiltinUsersSid) ||
                   identity.Equals(AuthenticatedUsersSid) ||
                   identity.Equals(EveryoneSid) ||
                   identity.Equals(InteractiveSid);
        }

        private static void ApplyDirectorySecurity(string path, bool grantUsersRead = false)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(AdministratorsSid);

            const InheritanceFlags inherit =
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            AddFullControl(security, AdministratorsSid, inherit);
            AddFullControl(security, LocalSystemSid, inherit);
            if (grantUsersRead)
            {
                AddReadExecute(security, BuiltinUsersSid, inherit);
            }

            new DirectoryInfo(path).SetAccessControl(security);
        }

        private static void ApplyFileSecurity(string path, bool grantUsersRead = false)
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.SetOwner(AdministratorsSid);

            AddFullControl(security, AdministratorsSid, InheritanceFlags.None);
            AddFullControl(security, LocalSystemSid, InheritanceFlags.None);
            if (grantUsersRead)
            {
                AddReadExecute(security, BuiltinUsersSid, InheritanceFlags.None);
            }

            new FileInfo(path).SetAccessControl(security);
        }

        private static void AddReadExecute(
            FileSystemSecurity security,
            SecurityIdentifier identity,
            InheritanceFlags inheritance)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.ReadAndExecute,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        private static void AddFullControl(
            FileSystemSecurity security,
            SecurityIdentifier identity,
            InheritanceFlags inheritance)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        private static void ClearDirectoryContents(string path)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(path))
            {
                DeleteEntry(entry);
            }
        }

        private static void DeleteEntry(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(path, recursive: false);
                }
                else
                {
                    File.Delete(path);
                }

                return;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                ClearDirectoryContents(path);
                Directory.Delete(path, recursive: false);
            }
            else
            {
                File.Delete(path);
            }
        }

        private static bool PathExists(string path)
        {
            try
            {
                File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        private static void RotateLogIfNeeded()
        {
            try
            {
                var log = new FileInfo(LogPath);
                if (log.Exists && log.Length > MaxLogBytes)
                {
                    File.Move(LogPath, LogPath + ".1", overwrite: true);
                }
            }
            catch
            {
            }
        }
    }
}
