using System;
using CryoTray.Models;

namespace CryoTray.Services
{
    public enum UpdateCheckOutcome
    {
        None,
        UpToDate,
        Available,
        NoReleases,
        Failed
    }

    public sealed class UpdateStatusService
    {
        private readonly object _gate = new();
        private bool _persist;

        public static UpdateStatusService Instance { get; } = new();

        public DateTime? LastCheckedUtc { get; private set; }
        public UpdateCheckOutcome Outcome { get; private set; } = UpdateCheckOutcome.None;
        public string Version { get; private set; } = string.Empty;
        public string Detail { get; private set; } = string.Empty;

        public event Action? Changed;

        public void Restore(AppSettings settings)
        {
            lock (_gate)
            {
                LastCheckedUtc = settings.LastUpdateCheckUtc;
                Outcome = Enum.TryParse(
                    settings.LastUpdateOutcome, ignoreCase: true, out UpdateCheckOutcome outcome)
                    ? outcome
                    : UpdateCheckOutcome.None;
                Version = settings.LastUpdateVersion ?? string.Empty;
                Detail = settings.LastUpdateDetail ?? string.Empty;
                _persist = true;
            }
        }

        public void Record(UpdateCheckOutcome outcome, string version, string? detail = null)
        {
            lock (_gate)
            {
                LastCheckedUtc = DateTime.UtcNow;
                Outcome = outcome;
                Version = version ?? string.Empty;
                Detail = detail ?? string.Empty;
            }

            Persist();
            Changed?.Invoke();
        }

        private void Persist()
        {
            if (!_persist)
            {
                return;
            }

            try
            {
                var settings = SettingsService.Instance.Current;
                settings.LastUpdateCheckUtc = LastCheckedUtc;
                settings.LastUpdateOutcome = Outcome.ToString();
                settings.LastUpdateVersion = Version;
                settings.LastUpdateDetail = Detail;
                SettingsService.Instance.Save();
            }
            catch
            {
            }
        }
    }
}
