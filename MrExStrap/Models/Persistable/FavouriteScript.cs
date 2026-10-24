namespace BeastStrap.Models.Persistable
{
    /// <summary>A script the user starred on the Scripts page.</summary>
    /// <remarks>
    /// Stores enough to render the favourites list on its own, so starred scripts still show up
    /// when the API is unreachable. The catalogue has no by-slug JSON endpoint anyway, so
    /// re-resolving a favourite would mean a search per entry.
    /// </remarks>
    public class FavouriteScript
    {
        public string Slug { get; set; } = "";

        public string Title { get; set; } = "";

        /// <summary>First game name from the API, or "" for universal scripts.</summary>
        public string Game { get; set; } = "";

        /// <summary>"free" | "key" | "paid".</summary>
        public string AccessType { get; set; } = "";

        /// <summary>The ready-to-run loadstring one-liner, so Copy works offline.</summary>
        public string Script { get; set; } = "";

        public string ScriptPageUrl { get; set; } = "";

        public DateTime StarredUtc { get; set; } = DateTime.UtcNow;
    }
}
