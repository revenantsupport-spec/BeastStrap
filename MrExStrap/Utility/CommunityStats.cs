using BeastStrap.Models.APIs.GitHub;

namespace BeastStrap.Utility
{
    /// <summary>
    /// Fetches the community figure shown on the Home dashboard: total installer
    /// downloads, summed from every release on the update host.
    ///
    /// This is a read of PUBLIC data. Nothing about this user is sent anywhere — no
    /// heartbeat, no telemetry — so the number is an honest aggregate rather than a
    /// live "who's online" census (which this fork deliberately has no infrastructure
    /// or consent for). The result is cached in State.json and refreshed at most once
    /// every State.CommunityStatsMaxAge; failures never surface to the user.
    /// </summary>
    public static class CommunityStats
    {
        private const string LOG_IDENT = "CommunityStats";

        public sealed record Stats(long TotalDownloads);

        /// <summary>
        /// Returns cached stats if fresh enough, otherwise fetches fresh ones (bounded by
        /// <paramref name="budget"/>). Never throws — on failure it returns whatever cache
        /// exists, or null when there is none.
        /// </summary>
        public static async Task<Stats?> GetAsync(TimeSpan budget)
        {
            bool stale = App.State.Prop.CommunityStatsFetchedUtc is null
                || DateTime.UtcNow - App.State.Prop.CommunityStatsFetchedUtc > State.CommunityStatsMaxAge;

            if (!stale)
            {
                return new Stats(App.State.Prop.CommunityTotalDownloads);
            }

            using var cts = new CancellationTokenSource(budget);
            try
            {
                long downloads = await FetchTotalDownloadsAsync(cts.Token);
                var stats = new Stats(downloads);

                // SaveMerged: this can race the tray timer / another window; merged write
                // re-reads disk so we never clobber a concurrent state change.
                App.State.SaveMerged(s =>
                {
                    s.CommunityTotalDownloads = stats.TotalDownloads;
                    s.CommunityStatsFetchedUtc = DateTime.UtcNow;
                });

                return stats;
            }
            catch (OperationCanceledException)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Refresh exceeded {budget.TotalSeconds:F1}s budget — using cache.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }

            // Stale-but-present cache beats nothing.
            if (App.State.Prop.CommunityStatsFetchedUtc is not null)
            {
                return new Stats(App.State.Prop.CommunityTotalDownloads);
            }

            return null;
        }

        // Sums download_count over every asset of every release. per_page=100 covers ~100
        // releases; this fork had well under that historically. Paginating further isn't
        // worth the complexity for a dashboard number.
        private static async Task<long> FetchTotalDownloadsAsync(CancellationToken token)
        {
            var releases = await Http.GetJson<List<GithubRelease>>(
                $"{App.ProjectApiBase}/repos/{App.ProjectRepository}/releases?per_page=100", token);

            long total = 0;
            foreach (var release in releases ?? new List<GithubRelease>())
            {
                if (release.Assets is null)
                    continue;

                foreach (var asset in release.Assets)
                    total += asset.DownloadCount;
            }

            return total;
        }
    }
}