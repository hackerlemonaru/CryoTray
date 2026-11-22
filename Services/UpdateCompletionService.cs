using System;
using System.IO;
using System.Text.Json;

namespace CryoTray.Services
{
    public sealed class UpdateCompletionInfo
    {
        public string FromVersion { get; set; } = string.Empty;
        public string ToVersion { get; set; } = string.Empty;
        public string UpdateId { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public DateTime? AppliedUtc { get; set; }
    }

    public static class UpdateCompletionService
    {
        public static string? RecordPending(string fromVersion, string toVersion)
        {
            if (string.IsNullOrWhiteSpace(fromVersion) ||
                string.IsNullOrWhiteSpace(toVersion) ||
                string.Equals(fromVersion, toVersion, StringComparison.Ordinal))
            {
                return null;
            }

            UpdateStorage.EnsureUpdateRoot();
            var info = new UpdateCompletionInfo
            {
                FromVersion = fromVersion,
                ToVersion = toVersion,
                UpdateId = Guid.NewGuid().ToString("N"),
                CreatedUtc = DateTime.UtcNow
            };

            WriteState(info);
            return info.UpdateId;
        }

        public static UpdateCompletionInfo? ReadPending(string currentVersion)
        {
            try
            {
                UpdateStorage.EnsureUpdateRoot();
                string path = UpdateStorage.CompletionStatePath;
                if (!File.Exists(path))
                {
                    return null;
                }

                var info = JsonSerializer.Deserialize<UpdateCompletionInfo>(
                    File.ReadAllText(path));
                if (info == null ||
                    string.IsNullOrWhiteSpace(info.FromVersion) ||
                    string.IsNullOrWhiteSpace(info.ToVersion) ||
                    string.IsNullOrWhiteSpace(info.UpdateId) ||
                    string.Equals(info.FromVersion, info.ToVersion, StringComparison.Ordinal) ||
                    info.CreatedUtc == default)
                {
                    Clear();
                    return null;
                }

                if (!info.AppliedUtc.HasValue)
                {
                    if (info.CreatedUtc < DateTime.UtcNow.AddDays(-2))
                    {
                        Clear();
                    }

                    return null;
                }

                if (!string.Equals(info.ToVersion, currentVersion, StringComparison.Ordinal) ||
                    info.AppliedUtc.Value < info.CreatedUtc ||
                    info.CreatedUtc < DateTime.UtcNow.AddDays(-2))
                {
                    Clear();
                    return null;
                }

                return info;
            }
            catch
            {
                Clear();
                return null;
            }
        }

        public static bool MarkApplied(string updateId)
        {
            if (string.IsNullOrWhiteSpace(updateId))
            {
                return false;
            }

            try
            {
                string path = UpdateStorage.CompletionStatePath;
                if (!File.Exists(path))
                {
                    return false;
                }

                var info = JsonSerializer.Deserialize<UpdateCompletionInfo>(
                    File.ReadAllText(path));
                if (info == null ||
                    !string.Equals(info.UpdateId, updateId, StringComparison.Ordinal))
                {
                    return false;
                }

                info.AppliedUtc = DateTime.UtcNow;
                WriteState(info);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void Clear()
        {
            try
            {
                string path = UpdateStorage.CompletionStatePath;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void WriteState(UpdateCompletionInfo info)
        {
            string path = UpdateStorage.CompletionStatePath;
            string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tempPath, JsonSerializer.Serialize(info));
                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                }

                throw;
            }
        }
    }
}
