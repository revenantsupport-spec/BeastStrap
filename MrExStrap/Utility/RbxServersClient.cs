using System.Text.RegularExpressions;

namespace BeastStrap.Utility
{
    // Small read-only client for rbxservers.xyz, the shared-VIP-server index behind the VIP picker.
    //
    // There is NO public JSON API — /api/servers, /api/games, /api/stats and friends are all 404
    // (checked). The site is a Next.js App Router app, and its React payload carries the i18n
    // dictionary rather than the server list, so the practical way to read it is the rendered HTML.
    //
    // ⚠️ Do NOT sniff the HTML for error text. Every page ships the full translation dictionary in
    // its bundle, which contains "400 Bad Request" and "not valid or has never been visited" as
    // dictionary VALUES — grepping the raw HTML for those reports a failure on pages that rendered
    // perfectly. Count the actual server links instead, which is what ServerCountForPlaceAsync does.
    //
    // No UA gating (verified: our default UA, a browser UA and curl all get 200), so unlike
    // RobloxNewsClient nothing needs spoofing here.
    public static class RbxServersClient
    {
        private const string LOG_IDENT = "RbxServersClient";

        private const string BaseUrl = "https://rbxservers.xyz";

        // The index moves slowly and this is decorative, so be generous and stay off their site.
        public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

        private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(10);

        /// <summary>Headline figures from the site's front page.</summary>
        public readonly record struct LiveIndex(int Servers, int Games);

        private sealed class Cached
        {
            public readonly LiveIndex Value;
            public readonly DateTime At;
            public Cached(LiveIndex value, DateTime at) { Value = value; At = at; }
        }

        private static volatile Cached? _indexCache;

        /// <summary>
        /// "9,526 VIP servers you can join right now" — the counters rbxservers puts on its home page.
        /// </summary>
        /// <returns>Null when it can't be read and nothing is cached. Serves stale on failure.</returns>
        public static async Task<LiveIndex?> GetLiveIndexAsync(bool forceRefresh = false, CancellationToken token = default)
        {
            var cached = _indexCache;
            if (!forceRefresh && cached is not null && DateTime.Now - cached.At < CacheTtl)
                return cached.Value;

            string? html = await GetStringAsync(BaseUrl, token);

            if (html is null)
                return cached?.Value;   // stale beats nothing

            // <span class="hero-count">9,526</span><span class="hero-lead">VIP servers you can join right now</span>
            int servers = ParseCount(html, @"class=""hero-count""[^>]*>([\d,]+)<");

            // Fall back to position rather than class name, in case the styling is renamed.
            if (servers <= 0)
                servers = ParseCount(html, @">([\d,]+)</span><span class=""hero-lead""");

            int games = ParseCount(html, @"([\d,]+)\s*games");

            if (servers <= 0)
            {
                App.Logger.WriteLine(LOG_IDENT, "Couldn't find the server counter on the front page — layout probably changed.");
                return cached?.Value;
            }

            var index = new LiveIndex(servers, games);
            _indexCache = new Cached(index, DateTime.Now);

            App.Logger.WriteLine(LOG_IDENT, $"Live index: {servers:N0} servers across {games:N0} games.");
            return index;
        }

        /// <summary>
        /// How many shared servers the index actually lists for a place.
        /// </summary>
        /// <returns>
        /// The count, or -1 if the page couldn't be fetched. ZERO is a real and common answer — most
        /// games are not in the index at all — and it's what lets the picker say so plainly instead of
        /// embedding rbxservers' own "400 Bad Request" page, which is what users were seeing.
        /// </returns>
        public static async Task<int> ServerCountForPlaceAsync(long placeId, CancellationToken token = default)
        {
            if (placeId <= 0)
                return 0;

            string? html = await GetStringAsync($"{BaseUrl}/embedded/game/{placeId}", token);

            if (html is null)
                return -1;

            // Each listed server is a /embedded/quicklaunch/<guid> link, the same shape
            // LaunchArgsUtility matches when the user clicks one. Distinct: the page emits two links
            // per server (thumbnail and title).
            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in Regex.Matches(html, @"/embedded/quicklaunch/([0-9a-fA-F\-]{36})"))
                guids.Add(match.Groups[1].Value);

            return guids.Count;
        }

        private static int ParseCount(string html, string pattern)
        {
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);

            if (!match.Success)
                return 0;

            return int.TryParse(match.Groups[1].Value.Replace(",", ""), out int value) ? value : 0;
        }

        private static async Task<string?> GetStringAsync(string url, CancellationToken token)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(RequestBudget);

            try
            {
                using var response = await App.HttpClient.GetAsync(url, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"GET {url} -> {(int)response.StatusCode}");
                    return null;
                }

                return await response.Content.ReadAsStringAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Get", ex);
                return null;
            }
        }
    }
}
