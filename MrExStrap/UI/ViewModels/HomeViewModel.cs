using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

using BeastStrap.Models.Persistable;
using BeastStrap.Utility;

namespace BeastStrap.UI.ViewModels
{
    // Backs the Home dashboard (UI\Elements\Settings\Pages\HomePage). Snapshot taken
    // when the settings window opens — the launch action itself reuses MainWindowViewModel's
    // SaveAndLaunchCommand via a RelativeSource binding in the page XAML. The executor
    // match line is resolved asynchronously against WEAO so the dashboard reflects the
    // latest supported build without ever blocking page load.
    public class HomeViewModel : NotifyPropertyChangedViewModel
    {
        private static readonly Brush MatchOkBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xA8, 0x4A));
        private static readonly Brush MatchWarnBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA3, 0x22));
        private static readonly Brush MatchMutedBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));

        private string _executorMatchText = "Checking latest…";
        private Brush _executorMatchBrush = MatchMutedBrush;

        private string _communityLine = "Counting the community…";

        public string Version => $"v{App.Version}";

        public string ChannelStatus => "LIVE · locked";

        public string ActiveProfileName { get; }

        public string? ExecutorTitle { get; }

        public bool HasExecutor => !string.IsNullOrEmpty(ExecutorTitle);

        public string RobloxVersion { get; }

        public string ExecutorMatchText
        {
            get => _executorMatchText;
            private set
            {
                _executorMatchText = value;
                OnPropertyChanged(nameof(ExecutorMatchText));
            }
        }

        public Brush ExecutorMatchBrush
        {
            get => _executorMatchBrush;
            private set
            {
                _executorMatchBrush = value;
                OnPropertyChanged(nameof(ExecutorMatchBrush));
            }
        }

        // e.g. "18,432 installs" — formatted with separators, refreshed in the background.
        // Community figures come from public data only (see Utility.CommunityStats).
        public string CommunityLine
        {
            get => _communityLine;
            private set { _communityLine = value; OnPropertyChanged(nameof(CommunityLine)); }
        }

        public HomeViewModel()
        {
            string activeId = App.Settings.Prop.ActiveVersionProfileId ?? "";
            VersionProfile? profile = App.Settings.Prop.VersionProfiles.FirstOrDefault(x => x.Id == activeId);

            ActiveProfileName = string.IsNullOrWhiteSpace(profile?.Name) ? "Latest LIVE" : profile!.Name;
            ExecutorTitle = App.GetActiveExecutorTitle();

            if (profile is not null && !string.IsNullOrWhiteSpace(profile.VersionGuid))
            {
                RobloxVersion = profile.VersionGuid;
            }
            else if (App.IsPlayerInstalled)
            {
                RobloxVersion = App.PlayerState.Prop.VersionGuid;
            }
            else
            {
                RobloxVersion = "LIVE";
            }

            _ = RefreshExecutorMatchAsync();
            _ = LoadCommunityStatsAsync();
        }

        private async Task RefreshExecutorMatchAsync()
        {
            try
            {
                if (!HasExecutor || string.IsNullOrEmpty(RobloxVersion) || RobloxVersion == "LIVE")
                {
                    ExecutorMatchText = "";
                    return;
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var result = await WeaoClient.GetWindowsExploitsAsync(cts.Token);
                if (!result.Success || result.Exploits.Count == 0)
                {
                    ExecutorMatchText = "Offline — could not check";
                    return;
                }

                var match = result.Exploits.FirstOrDefault(e =>
                    string.Equals(e.Title, ExecutorTitle, StringComparison.OrdinalIgnoreCase));
                if (match is null || string.IsNullOrEmpty(match.RbxVersion))
                {
                    ExecutorMatchText = "Executor not found on tracker";
                    return;
                }

                if (string.Equals(RobloxVersion, match.RbxVersion, StringComparison.OrdinalIgnoreCase))
                {
                    ExecutorMatchText = $"Matches {ExecutorTitle} {match.Version}";
                    ExecutorMatchBrush = MatchOkBrush;
                }
                else
                {
                    ExecutorMatchText = $"{ExecutorTitle} now supports {ShortenHash(match.RbxVersion)}";
                    ExecutorMatchBrush = MatchWarnBrush;
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("HomeViewModel::RefreshExecutorMatchAsync", ex);
                ExecutorMatchText = "Could not check";
            }
        }

        private async Task LoadCommunityStatsAsync()
        {
            var stats = await BeastStrap.Utility.CommunityStats.GetAsync(TimeSpan.FromSeconds(6));

            if (stats is null)
            {
                CommunityLine = "";
                return;
            }

            CommunityLine = stats.TotalDownloads > 0
                ? $"{stats.TotalDownloads:N0} installs"
                : "";
        }

        private static string ShortenHash(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return hash;

            string prefix = hash.StartsWith("version-", StringComparison.OrdinalIgnoreCase) ? "version-" : "";
            string core = prefix.Length > 0 ? hash.Substring(prefix.Length) : hash;
            return prefix + (core.Length <= 12 ? core : core.Substring(0, 12) + "…");
        }
    }
}
