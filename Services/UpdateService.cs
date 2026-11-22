using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CryoTray.Services
{
    public enum UpdateAssetKind
    {
        Archive,
        Installer,
        Executable,
        Rollback
    }

    public sealed class ReleaseInfo
    {
        public ReleaseInfo(string version, string tagName, string releaseUrl,
            string releaseNotes, string assetName, string assetDownloadUrl,
            UpdateAssetKind assetKind, string checksumName, string checksumDownloadUrl)
        {
            Version = version;
            TagName = tagName;
            ReleaseUrl = releaseUrl;
            ReleaseNotes = releaseNotes;
            AssetName = assetName;
            AssetDownloadUrl = assetDownloadUrl;
            AssetKind = assetKind;
            ChecksumName = checksumName;
            ChecksumDownloadUrl = checksumDownloadUrl;
        }

        public string Version { get; }
        public string TagName { get; }
        public string ReleaseUrl { get; }
        public string ReleaseNotes { get; }
        public string AssetName { get; }
        public string AssetDownloadUrl { get; }
        public UpdateAssetKind AssetKind { get; }
        public string ChecksumName { get; }
        public string ChecksumDownloadUrl { get; }
    }

    public sealed class PreparedUpdate
    {
        public PreparedUpdate(UpdateAssetKind kind, string packagePath, string workDirectory,
            string version, string verificationPath, string expectedSha256, string manifestPath)
        {
            Kind = kind;
            PackagePath = packagePath;
            WorkDirectory = workDirectory;
            Version = version;
            VerificationPath = verificationPath;
            ExpectedSha256 = expectedSha256;
            ManifestPath = manifestPath;
        }

        public UpdateAssetKind Kind { get; }
        public string PackagePath { get; }
        public string WorkDirectory { get; }
        public string Version { get; }
        public string VerificationPath { get; }
        public string ExpectedSha256 { get; }
        public string ManifestPath { get; }
    }

    public sealed record UpdateDownloadProgress(
        long ReceivedBytes,
        long? TotalBytes,
        double BytesPerSecond,
        int Percent,
        TimeSpan? Remaining);

    public sealed class UpdateService
    {
        private const string LatestReleaseApi = "https://api.github.com/repos/ZerroDevs/CryoTray/releases/latest";
        private const int MaxDownloadAttempts = 3;
        private const long MaxDownloadBytes = 512L * 1024 * 1024;
        private const int MaxReleaseNotesLength = 2400;
        private static readonly TimeSpan DownloadAttemptTimeout = TimeSpan.FromMinutes(15);
        private static readonly Regex VersionPattern = new(@"(?<![\d.])(\d+)\.(\d+)\.(\d+)(?![\d.])", RegexOptions.Compiled);
        private static readonly Regex HtmlTagPattern = new(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly HashSet<string> ReservedWindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };
        private static readonly HttpClient Http = CreateHttpClient();
        private static string? _currentVersion;
        private static string? _latestReleaseETag;
        private static ReleaseInfo? _latestReleaseCache;
        private static DateTime _latestReleaseRetryAfterUtc;

        public static UpdateService Instance { get; } = new();

        public static string CurrentVersion => _currentVersion ??= ReadInstalledVersion();

        public async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await GetLatestReleaseAsyncCore(
                        cancellationToken, allowConditionalRequest: true).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested &&
                    attempt < MaxDownloadAttempts - 1)
                {
                    await Task.Delay(
                        GetRetryDelay(new OperationCanceledException(), attempt + 1),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (
                    attempt < MaxDownloadAttempts - 1 &&
                    IsTransient(ex, cancellationToken))
                {
                    await Task.Delay(
                        GetRetryDelay(ex, attempt + 1),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task<ReleaseInfo?> GetLatestReleaseAsyncCore(
            CancellationToken cancellationToken, bool allowConditionalRequest)
        {
            if (DateTime.UtcNow < _latestReleaseRetryAfterUtc)
            {
                int minutes = Math.Max(
                    1,
                    (int)Math.Ceiling((_latestReleaseRetryAfterUtc - DateTime.UtcNow).TotalMinutes));
                throw new InvalidOperationException(
                    LocalizationService.Format("Update_RateLimited", minutes));
            }

            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(20));

            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            if (allowConditionalRequest && !string.IsNullOrWhiteSpace(_latestReleaseETag))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", _latestReleaseETag);
            }

            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestTimeout.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (_latestReleaseCache != null)
                {
                    return _latestReleaseCache;
                }

                _latestReleaseETag = null;
                return await GetLatestReleaseAsyncCore(
                    cancellationToken, allowConditionalRequest: false).ConfigureAwait(false);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (IsRateLimited(response))
            {
                SetRateLimit(response);
                int minutes = Math.Max(
                    1,
                    (int)Math.Ceiling((_latestReleaseRetryAfterUtc - DateTime.UtcNow).TotalMinutes));
                throw new InvalidOperationException(
                    LocalizationService.Format("Update_RateLimited", minutes));
            }

            response.EnsureSuccessStatusCode();
            UpdateRateLimitFromHeaders(response);

            await using var stream = await response.Content
                .ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                stream, cancellationToken: requestTimeout.Token).ConfigureAwait(false);
            var root = document.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString() ?? string.Empty
                : string.Empty;

            if (!TryParseVersion(tagName, out var version))
            {
                throw new InvalidOperationException(LocalizationService.Get("Update_NoVersion"));
            }

            bool installed = UpdateInstallationService.IsInstalled();
            var asset = SelectAsset(root, installed);
            if (asset == null)
            {
                throw new InvalidOperationException(LocalizationService.Get(
                    installed ? "Update_NoInstaller" : "Update_NoDownload"));
            }

            var checksum = SelectChecksumAsset(root, asset.Name);
            string releaseUrl = root.TryGetProperty("html_url", out var urlElement)
                ? urlElement.GetString() ?? string.Empty
                : string.Empty;
            string body = root.TryGetProperty("body", out var bodyElement) &&
                          bodyElement.ValueKind == JsonValueKind.String
                ? bodyElement.GetString() ?? string.Empty
                : string.Empty;

            var release = new ReleaseInfo(
                version.ToString(3),
                tagName,
                releaseUrl,
                NormalizeReleaseNotes(body),
                asset.Name,
                asset.Url,
                asset.Kind,
                checksum?.Name ?? string.Empty,
                checksum?.Url ?? string.Empty);

            _latestReleaseCache = release;
            _latestReleaseETag = response.Headers.ETag?.Tag;
            return release;
        }

        public static bool IsNewer(string candidate, string current)
        {
            if (!TryParseVersion(candidate, out var candidateVersion))
            {
                return false;
            }

            if (!TryParseVersion(current, out var currentVersion))
            {
                return true;
            }

            return candidateVersion > currentVersion;
        }

        public async Task<PreparedUpdate> DownloadAsync(
            ReleaseInfo release,
            IProgress<UpdateDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            UpdateStorage.EnsureUpdateRoot();

            string workDirectory = GetWorkDirectory(release);
            UpdateStorage.SecureDirectory(workDirectory);
            using var downloadLock = AcquireDownloadLock(workDirectory);
            CleanupStaleDownloads(workDirectory);

            string fileName = Path.GetFileName(release.AssetName);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = release.AssetKind == UpdateAssetKind.Archive
                    ? "update.zip"
                    : "update.exe";
            }

            string assetPath = Path.Combine(workDirectory, fileName);
            await DownloadFileAsync(
                release.AssetDownloadUrl, assetPath, progress, cancellationToken)
                .ConfigureAwait(false);

            string expectedSha256 = await VerifyChecksumAsync(
                release, assetPath, workDirectory, cancellationToken).ConfigureAwait(false);

            if (release.AssetKind != UpdateAssetKind.Archive)
            {
                AuthenticodeVerifier.VerifyOrAllowUnsigned(assetPath);
                return new PreparedUpdate(
                    release.AssetKind,
                    assetPath,
                    workDirectory,
                    release.Version,
                    assetPath,
                    expectedSha256,
                    string.Empty);
            }

            string extractDirectory = Path.Combine(workDirectory, "payload");
            if (Directory.Exists(extractDirectory))
            {
                TryDeleteDirectory(extractDirectory);
            }
            UpdateStorage.SecureDirectory(extractDirectory);
            ZipFile.ExtractToDirectory(assetPath, extractDirectory, true);

            string? payloadDirectory = FindPayloadDirectory(extractDirectory);
            if (payloadDirectory == null)
            {
                throw new InvalidOperationException(LocalizationService.Get("Update_NoPayload"));
            }

            string manifestPath = Path.Combine(payloadDirectory, "update-manifest.json");
            if (!File.Exists(manifestPath))
            {
                if (release.AssetName.Contains(
                    "Update", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        LocalizationService.Get("Update_ManifestMissing"));
                }

                manifestPath = string.Empty;
            }
            else
            {
                ValidateUpdateManifest(
                    manifestPath, payloadDirectory, release.Version);
            }

            AuthenticodeVerifier.VerifyOrAllowUnsigned(
                Path.Combine(payloadDirectory, "CryoTray.exe"));
            return new PreparedUpdate(
                UpdateAssetKind.Archive,
                payloadDirectory,
                workDirectory,
                release.Version,
                assetPath,
                expectedSha256,
                manifestPath);
        }

        public static void LaunchRollback(string backupDirectory)
        {
            UpdateStorage.EnsureRollbackRoot();
            if (!Directory.Exists(backupDirectory))
            {
                throw new InvalidOperationException(LocalizationService.Get("Update_BackupMissing"));
            }

            var backup = UpdateBackupService.GetCurrent();
            if (backup == null ||
                !string.Equals(
                    Path.GetFullPath(backup.Path),
                    Path.GetFullPath(backupDirectory),
                    StringComparison.OrdinalIgnoreCase) ||
                !UpdateBackupService.VerifyBackup(backup))
            {
                throw new InvalidOperationException(
                    LocalizationService.Get("Update_BackupTampered"));
            }

            UpdateStorage.EnsureUpdateRoot();
            string workDirectory = Path.Combine(
                UpdateStorage.UpdateRoot,
                "Rollback",
                Guid.NewGuid().ToString("N"));
            UpdateStorage.SecureDirectory(workDirectory);

            LaunchUpdater(new PreparedUpdate(
                UpdateAssetKind.Rollback,
                backupDirectory,
                workDirectory,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty), backupDirectory: backupDirectory);
        }

        public static void LaunchUpdater(
            PreparedUpdate update,
            string? updateId = null,
            string? backupDirectory = null)
        {
            if (update.Kind != UpdateAssetKind.Rollback)
            {
                if (!File.Exists(update.VerificationPath) ||
                    string.IsNullOrWhiteSpace(update.ExpectedSha256))
                {
                    throw new InvalidOperationException(
                        LocalizationService.Get("Update_ChecksumMismatch"));
                }
            }

            string targetDirectory = AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string executablePath = Path.Combine(targetDirectory, "CryoTray.exe");

            if (!File.Exists(executablePath))
            {
                executablePath = Environment.ProcessPath ?? string.Empty;
            }

            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException(LocalizationService.Get("Update_ExeMissing"));
            }

            if (!UpdateStorage.IsSafeApplicationLocation(targetDirectory))
            {
                throw new InvalidOperationException(
                    LocalizationService.Get("Update_UnsafeLocation"));
            }

            UpdateStorage.EnsureApplicationDirectoryProtected(targetDirectory);
            UpdateStorage.EnsureNoReparsePoints(targetDirectory);

            if (!string.IsNullOrWhiteSpace(backupDirectory))
            {
                var backup = UpdateBackupService.GetCurrent();
                if (backup == null ||
                    !string.Equals(
                        Path.GetFullPath(backup.Path),
                        Path.GetFullPath(backupDirectory),
                        StringComparison.OrdinalIgnoreCase) ||
                    !UpdateBackupService.VerifyBackup(backup))
                {
                    throw new InvalidOperationException(
                        LocalizationService.Get("Update_BackupTampered"));
                }
            }

            UpdateStorage.EnsureUpdateRoot();
            UpdateStorage.SecureDirectory(update.WorkDirectory);

            string scriptPath = Path.Combine(update.WorkDirectory, "apply-update.ps1");
            File.WriteAllText(scriptPath, UpdaterScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            string powershellPath = Path.Combine(
                Environment.SystemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            if (!Path.IsPathFullyQualified(powershellPath) || !File.Exists(powershellPath))
            {
                throw new FileNotFoundException(
                    LocalizationService.Get("Update_PowerShellMissing"));
            }

            long processStartTicks = 0;
            try
            {
                using var currentProcess = Process.GetCurrentProcess();
                processStartTicks = currentProcess.StartTime.ToUniversalTime().Ticks;
            }
            catch
            {
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = powershellPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = update.WorkDirectory
            };

            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-WaitPid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-WaitProcessStartTicks");
            startInfo.ArgumentList.Add(processStartTicks.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-Kind");
            startInfo.ArgumentList.Add(update.Kind.ToString());
            startInfo.ArgumentList.Add("-PackagePath");
            startInfo.ArgumentList.Add(update.PackagePath);
            startInfo.ArgumentList.Add("-TargetDirectory");
            startInfo.ArgumentList.Add(targetDirectory);
            startInfo.ArgumentList.Add("-ExePath");
            startInfo.ArgumentList.Add(executablePath);
            startInfo.ArgumentList.Add("-VerificationPath");
            startInfo.ArgumentList.Add(update.VerificationPath);
            startInfo.ArgumentList.Add("-ExpectedSha256");
            startInfo.ArgumentList.Add(update.ExpectedSha256);
            startInfo.ArgumentList.Add("-ManifestPath");
            startInfo.ArgumentList.Add(update.ManifestPath);
            startInfo.ArgumentList.Add("-ExpectedVersion");
            startInfo.ArgumentList.Add(update.Version);
            startInfo.ArgumentList.Add("-BackupPath");
            startInfo.ArgumentList.Add(backupDirectory ?? string.Empty);
            startInfo.ArgumentList.Add("-UpdateId");
            startInfo.ArgumentList.Add(updateId ?? string.Empty);
            startInfo.ArgumentList.Add("-CompletionPath");
            startInfo.ArgumentList.Add(
                update.Kind == UpdateAssetKind.Rollback || !string.IsNullOrWhiteSpace(updateId)
                    ? UpdateStorage.CompletionStatePath
                    : string.Empty);
            startInfo.ArgumentList.Add("-LogPath");
            startInfo.ArgumentList.Add(UpdateStorage.LogPath);

            using var updaterProcess = Process.Start(startInfo);
            if (updaterProcess == null)
            {
                throw new InvalidOperationException(
                    LocalizationService.Get("Update_UpdaterStartFailed"));
            }
        }

        private static ReleaseChecksum? SelectChecksumAsset(JsonElement root, string assetName)
        {
            if (!root.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string expectedName = assetName + ".sha256";
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameElement) ||
                    !asset.TryGetProperty("browser_download_url", out var urlElement))
                {
                    continue;
                }

                string? name = nameElement.GetString();
                string? url = urlElement.GetString();
                if (string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase) &&
                    Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                    uri.Scheme == Uri.UriSchemeHttps)
                {
                    return new ReleaseChecksum(name!, url!);
                }
            }

            return null;
        }

        private static async Task<string> VerifyChecksumAsync(
            ReleaseInfo release,
            string assetPath,
            string workDirectory,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(release.ChecksumDownloadUrl))
            {
                throw new InvalidOperationException(
                    LocalizationService.Get("Update_ChecksumMissing"));
            }

            string checksumPath = Path.Combine(workDirectory, "checksum.sha256");
            await DownloadFileAsync(
                release.ChecksumDownloadUrl, checksumPath, null, cancellationToken)
                .ConfigureAwait(false);

            if (new FileInfo(checksumPath).Length > 64 * 1024)
            {
                throw new InvalidOperationException(
                    LocalizationService.Get("Update_ChecksumMalformed"));
            }

            string checksumText = await File.ReadAllTextAsync(checksumPath, cancellationToken)
                .ConfigureAwait(false);
            string? expectedHex = ParseExpectedSha256(checksumText, release.AssetName);
            if (string.IsNullOrEmpty(expectedHex))
            {
                throw new InvalidOperationException(
                    LocalizationService.Get("Update_ChecksumMalformed"));
            }

            byte[] expected = Convert.FromHexString(expectedHex);
            byte[] actual;
            await using (var stream = File.OpenRead(assetPath))
            {
                actual = await SHA256.HashDataAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                throw new InvalidOperationException(
                    LocalizationService.Get("Update_ChecksumMismatch"));
            }

            return expectedHex;
        }

        internal static string? ParseExpectedSha256(string? checksumText, string? expectedAssetName)
        {
            if (string.IsNullOrWhiteSpace(checksumText))
            {
                return null;
            }

            string targetName = Path.GetFileName(expectedAssetName ?? string.Empty);
            var bareHashes = new List<string>();
            var namedHashes = new List<(string Hash, string FileName)>();

            string[] lines = checksumText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var match = Regex.Match(line, @"^([a-fA-F0-9]{64})(?:\s+\*?(.+))?$");
                if (!match.Success)
                {
                    continue;
                }

                string hash = match.Groups[1].Value.ToLowerInvariant();
                string filePart = match.Groups[2].Value.Trim();
                if (filePart.Length == 0)
                {
                    bareHashes.Add(hash);
                }
                else
                {
                    namedHashes.Add((hash, Path.GetFileName(filePart)));
                }
            }

            if (!string.IsNullOrEmpty(targetName))
            {
                foreach (var entry in namedHashes)
                {
                    if (string.Equals(entry.FileName, targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry.Hash;
                    }
                }

                if (namedHashes.Count > 0)
                {
                    return null;
                }
            }
            else if (namedHashes.Count == 1)
            {
                return namedHashes[0].Hash;
            }

            return bareHashes.Count == 1 ? bareHashes[0] : null;
        }

        internal static ReleaseAsset? SelectAsset(JsonElement root, bool installed)
        {
            if (!root.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var candidates = new List<ReleaseAsset>();

            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameElement)
                    || !asset.TryGetProperty("browser_download_url", out var urlElement))
                {
                    continue;
                }

                string? name = nameElement.GetString();
                string? url = urlElement.GetString();

                if (string.IsNullOrWhiteSpace(name)
                    || string.IsNullOrWhiteSpace(url)
                    || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps)
                {
                    continue;
                }

                UpdateAssetKind? kind = null;

                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    kind = UpdateAssetKind.Archive;
                }
                else if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    bool isUninstaller = name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("unins", StringComparison.OrdinalIgnoreCase);
                    bool isInstaller = !isUninstaller && (
                        name.Contains("setup", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("installer", StringComparison.OrdinalIgnoreCase));

                    if (!isUninstaller)
                    {
                        kind = isInstaller ? UpdateAssetKind.Installer : UpdateAssetKind.Executable;
                    }
                }

                if (kind.HasValue)
                {
                    candidates.Add(new ReleaseAsset(name, url, kind.Value));
                }
            }

            if (installed)
            {
                return candidates.FirstOrDefault(
                    asset => asset.Kind == UpdateAssetKind.Installer);
            }

            var updateArchive = candidates.FirstOrDefault(asset =>
                asset.Kind == UpdateAssetKind.Archive &&
                asset.Name.Contains("update", StringComparison.OrdinalIgnoreCase));

            return updateArchive
                ?? candidates.FirstOrDefault(asset => asset.Kind == UpdateAssetKind.Archive)
                ?? candidates.FirstOrDefault(asset => asset.Kind == UpdateAssetKind.Executable);
        }

        private static string? FindPayloadDirectory(string extractDirectory)
        {
            if (File.Exists(Path.Combine(extractDirectory, "CryoTray.exe")))
            {
                return extractDirectory;
            }

            foreach (string directory in Directory.GetDirectories(extractDirectory))
            {
                if (File.Exists(Path.Combine(directory, "CryoTray.exe")))
                {
                    return directory;
                }
            }

            return null;
        }

        private static string GetWorkDirectory(ReleaseInfo release)
        {
            string safeVersion = MakeSafeDirectoryName(release.Version);
            string key = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    release.AssetName + "|" + release.AssetDownloadUrl)))
                .Substring(0, 16)
                .ToLowerInvariant();

            return Path.Combine(
                UpdateStorage.DownloadsRoot,
                $"{safeVersion}-{key}");
        }

        private static FileStream AcquireDownloadLock(string workDirectory)
        {
            string lockPath = Path.Combine(workDirectory, "download.lock");
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    LocalizationService.Get("Update_DownloadBusy"), ex);
            }
        }

        public static long GetDownloadCacheSizeBytes()
        {
            try
            {
                UpdateStorage.EnsureUpdateRoot();
                return Directory.Exists(UpdateStorage.DownloadsRoot)
                    ? GetDirectorySize(UpdateStorage.DownloadsRoot)
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        public static void ClearDownloadCache()
        {
            UpdateStorage.EnsureUpdateRoot();
            if (!Directory.Exists(UpdateStorage.DownloadsRoot))
            {
                return;
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(
                UpdateStorage.DownloadsRoot))
            {
                DeleteCacheEntry(entry);
            }
        }

        private static long GetDirectorySize(string directory)
        {
            long total = 0;
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    total += GetDirectorySize(entry);
                }
                else
                {
                    total += new FileInfo(entry).Length;
                }
            }

            return total;
        }

        private static void DeleteCacheEntry(string path)
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
                foreach (string entry in Directory.EnumerateFileSystemEntries(path))
                {
                    DeleteCacheEntry(entry);
                }

                Directory.Delete(path, recursive: false);
            }
            else
            {
                File.Delete(path);
            }
        }

        private static void CleanupStaleDownloads(string currentWorkDirectory)
        {
            try
            {
                string current = Path.GetFullPath(currentWorkDirectory);
                foreach (string directory in Directory.GetDirectories(
                    UpdateStorage.DownloadsRoot))
                {
                    try
                    {
                        if (string.Equals(
                            Path.GetFullPath(directory),
                            current,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var info = new DirectoryInfo(directory);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        if (info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7) &&
                            !IsDownloadDirectoryLocked(directory))
                        {
                            TryDeleteDirectory(directory);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static bool IsDownloadDirectoryLocked(string directory)
        {
            string lockPath = Path.Combine(directory, "download.lock");
            if (!File.Exists(lockPath))
            {
                return false;
            }

            try
            {
                using var stream = new FileStream(
                    lockPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void ValidateUpdateManifest(
            string manifestPath,
            string payloadDirectory,
            string expectedVersion)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException("Manifest root is not an object.");
                }

                string version = root.TryGetProperty("version", out var versionElement) &&
                                 versionElement.ValueKind == JsonValueKind.String
                    ? versionElement.GetString() ?? string.Empty
                    : string.Empty;
                if (!string.Equals(
                    version.TrimStart('v'),
                    expectedVersion,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new FormatException("Manifest version does not match the release.");
                }

                if (!root.TryGetProperty("files", out var filesElement) ||
                    filesElement.ValueKind != JsonValueKind.Array)
                {
                    throw new FormatException("Manifest files are missing.");
                }

                if (!root.TryGetProperty("hashes", out var hashesElement) ||
                    hashesElement.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException("Manifest hashes are missing.");
                }

                var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in filesElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String)
                    {
                        throw new FormatException("Manifest file entry is not a string.");
                    }

                    string relative = NormalizeRelativePath(entry.GetString());
                    if (!IsSafeRelativePath(relative))
                    {
                        throw new FormatException("Manifest contains an unsafe file path.");
                    }

                    files.Add(relative);
                }

                if (!files.Contains("CryoTray.exe"))
                {
                    throw new FormatException("Manifest does not contain CryoTray.exe.");
                }

                var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in hashesElement.EnumerateObject())
                {
                    string relative = NormalizeRelativePath(property.Name);
                    if (!files.Contains(relative) ||
                        property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new FormatException("Manifest hash entry is invalid.");
                    }

                    string? expectedHash = property.Value.GetString();
                    if (!IsSha256(expectedHash))
                    {
                        throw new FormatException("Manifest hash is invalid.");
                    }

                    string source = Path.Combine(
                        payloadDirectory,
                        relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(source))
                    {
                        throw new FileNotFoundException(
                            "Manifest file is missing from the payload.", source);
                    }

                    using var stream = File.OpenRead(source);
                    string actual = Convert.ToHexString(SHA256.HashData(stream))
                        .ToLowerInvariant();
                    if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "Manifest file hash does not match the payload.");
                    }

                    validated.Add(relative);
                }

                if (validated.Count != files.Count)
                {
                    throw new FormatException("Manifest is missing one or more file hashes.");
                }

                if (root.TryGetProperty("delete", out var deleteElement) &&
                    deleteElement.ValueKind != JsonValueKind.Null)
                {
                    if (deleteElement.ValueKind != JsonValueKind.Array)
                    {
                        throw new FormatException("Manifest delete list is invalid.");
                    }

                    foreach (var entry in deleteElement.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.String ||
                            !IsSafeRelativePath(NormalizeRelativePath(entry.GetString())))
                        {
                            throw new FormatException("Manifest delete path is unsafe.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    LocalizationService.Get("Update_ManifestInvalid"), ex);
            }
        }

        private static string NormalizeRelativePath(string? path)
        {
            return (path ?? string.Empty).Replace('\\', '/');
        }

        internal static bool IsSafeRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                Path.IsPathRooted(path) ||
                path.Contains(':') ||
                path.Contains('\0') ||
                path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return false;
            }

            string[] parts = path.Replace('\\', '/').Split('/');
            foreach (string part in parts)
            {
                if (part.Length == 0 ||
                    part == "." ||
                    part == ".." ||
                    part.EndsWith('.') ||
                    part.EndsWith(' '))
                {
                    return false;
                }

                int dot = part.IndexOf('.');
                string baseName = dot >= 0 ? part[..dot] : part;
                if (ReservedWindowsDeviceNames.Contains(baseName))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsSha256(string? hash)
        {
            return hash != null &&
                   hash.Length == 64 &&
                   hash.All(Uri.IsHexDigit);
        }

        private static async Task DownloadFileAsync(
            string url,
            string destination,
            IProgress<UpdateDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            string partPath = destination + ".part";
            string metaPath = destination + ".part.meta";

            for (int attempt = 0; attempt < MaxDownloadAttempts; attempt++)
            {
                try
                {
                    await DownloadAttemptAsync(
                        url,
                        destination,
                        partPath,
                        metaPath,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (
                    attempt < MaxDownloadAttempts - 1 &&
                    IsTransient(ex, cancellationToken))
                {
                    TimeSpan delay = GetRetryDelay(ex, attempt + 1);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static async Task DownloadAttemptAsync(
            string url,
            string destination,
            string partPath,
            string metaPath,
            IProgress<UpdateDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            long offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (offset > MaxDownloadBytes)
            {
                TryDeleteFile(partPath);
                TryDeleteFile(metaPath);
                offset = 0;
            }

            string resumeValidator = offset > 0 && File.Exists(metaPath)
                ? File.ReadAllText(metaPath).Trim()
                : string.Empty;

            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptTimeout.CancelAfter(DownloadAttemptTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                if (!string.IsNullOrWhiteSpace(resumeValidator))
                {
                    request.Headers.TryAddWithoutValidation("If-Range", resumeValidator);
                }
            }

            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                attemptTimeout.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
            {
                TryDeleteFile(partPath);
                TryDeleteFile(metaPath);
                throw new UpdateTransientException(1);
            }

            if (offset > 0 && response.StatusCode == HttpStatusCode.OK)
            {
                offset = 0;
            }

            if ((int)response.StatusCode == 429 || (int)response.StatusCode == 503)
            {
                int retryAfter = ReadRetryAfterSeconds(response) ?? 2;
                throw new UpdateTransientException(Math.Clamp(retryAfter, 1, 30));
            }

            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentRange?.Length
                ?? (response.Content.Headers.ContentLength.HasValue
                    ? offset + response.Content.Headers.ContentLength.Value
                    : null);

            if (totalBytes.HasValue && totalBytes.Value > MaxDownloadBytes)
            {
                throw new InvalidOperationException(LocalizationService.Get("Update_TooLarge"));
            }

            EnsureFreeSpace(destination, totalBytes, offset);

            string? validator = response.Headers.ETag?.Tag;
            if (string.IsNullOrWhiteSpace(validator))
            {
                validator = response.Content.Headers.LastModified?.ToString("R");
            }

            if (!string.IsNullOrWhiteSpace(validator))
            {
                File.WriteAllText(metaPath, validator);
            }

            await using var source = await response.Content
                .ReadAsStreamAsync(attemptTimeout.Token).ConfigureAwait(false);
            await using var target = new FileStream(
                partPath,
                offset > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);

            byte[] buffer = new byte[81920];
            long received = offset;
            long receivedThisAttempt = 0;
            var attemptWatch = Stopwatch.StartNew();
            var reportWatch = Stopwatch.StartNew();
            int read;

            ReportDownloadProgress(
                progress, received, totalBytes, receivedThisAttempt,
                attemptWatch, force: true);

            while ((read = await source.ReadAsync(
                buffer, attemptTimeout.Token).ConfigureAwait(false)) > 0)
            {
                if (received + read > MaxDownloadBytes)
                {
                    throw new InvalidOperationException(
                        LocalizationService.Get("Update_TooLarge"));
                }

                await target.WriteAsync(
                    buffer.AsMemory(0, read), attemptTimeout.Token).ConfigureAwait(false);
                received += read;
                receivedThisAttempt += read;

                if (reportWatch.ElapsedMilliseconds >= 200)
                {
                    reportWatch.Restart();
                    ReportDownloadProgress(
                        progress, received, totalBytes, receivedThisAttempt,
                        attemptWatch, force: false);
                }
            }

            await target.FlushAsync(attemptTimeout.Token).ConfigureAwait(false);
            target.Close();
            File.Move(partPath, destination, true);
            TryDeleteFile(metaPath);

            progress?.Report(new UpdateDownloadProgress(
                received,
                totalBytes,
                receivedThisAttempt / Math.Max(0.001, attemptWatch.Elapsed.TotalSeconds),
                100,
                TimeSpan.Zero));
        }

        private static void ReportDownloadProgress(
            IProgress<UpdateDownloadProgress>? progress,
            long received,
            long? totalBytes,
            long receivedThisAttempt,
            Stopwatch attemptWatch,
            bool force)
        {
            if (progress == null)
            {
                return;
            }

            double speed = receivedThisAttempt /
                Math.Max(0.001, attemptWatch.Elapsed.TotalSeconds);
            TimeSpan? remaining = null;
            if (totalBytes.HasValue && speed > 1024)
            {
                double seconds = Math.Max(0, totalBytes.Value - received) / speed;
                remaining = TimeSpan.FromSeconds(seconds);
            }

            int percent = totalBytes.HasValue && totalBytes.Value > 0
                ? (int)Math.Clamp(received * 100 / totalBytes.Value, 0, 100)
                : 0;

            if (!force && percent >= 100)
            {
                return;
            }

            progress.Report(new UpdateDownloadProgress(
                received, totalBytes, speed, percent, remaining));
        }

        private static void EnsureFreeSpace(string destination, long? totalBytes, long offset)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(destination));
                if (string.IsNullOrWhiteSpace(root))
                {
                    return;
                }

                long required = totalBytes.HasValue
                    ? Math.Max(0, totalBytes.Value - offset) + 64L * 1024 * 1024
                    : 256L * 1024 * 1024;

                if (new DriveInfo(root).AvailableFreeSpace < required)
                {
                    throw new InvalidOperationException(
                        LocalizationService.Get("Update_NotEnoughSpace"));
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch
            {
            }
        }

        private static bool IsTransient(Exception exception, CancellationToken cancellationToken)
        {
            if (exception is UpdateTransientException)
            {
                return true;
            }

            if (exception is HttpRequestException { StatusCode: { } status })
            {
                int code = (int)status;
                return code == 408 || code == 429 || code >= 500;
            }

            if (exception is HttpRequestException or IOException)
            {
                return true;
            }

            return exception is OperationCanceledException &&
                   !cancellationToken.IsCancellationRequested;
        }

        private static TimeSpan GetRetryDelay(Exception exception, int attempt)
        {
            if (exception is UpdateTransientException transient &&
                transient.RetryAfterSeconds > 0)
            {
                return TimeSpan.FromSeconds(
                    Math.Clamp(transient.RetryAfterSeconds, 1, 30));
            }

            int seconds = Math.Min(30, 1 << Math.Min(attempt, 5));
            int jitter = Random.Shared.Next(250, 750);
            return TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(jitter);
        }

        private static bool IsRateLimited(HttpResponseMessage response)
        {
            int status = (int)response.StatusCode;
            if (status == 429)
            {
                return true;
            }

            if (status != 403)
            {
                return false;
            }

            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues) &&
                int.TryParse(remainingValues.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int remaining) &&
                remaining == 0)
            {
                return true;
            }

            return response.Headers.RetryAfter != null || response.Headers.Contains("Retry-After");
        }

        private static void SetRateLimit(HttpResponseMessage response)
        {
            TimeSpan delay = TimeSpan.FromMinutes(60);

            int? retryAfter = ReadRetryAfterSeconds(response);
            if (retryAfter.HasValue)
            {
                delay = TimeSpan.FromSeconds(Math.Max(1, retryAfter.Value));
            }
            else if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
                     long.TryParse(resetValues.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long resetSeconds))
            {
                var reset = DateTimeOffset.FromUnixTimeSeconds(resetSeconds);
                delay = reset - DateTimeOffset.UtcNow;
            }

            if (delay <= TimeSpan.Zero)
            {
                delay = TimeSpan.FromMinutes(1);
            }

            _latestReleaseRetryAfterUtc = DateTime.UtcNow.Add(delay);
        }

        private static void UpdateRateLimitFromHeaders(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues) ||
                !int.TryParse(remainingValues.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int remaining) ||
                remaining > 0)
            {
                return;
            }

            SetRateLimit(response);
        }

        private static int? ReadRetryAfterSeconds(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter?.Delta != null)
            {
                return (int)Math.Ceiling(retryAfter.Delta.Value.TotalSeconds);
            }

            if (retryAfter?.Date != null)
            {
                return (int)Math.Ceiling((retryAfter.Date.Value - DateTimeOffset.UtcNow).TotalSeconds);
            }

            if (response.Headers.TryGetValues("Retry-After", out var values) &&
                int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
            {
                return seconds;
            }

            return null;
        }

        private static string NormalizeReleaseNotes(string notes)
        {
            if (string.IsNullOrWhiteSpace(notes))
            {
                return string.Empty;
            }

            string text = WebUtility.HtmlDecode(notes);
            text = HtmlTagPattern.Replace(text, " ");
            text = Regex.Replace(text, @"\r\n?", "\n");
            text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]+\)", string.Empty);
            text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
            text = Regex.Replace(text, @"(?m)^\s{0,3}#{1,6}\s*", string.Empty);
            text = Regex.Replace(text, @"\*\*(.*?)\*\*", "$1");
            text = Regex.Replace(text, @"`([^`]+)`", "$1");
            text = Regex.Replace(text, @"[ \t]{2,}", " ");
            text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();

            if (text.Length > MaxReleaseNotesLength)
            {
                text = text[..MaxReleaseNotesLength].TrimEnd() + "…";
            }

            return text;
        }

        private static bool TryParseVersion(string? text, out Version version)
        {
            version = new Version(0, 0, 0);

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var match = VersionPattern.Match(text);
            if (!match.Success)
            {
                return false;
            }

            if (Version.TryParse(
                $"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}",
                out Version? parsed))
            {
                version = parsed;
                return true;
            }

            return false;
        }

        private static string ReadInstalledVersion()
        {
            try
            {
                string versionFile = Path.Combine(AppContext.BaseDirectory, "VERSION");
                if (File.Exists(versionFile) &&
                    TryParseVersion(File.ReadAllText(versionFile), out var fileVersion))
                {
                    return fileVersion.ToString(3);
                }
            }
            catch
            {
            }

            var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (assemblyVersion != null)
            {
                return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(0, assemblyVersion.Build)}";
            }

            return "0.0.0";
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd("CryoTray");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        private static string MakeSafeDirectoryName(string value)
        {
            char[] invalidCharacters = Path.GetInvalidFileNameChars();
            foreach (char invalidCharacter in invalidCharacters)
            {
                value = value.Replace(invalidCharacter, '_');
            }

            return string.IsNullOrWhiteSpace(value) ? "update" : value;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
            }
        }

        internal sealed class ReleaseAsset
        {
            public ReleaseAsset(string name, string url, UpdateAssetKind kind)
            {
                Name = name;
                Url = url;
                Kind = kind;
            }

            public string Name { get; }
            public string Url { get; }
            public UpdateAssetKind Kind { get; }
        }

        private sealed class ReleaseChecksum
        {
            public ReleaseChecksum(string name, string url)
            {
                Name = name;
                Url = url;
            }

            public string Name { get; }
            public string Url { get; }
        }

        private sealed class UpdateTransientException : Exception
        {
            public UpdateTransientException(int retryAfterSeconds)
                : base("Transient update download failure.")
            {
                RetryAfterSeconds = retryAfterSeconds;
            }

            public int RetryAfterSeconds { get; }
        }

        private const string UpdaterScript = """
            param(
                [Parameter(Mandatory=$true)][int]$WaitPid,
                [Parameter(Mandatory=$false)][long]$WaitProcessStartTicks = 0,
                [Parameter(Mandatory=$true)][ValidateSet('Archive','Executable','Installer','Rollback')][string]$Kind,
                [Parameter(Mandatory=$true)][string]$PackagePath,
                [Parameter(Mandatory=$true)][string]$TargetDirectory,
                [Parameter(Mandatory=$true)][string]$ExePath,
                [Parameter(Mandatory=$false)][string]$VerificationPath = '',
                [Parameter(Mandatory=$false)][string]$ExpectedSha256 = '',
                [Parameter(Mandatory=$false)][string]$ExpectedVersion = '',
                [Parameter(Mandatory=$false)][string]$ManifestPath = '',
                [Parameter(Mandatory=$false)][string]$BackupPath = '',
                [Parameter(Mandatory=$false)][string]$UpdateId = '',
                [Parameter(Mandatory=$false)][string]$CompletionPath = '',
                [Parameter(Mandatory=$true)][string]$LogPath
            )

            $ErrorActionPreference = 'Stop'
            $script:targetModified = $false

            function Write-UpdateLog([string]$message) {
                try {
                    Add-Content -LiteralPath $LogPath -Value ("[{0}] {1}" -f (Get-Date -Format o), $message)
                } catch {
                }
            }

            function Test-ReparsePoint([string]$path) {
                try {
                    $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
                    return (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
                } catch [System.Management.Automation.ItemNotFoundException] {
                    return $false
                } catch [System.IO.FileNotFoundException] {
                    return $false
                } catch [System.IO.DirectoryNotFoundException] {
                    return $false
                }
            }

            function Assert-NoReparseChain([string]$path) {
                $current = [IO.Path]::GetFullPath($path)
                while ($current) {
                    if (Test-ReparsePoint $current) {
                        throw "Refusing to use a reparse point: $current"
                    }
                    $parent = Split-Path -Parent $current
                    if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $current) { break }
                    $current = $parent
                }
            }

            function Assert-NoReparse([string]$root, [string]$path) {
                $rootFull = [IO.Path]::GetFullPath($root).TrimEnd('\')
                if ($rootFull -match '^[A-Za-z]:$') { $rootFull = $rootFull + '\' }
                $prefix = $rootFull.TrimEnd('\') + '\'
                $full = [IO.Path]::GetFullPath($path)
                if (-not ($full.TrimEnd('\').Equals($rootFull.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) {
                    throw "Path is outside the expected directory: $path"
                }
                if (Test-ReparsePoint $rootFull) {
                    throw "Refusing to use a reparse point: $rootFull"
                }
                $relative = $full.Substring($rootFull.TrimEnd('\').Length).TrimStart('\')
                $cursor = $rootFull.TrimEnd('\')
                foreach ($part in $relative.Split('\')) {
                    if ([string]::IsNullOrEmpty($part)) { continue }
                    $cursor = Join-Path $cursor $part
                    if (Test-ReparsePoint $cursor) {
                        throw "Refusing to use a reparse point: $cursor"
                    }
                }
            }

            function Resolve-ChildPath([string]$root, [string]$relative) {
                $rootFull = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
                $full = [IO.Path]::GetFullPath((Join-Path $root $relative))
                if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Path is outside the expected directory: $relative"
                }
                return $full
            }

            function Copy-TreeSafe([string]$source, [string]$destination) {
                Assert-NoReparse $TargetDirectory $destination
                if (Test-ReparsePoint $source) {
                    throw "Refusing to copy a reparse point: $source"
                }
                $sourceItem = Get-Item -LiteralPath $source -Force
                if ($sourceItem.PSIsContainer) {
                    if (Test-Path -LiteralPath $destination -PathType Leaf) {
                        throw "Cannot replace a file with a directory: $destination"
                    }
                    if (-not (Test-Path -LiteralPath $destination)) {
                        New-Item -ItemType Directory -Path $destination -Force | Out-Null
                    }
                    foreach ($child in Get-ChildItem -LiteralPath $source -Force) {
                        Copy-TreeSafe $child.FullName (Join-Path $destination $child.Name)
                    }
                } else {
                    if (Test-Path -LiteralPath $destination -PathType Container) {
                        throw "Cannot replace a directory with a file: $destination"
                    }
                    $parent = Split-Path -Parent $destination
                    if (-not (Test-Path -LiteralPath $parent)) {
                        New-Item -ItemType Directory -Path $parent -Force | Out-Null
                    }
                    $copied = $false
                    for ($attempt = 1; $attempt -le 3; $attempt++) {
                        try {
                            Copy-Item -LiteralPath $source -Destination $destination -Force
                            $copied = $true
                            break
                        } catch {
                            if ($attempt -eq 3) { throw }
                            Start-Sleep -Milliseconds (250 * $attempt)
                        }
                    }
                    if (-not $copied) {
                        throw "Failed to copy $source to $destination"
                    }
                }
            }

            function Remove-TreeContents([string]$path) {
                foreach ($child in Get-ChildItem -LiteralPath $path -Force) {
                    $attributes = [IO.File]::GetAttributes($child.FullName)
                    if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                            [IO.Directory]::Delete($child.FullName, $false)
                        } else {
                            [IO.File]::Delete($child.FullName)
                        }
                    } elseif (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                        Remove-TreeContents $child.FullName
                        [IO.Directory]::Delete($child.FullName, $false)
                    } else {
                        [IO.File]::Delete($child.FullName)
                    }
                }
            }

            function Remove-TreeItem([string]$path) {
                Assert-NoReparse $TargetDirectory $path
                if (-not (Test-Path -LiteralPath $path)) { return }
                if (Test-Path -LiteralPath $path -PathType Container) {
                    if (Test-ReparsePoint $path) {
                        [IO.Directory]::Delete($path, $false)
                    } else {
                        Remove-TreeContents $path
                        [IO.Directory]::Delete($path, $false)
                    }
                } else {
                    [IO.File]::Delete($path)
                }
            }

            function Restore-Backup {
                if ([string]::IsNullOrWhiteSpace($BackupPath) -or -not (Test-Path -LiteralPath $BackupPath -PathType Container)) {
                    Write-UpdateLog 'No verified backup is available for automatic restore.'
                    return $false
                }

                try {
                    Assert-NoReparseChain $BackupPath
                    Assert-NoReparse $TargetDirectory $TargetDirectory
                    if ($UpdateId -and $CompletionPath -and (Test-Path -LiteralPath $CompletionPath -PathType Leaf)) {
                        Remove-Item -LiteralPath $CompletionPath -Force -ErrorAction SilentlyContinue
                    }
                    Write-UpdateLog 'Restoring the previous version from backup.'
                    Remove-TreeContents $TargetDirectory
                    foreach ($item in Get-ChildItem -LiteralPath $BackupPath -Force) {
                        Copy-TreeSafe $item.FullName (Join-Path $TargetDirectory $item.Name)
                    }
                    $script:targetModified = $false
                    Write-UpdateLog 'Previous version restored from backup.'
                    return $true
                } catch {
                    Write-UpdateLog ("Backup restore failed: " + $_.Exception.Message)
                    return $false
                }
            }

            function Complete-UpdateMarker {
                if ([string]::IsNullOrWhiteSpace($UpdateId) -or [string]::IsNullOrWhiteSpace($CompletionPath)) { return }
                if (-not (Test-Path -LiteralPath $CompletionPath -PathType Leaf)) { return }
                $temporary = $null
                try {
                    $state = Get-Content -LiteralPath $CompletionPath -Raw | ConvertFrom-Json
                    if ([string]$state.UpdateId -ne $UpdateId) {
                        Write-UpdateLog 'Completion marker update id did not match; leaving it pending.'
                        return
                    }
                    $state | Add-Member -NotePropertyName AppliedUtc -NotePropertyValue ((Get-Date).ToUniversalTime().ToString('o')) -Force
                    $temporary = $CompletionPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
                    $state | ConvertTo-Json -Compress | Set-Content -LiteralPath $temporary -Encoding ascii
                    Move-Item -LiteralPath $temporary -Destination $CompletionPath -Force
                    $temporary = $null
                    Write-UpdateLog 'Update completion marker applied.'
                } catch {
                    Write-UpdateLog ("Could not apply the completion marker: " + $_.Exception.Message)
                } finally {
                    if ($temporary -and (Test-Path -LiteralPath $temporary)) {
                        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
                    }
                }
            }

            try {
                Assert-NoReparseChain $TargetDirectory
                if ($Kind -ne 'Rollback') {
                    Assert-NoReparseChain $VerificationPath
                    if (-not (Test-Path -LiteralPath $VerificationPath)) {
                        throw 'The verified update payload is missing.'
                    }

                    $actualHash = (Get-FileHash -LiteralPath $VerificationPath -Algorithm SHA256).Hash.ToLowerInvariant()
                    if ($actualHash -ne $ExpectedSha256.ToLowerInvariant()) {
                        throw 'The update payload hash changed after verification.'
                    }
                }

                function Test-OriginalProcessRunning {
                    $proc = Get-Process -Id $WaitPid -ErrorAction SilentlyContinue
                    if ($null -eq $proc) { return $false }
                    if ($WaitProcessStartTicks -gt 0) {
                        try {
                            $delta = [Math]::Abs($proc.StartTime.ToUniversalTime().Ticks - $WaitProcessStartTicks)
                            if ($delta -gt [TimeSpan]::TicksPerSecond * 5) {
                                return $false
                            }
                        } catch {
                        }
                    }
                    return $true
                }

                $deadline = (Get-Date).AddSeconds(60)
                while ((Test-OriginalProcessRunning) -and ((Get-Date) -lt $deadline)) {
                    Start-Sleep -Milliseconds 300
                }

                if (Test-OriginalProcessRunning) {
                    throw 'CryoTray did not exit before the update deadline.'
                }

                Start-Sleep -Milliseconds 700

                if ($Kind -eq 'Archive') {
                    if ($ManifestPath -and (Test-Path -LiteralPath $ManifestPath)) {
                        $manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
                        if ([string]$manifest.version -ne $ExpectedVersion) {
                            throw 'The update manifest version does not match the release.'
                        }
                        if ($null -eq $manifest.hashes) {
                            throw 'The update manifest has no file hashes.'
                        }

                        $hashes = @{}
                        foreach ($property in @($manifest.hashes.PSObject.Properties)) {
                            $hashes[([string]$property.Name).Replace('\','/')] = ([string]$property.Value).ToLowerInvariant()
                        }

                        $copyEntries = @()
                        $deleteEntries = @()
                        foreach ($entry in @($manifest.files)) {
                            $relative = ([string]$entry).Replace('\','/')
                            if ([string]::IsNullOrWhiteSpace($relative)) { continue }
                            $source = Resolve-ChildPath $PackagePath $relative
                            $destination = Resolve-ChildPath $TargetDirectory $relative

                            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
                                throw "Missing payload file: $relative"
                            }
                            if (-not $hashes.ContainsKey($relative)) {
                                throw "Missing payload hash: $relative"
                            }

                            Assert-NoReparse $PackagePath $source
                            Assert-NoReparse $TargetDirectory $destination
                            $actualHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
                            if ($actualHash -ne $hashes[$relative]) {
                                throw "Payload hash mismatch: $relative"
                            }

                            $copyEntries += [pscustomobject]@{ Source = $source; Destination = $destination }
                        }

                        if ($copyEntries.Count -eq 0) {
                            throw 'The update manifest contains no files.'
                        }
                        $exeDestination = Join-Path $TargetDirectory 'CryoTray.exe'
                        if (-not ($copyEntries | Where-Object { $_.Destination -ieq $exeDestination })) {
                            throw 'The update manifest does not contain CryoTray.exe.'
                        }

                        foreach ($entry in @($manifest.delete)) {
                            $relative = ([string]$entry).Replace('\','/')
                            if ([string]::IsNullOrWhiteSpace($relative)) { continue }
                            $destination = Resolve-ChildPath $TargetDirectory $relative
                            Assert-NoReparse $TargetDirectory $destination
                            $deleteEntries += $destination
                        }

                        foreach ($entry in $copyEntries) {
                            $script:targetModified = $true
                            Copy-TreeSafe $entry.Source $entry.Destination
                        }

                        foreach ($destination in $deleteEntries) {
                            $script:targetModified = $true
                            Remove-TreeItem $destination
                        }

                        Complete-UpdateMarker
                    } else {
                        Get-ChildItem -LiteralPath $PackagePath -Force | ForEach-Object {
                            Assert-NoReparse $PackagePath $_.FullName
                            $destination = Join-Path $TargetDirectory $_.Name
                            Assert-NoReparse $TargetDirectory $destination
                            $script:targetModified = $true
                            Copy-TreeSafe $_.FullName $destination
                        }
                        Complete-UpdateMarker
                    }
                } elseif ($Kind -eq 'Rollback') {
                    Assert-NoReparseChain $PackagePath
                    if ($CompletionPath -and (Test-Path -LiteralPath $CompletionPath -PathType Leaf)) {
                        Remove-Item -LiteralPath $CompletionPath -Force -ErrorAction SilentlyContinue
                    }
                    $script:targetModified = $true
                    Remove-TreeContents $TargetDirectory
                    Get-ChildItem -LiteralPath $PackagePath -Force | ForEach-Object {
                        Copy-TreeSafe $_.FullName (Join-Path $TargetDirectory $_.Name)
                    }
                } elseif ($Kind -eq 'Executable') {
                    Assert-NoReparseChain $PackagePath
                    $script:targetModified = $true
                    Copy-TreeSafe $PackagePath $ExePath
                    Complete-UpdateMarker
                } elseif ($Kind -eq 'Installer') {
                    $script:targetModified = $true
                    $installer = Start-Process -FilePath $PackagePath -ArgumentList @(
                        '/VERYSILENT',
                        '/SUPPRESSMSGBOXES',
                        '/NORESTART',
                        '/CLOSEAPPLICATIONS',
                        '/PRESERVEUPDATE'
                    ) -Wait -PassThru

                    if ($null -eq $installer -or $installer.ExitCode -ne 0) {
                        $exitCode = if ($null -eq $installer) { 'unknown' } else { $installer.ExitCode }
                        throw "Installer failed with exit code $exitCode."
                    }

                    Complete-UpdateMarker
                    Write-UpdateLog 'Installer completed.'
                    Start-Sleep -Milliseconds 700
                    Start-Process -FilePath $ExePath -WorkingDirectory $TargetDirectory
                    Write-UpdateLog 'Updated app restarted after installer.'
                    exit 0
                }

                Start-Process -FilePath $ExePath -WorkingDirectory $TargetDirectory
                Write-UpdateLog 'Update applied and CryoTray restarted.'
            } catch {
                Write-UpdateLog ("Update failed: " + $_.Exception.Message)
                if ($script:targetModified) {
                    if (Restore-Backup) {
                        Write-UpdateLog 'Automatic rollback completed after the update failure.'
                    }
                }
                try {
                    if (Test-Path -LiteralPath $ExePath) {
                        Start-Process -FilePath $ExePath -WorkingDirectory $TargetDirectory
                    }
                } catch {
                }
                exit 1
            }
            """;
    }
}
