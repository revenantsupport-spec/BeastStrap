using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace BeastStrap.Utility
{
    // Backs the Scripts page, reading the public catalogue at https://robloxscripts.com/api/v1.
    // No auth, no API key, CORS open. ~8,000 scripts at time of writing.
    //
    // Notes that shaped this client:
    //   • SEARCH IS SERVER-SIDE (q / tag / game / placeId / access / author / sort / page / limit).
    //     Don't pull the catalogue down and filter locally — a full crawl is ~161 requests and
    //     ~15 MB, and `page` caps at 200 so it can't even fetch everything past 10k scripts.
    //   • RATE LIMITS are real and advertised: 60/min on /api/v1, 120/min on /raw, then 429 with a
    //     RateLimit-Reset header. That's why the UI debounces rather than searching per keystroke.
    //   • CLOUDFLARE IS UA-GATED. A bare curl UA gets 403; App.HttpClient's default
    //     "BeastStrap/<version>" passes, so unlike RobloxNewsClient we do NOT need to spoof a
    //     browser here.
    //   • There is NO single-script-by-slug JSON endpoint — /api/v1/scripts/:slug is a 404. To
    //     resolve one script, search q=<slug> and match client-side, or just fetch /raw/<slug>.
    //
    // Results are cached in-process for CacheTtl keyed by the full query, so paging back and forth
    // or reopening the tab doesn't re-hit the API. Refresh passes forceRefresh to bust it.
    public static class RobloxScriptsClient
    {
        private const string LOG_IDENT = "RobloxScriptsClient";

        private const string ApiBase = "https://robloxscripts.com/api/v1";

        public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

        // The API clamps limit to 50 regardless of what we ask for.
        public const int PageSize = 24;

        // Per-search deadline. The caller's token still cancels everything, but a stalled host must
        // not leave the tab spinning forever. Same idiom as WeaoClient.
        private static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(10);

        /// <summary>A search result, or a human-readable reason it didn't work.</summary>
        /// <remarks>
        /// Never throws at the boundary — the page shows <see cref="Error"/> verbatim, so the text
        /// has to be something a user can act on.
        /// </remarks>
        public readonly record struct ScriptSearchResult(
            IReadOnlyList<Models.APIs.RobloxScript> Scripts,
            int Total,
            bool HasMore,
            string? MatchedGameName,
            string? Error)
        {
            public bool Success => Error is null;

            public static ScriptSearchResult Failed(string error) =>
                new(Array.Empty<Models.APIs.RobloxScript>(), 0, false, null, error);
        }

        private sealed class Cached
        {
            public readonly ScriptSearchResult Value;
            public readonly DateTime At;
            public Cached(ScriptSearchResult value, DateTime at) { Value = value; At = at; }
        }

        private static readonly ConcurrentDictionary<string, Cached> _searchCache = new();

        // The cache is keyed by the full query URL, and the query includes whatever the user typed —
        // every debounce boundary mid-word is its own key. Without a cap that grows for the life of
        // the settings process, holding a page of results per entry.
        private const int MaxCachedSearches = 32;

        /// <summary>
        /// Searches the catalogue. Every argument is optional — with none of them set this returns
        /// the newest scripts.
        /// </summary>
        /// <param name="placeId">
        /// Exact place-id match. When it resolves, <see cref="ScriptSearchResult.MatchedGameName"/>
        /// carries the game's name; when it doesn't, that stays null and the caller should fall
        /// back to a plain search rather than showing an empty list.
        /// </param>
        public static async Task<ScriptSearchResult> SearchAsync(
            string? query = null,
            long placeId = 0,
            string? access = null,
            string? sort = null,
            bool verifiedOnly = false,
            int page = 1,
            bool forceRefresh = false,
            CancellationToken token = default)
        {
            var parts = new List<string> { $"limit={PageSize}", $"page={Math.Clamp(page, 1, 200)}" };

            if (!string.IsNullOrWhiteSpace(query))
                parts.Add("q=" + Uri.EscapeDataString(query.Trim()));

            if (placeId > 0)
                parts.Add("placeId=" + placeId);

            if (!string.IsNullOrWhiteSpace(access))
                parts.Add("access=" + Uri.EscapeDataString(access));

            if (verifiedOnly)
                parts.Add("verified=true");

            if (!string.IsNullOrWhiteSpace(sort))
                parts.Add("sort=" + Uri.EscapeDataString(sort));

            string url = $"{ApiBase}/scripts?" + string.Join("&", parts);

            if (_searchCache.TryGetValue(url, out var cached))
            {
                if (!forceRefresh && DateTime.Now - cached.At < CacheTtl)
                    return cached.Value;

                // Expired or explicitly refreshed — drop it rather than leaving it to rot.
                _searchCache.TryRemove(url, out _);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(SearchBudget);

            try
            {
                using var response = await App.HttpClient.GetAsync(url, cts.Token);

                if (!response.IsSuccessStatusCode)
                    return ScriptSearchResult.Failed(DescribeStatus((int)response.StatusCode));

                await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                var payload = await JsonSerializer.DeserializeAsync<Models.APIs.RobloxScriptsResponse>(
                    stream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    cts.Token);

                var scripts = payload?.Data ?? new List<Models.APIs.RobloxScript>();

                // meta.matched is omitted entirely on a placeId miss, so treat "not true" as no match.
                string? matchedGame = payload?.Meta?.Matched == true ? payload.Meta.GameName : null;

                var result = new ScriptSearchResult(
                    scripts,
                    payload?.Pagination?.Total ?? scripts.Count,
                    payload?.Pagination?.HasMore ?? false,
                    matchedGame,
                    null);

                TrimCache();
                _searchCache[url] = new Cached(result, DateTime.Now);
                return result;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Our own deadline, not the caller's.
                App.Logger.WriteLine(LOG_IDENT, $"Search timed out after {SearchBudget.TotalSeconds:F0}s: {url}");
                return ScriptSearchResult.Failed($"The search timed out after {SearchBudget.TotalSeconds:F0} seconds. Check your connection and try again.");
            }
            catch (OperationCanceledException)
            {
                // Caller gave up (user typed again). Not an error, and not worth a stack trace.
                return ScriptSearchResult.Failed("");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Search", ex);
                return ScriptSearchResult.Failed("Couldn't reach robloxscripts.com. Check your connection and try again.");
            }
        }

        /// <summary>Resolves a single script from its slug, preferring its stored title as the query.</summary>
        /// <remarks>
        /// There is no by-slug JSON endpoint — /api/v1/scripts/&lt;slug&gt; is a verified 404.
        /// ⚠️ And <c>q</c> does NOT search the slug field, only title/description/tags, so querying
        /// the slug itself fails for anything longer than a token or two — verified live, three
        /// sampled slugs all returned <c>total: 0</c>. Searching the TITLE resolves it every time,
        /// which is why favourites persist the title. The slug is kept as a fallback because it does
        /// work for short single-word slugs, and because a favourite stored by an older build may
        /// have no title.
        /// </remarks>
        public static async Task<Models.APIs.RobloxScript?> GetBySlugAsync(
            string slug,
            string? title = null,
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(slug))
                return null;

            var queries = new List<string>();
            if (!string.IsNullOrWhiteSpace(title))
                queries.Add(title!.Trim());
            if (!queries.Any(q => string.Equals(q, slug, StringComparison.OrdinalIgnoreCase)))
                queries.Add(slug);

            foreach (string query in queries)
            {
                var result = await SearchAsync(query: query, token: token);

                // A failed request is worth giving up on; an empty one just means this query missed.
                if (!result.Success)
                    return null;

                var hit = result.Scripts.FirstOrDefault(
                    s => string.Equals(s.Slug, slug, StringComparison.OrdinalIgnoreCase));

                if (hit is not null)
                    return hit;
            }

            return null;
        }

        /// <summary>Fetches a script's body. May well be a one-line loader — see RobloxScript.RawScriptUrl.</summary>
        public static async Task<string?> GetRawScriptAsync(string rawUrl, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
                return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(SearchBudget);

            try
            {
                using var response = await App.HttpClient.GetAsync(rawUrl, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"GET {rawUrl} -> {(int)response.StatusCode}");
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
                App.Logger.WriteException(LOG_IDENT + "::GetRaw", ex);
                return null;
            }
        }

        // Drop the oldest entries once the cache is full. Best effort — a concurrent writer racing
        // us just means the cap is briefly exceeded, which is harmless.
        private static void TrimCache()
        {
            if (_searchCache.Count < MaxCachedSearches)
                return;

            foreach (var stale in _searchCache.OrderBy(e => e.Value.At).Take(_searchCache.Count - MaxCachedSearches + 1))
                _searchCache.TryRemove(stale.Key, out _);
        }

        // Same shape as WeaoClient's mapping: say what happened AND what to do about it.
        private static string DescribeStatus(int code) => code switch
        {
            429 => "robloxscripts.com is rate limiting us (60 searches a minute). Wait a moment and try again.",
            503 => "robloxscripts.com has the API switched off right now. Try again shortly.",
            403 => "robloxscripts.com returned 403. Cloudflare or your network may be blocking the request from your IP.",
            >= 500 and < 600 => $"robloxscripts.com returned {code}. The site is having issues — try again shortly.",
            _ => $"robloxscripts.com returned HTTP {code}. Try again in a moment."
        };
    }
}
