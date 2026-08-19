using System.Collections.Concurrent;

namespace BeastStrap.Models.Entities
{
    /// <summary>
    /// Explicit loading. Load from cache before and after a fetch.
    /// </summary>
    /// <remarks>
    /// The cache is a ConcurrentDictionary rather than a List because three threads reach it:
    /// the ActivityWatcher log-reader thread (via ActivityData), thread-pool threads (Discord
    /// presence), and the UI thread (the tray's game-history window, which refetches on every
    /// game leave). A plain List enumerated on one while another appended threw
    /// "Collection was modified" out of the log reader and killed the watcher process.
    ///
    /// Keying by universe id also fixes the other half of it: nothing used to check the cache
    /// before adding, so rejoining the same game appended a duplicate entry and re-issued both
    /// HTTP calls every single time.
    /// </remarks>
    public class UniverseDetails
    {
        private static readonly ConcurrentDictionary<long, UniverseDetails> _cache = new();

        public GameDetailResponse Data { get; set; } = null!;

        /// <summary>
        /// Returns data for a 128x128 icon
        /// </summary>
        public ThumbnailResponse Thumbnail { get; set; } = null!;

        public static UniverseDetails? LoadFromCache(long id) =>
            _cache.TryGetValue(id, out var details) ? details : null;

        public static Task FetchSingle(long id) => FetchBulk(id.ToString());

        public static async Task FetchBulk(string ids)
        {
            // Drop anything we already hold. Callers pass the full set every time, so without
            // this a long session re-requests details it fetched minutes ago on every join.
            var wanted = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(x => long.TryParse(x.Trim(), out long id) ? id : (long?)null)
                            .Where(id => id is not null && !_cache.ContainsKey(id.Value))
                            .Select(id => id!.Value)
                            .Distinct()
                            .ToArray();

            if (wanted.Length == 0)
                return;

            string query = string.Join(',', wanted);

            var gameDetailResponse = await Http.GetJson<ApiArrayResponse<GameDetailResponse>>($"https://games.roblox.com/v1/games?universeIds={query}");

            if (gameDetailResponse is null || !gameDetailResponse.Data.Any())
                return;

            var universeThumbnailResponse = await Http.GetJson<ApiArrayResponse<ThumbnailResponse>>($"https://thumbnails.roblox.com/v1/games/icons?universeIds={query}&returnPolicy=PlaceHolder&size=128x128&format=Png&isCircular=false");

            if (universeThumbnailResponse is null || !universeThumbnailResponse.Data.Any())
                throw new InvalidHTTPResponseException("Roblox API for Game Thumbnails returned invalid data");

            foreach (long id in wanted)
            {
                // FirstOrDefault: Roblox does not promise an entry for every id we asked about,
                // and First() threw on a thread-pool thread when one came back short.
                var data = gameDetailResponse.Data.FirstOrDefault(x => x.Id == id);
                var thumbnail = universeThumbnailResponse.Data.FirstOrDefault(x => x.TargetId == id);

                if (data is null || thumbnail is null)
                    continue;

                _cache.TryAdd(id, new UniverseDetails { Data = data, Thumbnail = thumbnail });
            }
        }
    }
}
