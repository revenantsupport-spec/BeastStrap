using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BeastStrap.Utility
{
    // Backs the News page. Every source here is public and needs no Roblox login:
    //   • DevForum announcements — Discourse category JSON (updates/announcements, category 36).
    //   • Release list           — Discourse category JSON (updates/release-notes, category 62); the
    //                              topic titles read "Release Notes for {N}", which gives us the picker.
    //   • Per-release notes       — create.roblox.com embeds the notes as structured JSON inside its
    //                              __NEXT_DATA__ blob: objects of the shape
    //                              { "ReleaseNotesText": "...", "ReleaseNotesType": "Improvements|Fixes",
    //                                "Status": "Pending|Live" }. We pull those out directly rather than
    //                              scraping rendered HTML, so the Imp/Fix split and Live/Pending state are
    //                              real Roblox data, not guessed.
    //
    // Results are cached in-process for CacheTtl so flipping tabs / pages doesn't refetch. Refresh buttons
    // pass forceRefresh to bust it.
    public static class RobloxNewsClient
    {
        private const string LOG_IDENT = "RobloxNewsClient";

        public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

        // create.roblox.com sits behind Cloudflare and can challenge app-like / bot User-Agents. We present
        // a normal browser string on every news request so the fetch isn't blocked. App.HttpClient's own
        // default UA is left untouched for the rest of the app — a per-request UA overrides the default only
        // for that request.
        private const string BrowserUa =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private const int AnnouncementsCategoryId = 36; // devforum: updates/announcements
        private const int ReleaseNotesCategoryId = 62;  // devforum: updates/release-notes

        // ---- models -------------------------------------------------------------------

        public class DevForumTopic
        {
            public long Id;
            public string Title = "";
            public string Excerpt = "";
            public string Slug = "";
            public int Replies;
            public int Views;
            public int Likes;
            public bool Pinned;
            public DateTime CreatedAt;

            public string Url => $"https://devforum.roblox.com/t/{Slug}/{Id}";
        }

        public class ReleaseNoteEntry
        {
            public string Text = "";    // may carry `code` spans and \n line breaks
            public string Type = "";    // "Improvements" | "Fixes"
            public string Status = "";  // "Pending" | "Live"
        }

        public class ReleaseNotes
        {
            public int Release;
            public List<ReleaseNoteEntry> Entries = new();
        }

        // ---- caches -------------------------------------------------------------------
        // Refreshes can overlap (manual refresh racing a tab-open fetch on another
        // continuation thread), so every cache is either published atomically as one
        // immutable object or is a ConcurrentDictionary — plain fields + Dictionary
        // here risk torn reads and dictionary corruption.

        private sealed class Cached<T>
        {
            public readonly T Value;
            public readonly DateTime At;
            public Cached(T value, DateTime at) { Value = value; At = at; }
        }

        private static volatile Cached<List<DevForumTopic>>? _announcementsCache;
        private static volatile Cached<List<int>>? _releaseListCache;

        private static readonly ConcurrentDictionary<int, (ReleaseNotes Notes, DateTime At)> _releaseNotesCache = new();

        // When the currently-served announcements were actually fetched (for the "cached N min ago" footer).
        public static DateTime AnnouncementsFetchedAt => _announcementsCache?.At ?? default;

        // ---- announcements ------------------------------------------------------------

        public static async Task<List<DevForumTopic>> GetAnnouncementsAsync(bool forceRefresh = false)
        {
            var cached = _announcementsCache;
            if (!forceRefresh && cached != null && DateTime.Now - cached.At < CacheTtl)
                return cached.Value;

            var list = await FetchCategoryTopicsAsync(AnnouncementsCategoryId, "announcements");
            if (list.Count > 0)
            {
                _announcementsCache = new Cached<List<DevForumTopic>>(list, DateTime.Now);
                return list;
            }

            // Fetch failed (offline / blocked). Serve stale cache if we have it, otherwise the empty list.
            return _announcementsCache?.Value ?? list;
        }

        private static async Task<List<DevForumTopic>> FetchCategoryTopicsAsync(int categoryId, string slug)
        {
            var result = new List<DevForumTopic>();
            try
            {
                string url = $"https://devforum.roblox.com/c/updates/{slug}/{categoryId}.json";
                string? json = await GetStringAsync(url);
                if (json is null)
                    return result;

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("topic_list", out var topicList) ||
                    !topicList.TryGetProperty("topics", out var topics) ||
                    topics.ValueKind != JsonValueKind.Array)
                    return result;

                foreach (var t in topics.EnumerateArray())
                {
                    string title = GetString(t, "title") ?? GetString(t, "fancy_title") ?? "";
                    // Skip Discourse's pinned "About the X category" meta topic — it isn't news.
                    if (title.StartsWith("About the ", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var topic = new DevForumTopic
                    {
                        Id = GetLong(t, "id"),
                        Title = WebUtility.HtmlDecode(title),
                        Excerpt = CleanExcerpt(GetString(t, "excerpt")),
                        Slug = GetString(t, "slug") ?? "",
                        Replies = (int)GetLong(t, "reply_count"),
                        Views = (int)GetLong(t, "views"),
                        Likes = (int)GetLong(t, "like_count"),
                        Pinned = GetBool(t, "pinned"),
                        CreatedAt = GetDate(t, "created_at"),
                    };

                    if (topic.Id != 0 && !string.IsNullOrEmpty(topic.Title))
                        result.Add(topic);
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::FetchCategory", ex);
            }
            return result;
        }

        // ---- release list -------------------------------------------------------------

        public static async Task<List<int>> GetReleaseNumbersAsync(bool forceRefresh = false)
        {
            var cachedList = _releaseListCache;
            if (!forceRefresh && cachedList != null && DateTime.Now - cachedList.At < CacheTtl)
                return cachedList.Value;

            var numbers = new List<int>();
            try
            {
                string url = $"https://devforum.roblox.com/c/updates/release-notes/{ReleaseNotesCategoryId}.json";
                string? json = await GetStringAsync(url);
                if (json is null)
                    return _releaseListCache?.Value ?? numbers;

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("topic_list", out var tl) &&
                    tl.TryGetProperty("topics", out var topics) &&
                    topics.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in topics.EnumerateArray())
                    {
                        string title = GetString(t, "title") ?? "";
                        var m = Regex.Match(title, @"Release Notes for (\d+)");
                        if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && !numbers.Contains(n))
                            numbers.Add(n);
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::ReleaseList", ex);
            }

            numbers.Sort((a, b) => b.CompareTo(a)); // newest first
            if (numbers.Count > 0)
            {
                _releaseListCache = new Cached<List<int>>(numbers, DateTime.Now);
                return numbers;
            }
            return _releaseListCache?.Value ?? numbers;
        }

        // ---- per-release notes --------------------------------------------------------

        public static async Task<ReleaseNotes> GetReleaseNotesAsync(int release, bool forceRefresh = false)
        {
            if (!forceRefresh && _releaseNotesCache.TryGetValue(release, out var cached) &&
                DateTime.Now - cached.At < CacheTtl)
                return cached.Notes;

            var notes = new ReleaseNotes { Release = release };
            try
            {
                string url = $"https://create.roblox.com/docs/en-us/release-notes/release-notes-{release}";
                string? html = await GetStringAsync(url);
                if (html is null)
                    return notes;

                string? nextData = ExtractNextData(html);
                if (nextData != null)
                {
                    var collected = new List<ReleaseNoteEntry>();
                    using (var doc = JsonDocument.Parse(nextData))
                        CollectReleaseEntries(doc.RootElement, collected);

                    // The same entry can appear more than once in the Next.js tree; keep the first of each.
                    var seen = new HashSet<string>();
                    foreach (var e in collected)
                        if (seen.Add(e.Type + "" + e.Status + "" + e.Text))
                            notes.Entries.Add(e);
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::ReleaseNotes", ex);
            }

            if (notes.Entries.Count > 0)
                _releaseNotesCache[release] = (notes, DateTime.Now);
            return notes;
        }

        private static string? ExtractNextData(string html)
        {
            // The JSON never contains a literal "</script>" (Next.js escapes it as "<\/script>"), so a
            // non-greedy match to the first closing tag captures the whole blob safely.
            var m = Regex.Match(html, "<script id=\"__NEXT_DATA__\"[^>]*>(.*?)</script>", RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value : null;
        }

        // Depth-first walk of the Next.js data tree, collecting every object that looks like a release note.
        // Order is preserved (document order), which is the order Roblox lists the notes in.
        private static void CollectReleaseEntries(JsonElement el, List<ReleaseNoteEntry> into)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    if (el.TryGetProperty("ReleaseNotesText", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    {
                        into.Add(new ReleaseNoteEntry
                        {
                            Text = textEl.GetString() ?? "",
                            Type = el.TryGetProperty("ReleaseNotesType", out var ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString() ?? "" : "",
                            Status = el.TryGetProperty("Status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "" : "",
                        });
                    }
                    else
                    {
                        foreach (var prop in el.EnumerateObject())
                            CollectReleaseEntries(prop.Value, into);
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                        CollectReleaseEntries(item, into);
                    break;
            }
        }

        // ---- helpers ------------------------------------------------------------------

        // Returns null on a non-success status. A 403 Cloudflare challenge or a 404 for a
        // release with no notes page is routine for these public endpoints — that's a
        // one-line log and an empty result, not an exception with a stack trace.
        private static async Task<string?> GetStringAsync(string url)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/html;q=0.9, */*;q=0.8");

            using var resp = await App.HttpClient.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                App.Logger.WriteLine(LOG_IDENT, $"GET {url} -> {(int)resp.StatusCode} {resp.StatusCode}");
                return null;
            }
            return await resp.Content.ReadAsStringAsync();
        }

        private static string CleanExcerpt(string? raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "";
            string decoded = WebUtility.HtmlDecode(raw);
            return Regex.Replace(decoded, @"\s+", " ").Trim();
        }

        private static long GetLong(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

        private static string? GetString(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool GetBool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) && v.GetBoolean();

        private static DateTime GetDate(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), out var d)
                ? d.ToLocalTime()
                : DateTime.MinValue;
    }
}
