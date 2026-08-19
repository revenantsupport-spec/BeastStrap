namespace BeastStrap.Models.Persistable
{
    // A single saved Roblox version slot. The user can have many of these — one per
    // executor they care about, plus a built-in "Latest LIVE" sentinel — and switches
    // between them with one click on the Versions Manager tab.
    //
    // VersionGuid == "" is special: it means "always use the current LIVE build", so
    // the built-in profile doesn't have to be re-pinned every time Roblox ships a new
    // production hash.
    public class VersionProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";

        // Empty means "always use the current LIVE hash from Roblox CDN". For pinned
        // profiles this is a well-formed "version-<16 hex>" string.
        public string VersionGuid { get; set; } = "";

        // Populated when the profile was created via the WEAO executor dropdown so
        // we can show the title in the tile and (later) periodically check whether
        // the upstream executor has updated to a newer Roblox build.
        public string? ExecutorTitle { get; set; }

        // WEAO CDN logo URL (slug.logo). The Versions Manager tile fetches and caches
        // this through Utility.ExecutorLogoCache on first display.
        public string? ExecutorLogoUrl { get; set; }

        // Locked from edit/delete in the UI. The seed "Latest LIVE" profile sets this
        // so users can't accidentally remove the always-LIVE fallback.
        public bool IsBuiltIn { get; set; }

        // Version hash currently materialised on disk for this profile. Kept around
        // from v420.20's per-profile dir layout so the v420.20 -> v420.23 reverse
        // migration knows what hash to rename Versions/profile-<id> back to. The
        // active launch path now uses Versions/version-<hash> shared across same-hash
        // profiles (executor compatibility — see v420.23 release notes).
        public string InstalledVersionGuid { get; set; } = "";

        // When non-empty, the launch path refreshes this profile's VersionGuid from
        // WEAO by matching this key against WeaoExploit.Title (case-insensitive). Set
        // when the profile was created via the "From executor" branch of the
        // AddVersionProfileDialog so executor updates flow into the profile without
        // re-adding it. Empty for manual profiles and the built-in "Latest LIVE".
        public string ExecutorRefreshKey { get; set; } = "";

        // Stamped when the WEAO auto-refresh last succeeded. Surfaced on the tile as
        // "Updated <relative time>" so the user knows when the version last moved.
        public DateTime? LastExecutorRefreshUtc { get; set; }

        // v420.28+: hash this profile was at the last time a "<executor> updated"
        // toast fired. Lets UpdateMonitor pop the toast exactly once per WEAO bump
        // instead of every launcher open / tray poll.
        public string LastNotifiedExecutorHash { get; set; } = "";

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }
}
