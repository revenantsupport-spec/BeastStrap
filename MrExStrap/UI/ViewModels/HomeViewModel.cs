using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

using CommunityToolkit.Mvvm.Input;

using BeastStrap.Models.Persistable;
using BeastStrap.UI;
using BeastStrap.Utility;

namespace BeastStrap.UI.ViewModels
{
    // Backs the Home dashboard (UI\Elements\Settings\Pages\HomePage). Snapshot taken
    // when the settings window opens - the launch action itself reuses MainWindowViewModel's
    // SaveAndLaunchCommand via a RelativeSource binding in the page XAML. The executor
    // match line is resolved asynchronously against WEAO so the dashboard reflects the
    // latest supported build without ever blocking page load.
    public class HomeViewModel : NotifyPropertyChangedViewModel
    {
        private static readonly Brush MatchOkBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xA8, 0x4A));
        private static readonly Brush MatchWarnBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA3, 0x22));
        private static readonly Brush MatchMutedBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));

        private string _executorMatchText = "Checking latest.";
        private Brush _executorMatchBrush = MatchMutedBrush;

        private string _communityLine = "Counting the community.";

        public string Version => $"v{App.Version}";

        public string ChannelStatus => "LIVE – locked";

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

        // e.g. "18,432 installs" - formatted with separators, refreshed in the background.
        // Community figures come from public data only (see Utility.CommunityStats).
        public string CommunityLine
        {
            get => _communityLine;
            private set { _communityLine = value; OnPropertyChanged(nameof(CommunityLine)); }
        }

        // ── Unexplained crashes ───────────────────────────────────────────────
        //
        // The crash dialog stays silent when it cannot name a cause, which was the right call (it
        // used to invent one) but it removed the only prompt that ever asked anyone to send logs.
        // This is the quiet replacement: mention it here, where the user came of their own accord,
        // instead of interrupting them at the moment their game closed.
        //
        // Local only. Nothing is uploaded, and nothing here changes that.

        // Silent, not Unexplained: a crash that DID show a dialog already gave the user their chance
        // to export, so nagging about it again is noise.
        private int PendingCrashCount
        {
            get
            {
                var since = App.CrashJournal.Prop.CrashNoticeAcknowledgedUtc;

                return App.CrashJournal.Prop.RecentCrashes
                    .Where(c => c.Silent)
                    .Count(c => since is null || c.TimeUtc > since);
            }
        }

        // BeastStrap's own background failures. Counted separately so the notice never calls one
        // of our bugs a Roblox crash.
        private int PendingFailureCount
        {
            get
            {
                var since = App.CrashJournal.Prop.CrashNoticeAcknowledgedUtc;

                return App.CrashJournal.Prop.RecentBackgroundFailures
                    .Count(c => since is null || c.TimeUtc > since);
            }
        }

        public int UnexplainedCrashCount => PendingCrashCount + PendingFailureCount;

        public bool HasUnexplainedCrashes => UnexplainedCrashCount > 0;

        public Visibility CrashNoticeVisibility => HasUnexplainedCrashes ? Visibility.Visible : Visibility.Collapsed;

        public string CrashNoticeText
        {
            get
            {
                int crashes = PendingCrashCount;
                int failures = PendingFailureCount;
                int count = crashes + failures;

                // Say which kind it actually was. Calling our own background exception a "Roblox
                // crash" is exactly the sort of confident wrong claim the crash dialog was reformed
                // to stop making.
                string what =
                    failures == 0
                        ? count == 1
                            ? "BeastStrap noticed a Roblox crash it could not explain."
                            : $"BeastStrap noticed {count} Roblox crashes it could not explain."
                        : crashes == 0
                            ? count == 1
                                ? "BeastStrap ran into a problem of its own in the background."
                                : $"BeastStrap ran into {count} problems of its own in the background."
                            : $"BeastStrap noticed {crashes} unexplained Roblox crash{(crashes == 1 ? "" : "es")} and {failures} problem{(failures == 1 ? "" : "s")} of its own.";

                return what + " Your logs are saved and nothing has been sent anywhere. "
                    + "If you want us to take a look, export them and send them over.";
            }
        }

        /// <summary>Exports the bundle, then stops the notice reappearing for these crashes.</summary>
        public ICommand ExportCrashLogsCommand => new AsyncRelayCommand(async () =>
        {
            await SupportActions.ExportLogsAsync();
            Acknowledge();
        });

        public ICommand DismissCrashNoticeCommand => new RelayCommand(Acknowledge);

        private void Acknowledge()
        {
            // Timestamp rather than clearing the list: the history stays intact for a later export,
            // it just stops counting towards the banner.
            //
            // SaveMerged is safe here even though we're the settings UI, because nothing in the
            // settings UI ever whole-writes CrashJournal - so there are no pending in-memory edits
            // for the re-read to discard. That would NOT be true of State.
            App.CrashJournal.SaveMerged(journal => journal.CrashNoticeAcknowledgedUtc = DateTime.UtcNow);

            OnPropertyChanged(nameof(UnexplainedCrashCount));
            OnPropertyChanged(nameof(HasUnexplainedCrashes));
            OnPropertyChanged(nameof(CrashNoticeVisibility));
            OnPropertyChanged(nameof(CrashNoticeText));
        }

        // ── "New: themes" ─────────────────────────────────────────────────────
        //
        // Points people at the Appearance tab, where the creator themes and the seasonal switches live. It shows until
        // it's dismissed or Appearance has been opened, and never to someone who has already saved a creator theme.
        // During a seasonal run it also says why the app has dressed up.

        public Visibility ThemesNoticeVisibility =>
            App.State.Prop.ThemesNoticeSeen || CreatorTheme.Find(App.Settings.Prop.ActiveCreatorTheme) is not null
                ? Visibility.Collapsed : Visibility.Visible;

        public string ThemesNoticeText => BeastStrap.Utility.ThemeManager.ActiveSeasonal is { } seasonal
            ? $"The {seasonal.Name} theme is on right now. Pick your own look, or turn it off, in Appearance."
            : "Turn the whole app into a YouTuber's or a script site's edition, or let it dress up for 4/20, Halloween and Christmas. Pick yours in Appearance.";

        public ICommand DismissThemesNoticeCommand => new RelayCommand(() =>
        {
            MarkThemesNoticeSeen();
            RefreshThemesNotice();
        });

        // Also called when the Appearance page opens: once someone has been there, the callout has done its job.
        // A whole-file Save, not SaveMerged, because this is the settings UI (see Acknowledge above).
        public static void MarkThemesNoticeSeen()
        {
            if (App.State.Prop.ThemesNoticeSeen)
                return;

            App.State.Prop.ThemesNoticeSeen = true;
            App.State.Save();
        }

        public void RefreshThemesNotice()
        {
            OnPropertyChanged(nameof(ThemesNoticeVisibility));
            OnPropertyChanged(nameof(ThemesNoticeText));
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
                    ExecutorMatchText = "Offline - could not check";
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
            return prefix + (core.Length <= 12 ? core : core.Substring(0, 12) + ".");
        }
    }
}