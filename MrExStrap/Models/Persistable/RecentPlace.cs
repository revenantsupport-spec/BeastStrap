namespace BeastStrap.Models.Persistable
{
    // A game the user has actually played through BeastStrap. Recorded on every game join so the
    // launch-time game picker has something to offer — without this, "join the emptiest server on
    // launch" can only work for launches that came from a game page on the website.
    public class RecentPlace
    {
        public long PlaceId { get; set; }

        // Best-known display name. Falls back to the place id in the UI when we never resolved one
        // (the universe details lookup is cached and can miss).
        public string Name { get; set; } = "";

        public DateTime LastPlayedUtc { get; set; } = DateTime.UtcNow;

        [JsonIgnore]
        public string DisplayLabel => string.IsNullOrWhiteSpace(Name) ? $"Place {PlaceId}" : Name;
    }
}
