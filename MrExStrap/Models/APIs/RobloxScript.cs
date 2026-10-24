using System.Text.Json.Serialization;

namespace BeastStrap.Models.APIs
{
    // Minimal shape we consume from https://robloxscripts.com/api/v1/scripts.
    // Only fields the Scripts tab actually uses are mapped; the API returns
    // several more (id, image, createdAt, features…) we can safely ignore.
    public class RobloxScript
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("slug")]
        public string Slug { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        /// <summary>Thumbnail, usually a Roblox CDN game icon (tr.rbxcdn.com).</summary>
        [JsonPropertyName("image")]
        public string? Image { get; set; }

        // Game names as plain strings. The API also has a richer "game" object, but it is
        // frequently null (it was null even on a Blox Fruits result), so this is the reliable one.
        // ⚠️ On hub entries these can be unresolved placeholders like "Game 4954752502" — the
        // digits are a place id the site never resolved to a name.
        [JsonPropertyName("games")]
        public List<string>? Games { get; set; }

        /// <summary>
        /// The richer game object — name plus an icon. Null far more often than <see cref="Games"/>
        /// is populated (10 of 25 in a sample), so treat it as a bonus, not the source of truth.
        /// </summary>
        /// <remarks>⚠️ iconUrl is frequently a <c>…/Image/Webp/…</c> URL despite coming from the
        /// Roblox CDN, so it goes through ExecutorLogoCache's transcode like everything else.</remarks>
        [JsonPropertyName("game")]
        public RobloxScriptGame? GameInfo { get; set; }

        /// <summary>What the script actually does — "autofarms", "esp", "duplications"…</summary>
        /// <remarks>The most useful field for a details view: populated on 24 of 25 sampled scripts.</remarks>
        [JsonPropertyName("features")]
        public List<string>? Features { get; set; }

        [JsonPropertyName("author")]
        public RobloxScriptAuthor? Author { get; set; }

        // "free" | "key" | "paid"
        [JsonPropertyName("accessType")]
        public string AccessType { get; set; } = "";

        /// <summary>True when a key system gates the script; <see cref="KeyLink"/> is where to get one.</summary>
        [JsonPropertyName("key")]
        public bool Key { get; set; }

        [JsonPropertyName("keyLink")]
        public string? KeyLink { get; set; }

        /// <summary>Null for everything free. Currency is unspecified by the API.</summary>
        /// <remarks>
        /// ⚠️ MUST stay a nullable floating-point type. Across a 400-script sample this field came
        /// back null 346 times, int 52 times and float twice — an <c>int</c> here throws on both the
        /// nulls and the decimals, and because the whole page is one deserialize that would take out
        /// the entire Scripts tab, not just the price.
        /// </remarks>
        [JsonPropertyName("price")]
        public double? Price { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime? CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTime? UpdatedAt { get; set; }

        [JsonPropertyName("hasMultipleScripts")]
        public bool HasMultipleScripts { get; set; }

        /// <summary>Alternative builds of the same script, each with its own loadstring.</summary>
        /// <remarks>Rare — 1 of 25 sampled — but when present the alternatives are the whole point
        /// of the entry, so a details view has to show them.</remarks>
        [JsonPropertyName("variants")]
        public List<RobloxScriptVariant>? Variants { get; set; }

        [JsonPropertyName("views")]
        public int Views { get; set; }

        [JsonPropertyName("likes")]
        public int Likes { get; set; }

        [JsonPropertyName("tags")]
        public List<string>? Tags { get; set; }

        [JsonPropertyName("isUniversal")]
        public bool IsUniversal { get; set; }

        [JsonPropertyName("isHub")]
        public bool IsHub { get; set; }

        /// <summary>Page on robloxscripts.com, for the "open in browser" action.</summary>
        [JsonPropertyName("scriptPageUrl")]
        public string ScriptPageUrl { get; set; } = "";

        /// <summary>Serves the script body as text/plain.</summary>
        /// <remarks>
        /// ⚠️ Often returns ANOTHER loadstring pointing at GitHub rather than real code — one
        /// sampled script was 123 bytes of loader. Fine to copy or run, but do not assume a preview
        /// pane will show anything meaningful.
        /// </remarks>
        [JsonPropertyName("rawScriptUrl")]
        public string RawScriptUrl { get; set; } = "";

        /// <summary>Ready-to-run one-liner: loadstring(game:HttpGet("…/raw/&lt;slug&gt;"))().</summary>
        /// <remarks>This is what users actually paste into an executor, so it's the default copy.</remarks>
        [JsonPropertyName("script")]
        public string Script { get; set; } = "";
    }

    public class RobloxScriptGame
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("slug")]
        public string Slug { get; set; } = "";

        [JsonPropertyName("iconUrl")]
        public string? IconUrl { get; set; }
    }

    public class RobloxScriptVariant
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        /// <summary>This variant's own ready-to-run loadstring — not the parent script's.</summary>
        [JsonPropertyName("script")]
        public string Script { get; set; } = "";

        [JsonPropertyName("rawScriptUrl")]
        public string RawScriptUrl { get; set; } = "";
    }

    public class RobloxScriptAuthor
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = "";

        [JsonPropertyName("verified")]
        public bool Verified { get; set; }
    }

    // { apiVersion, data[], pagination, meta }
    public class RobloxScriptsResponse
    {
        [JsonPropertyName("data")]
        public List<RobloxScript>? Data { get; set; }

        [JsonPropertyName("pagination")]
        public RobloxScriptsPagination? Pagination { get; set; }

        [JsonPropertyName("meta")]
        public RobloxScriptsMeta? Meta { get; set; }
    }

    public class RobloxScriptsPagination
    {
        [JsonPropertyName("page")]
        public int Page { get; set; }

        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("pages")]
        public int Pages { get; set; }

        [JsonPropertyName("hasMore")]
        public bool HasMore { get; set; }
    }

    public class RobloxScriptsMeta
    {
        /// <summary>
        /// Only present when a placeId query actually resolved to a known game. The API OMITS this
        /// on a miss rather than sending false, so null is the "we don't have scripts for this
        /// place" signal.
        /// </summary>
        [JsonPropertyName("matched")]
        public bool? Matched { get; set; }

        [JsonPropertyName("gameName")]
        public string? GameName { get; set; }
    }
}
