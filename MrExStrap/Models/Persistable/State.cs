using System.Collections.Generic;

namespace BeastStrap.Models.Persistable
{
    public class State
    {
        public bool PromptWebView2Install { get; set; } = true;

        public bool ForceReinstall { get; set; } = false;

        // Which Versions Manager profile currently owns the unparked install at
        // Versions\version-<hash>\, and the hash it's sitting under. Only one profile is unparked at
        // a time; every other profile's install waits at Versions\profile-<id>\. Needed so a profile
        // switch knows whose folder it's moving out of the way. See Utility/VersionProfileLayout.cs.
        public string ActiveInstallProfileId { get; set; } = "";

        public string ActiveInstallVersionGuid { get; set; } = "";

        public WindowState SettingsWindow { get; set; } = new();

        // Recent hashes the user has verified/pinned (BeastStrap fork feature).
        // Newest first. Capped at 10 via the push helper.
        public List<string> RecentCustomVersionHashes { get; set; } = new();

        // Games the user has played, newest first. Capped at 10 via PushRecentPlace.
        // Feeds the launch-time game picker (BeastStrap fork feature).
        public List<RecentPlace> RecentPlaces { get; set; } = new();

        // Dedupe by placeId, push to front, cap at 10. Keeps an existing name when the caller
        // doesn't have one, so a join that couldn't resolve the title never wipes a good label.
        public void PushRecentPlace(long placeId, string? name)
        {
            if (placeId <= 0)
                return;

            var existing = RecentPlaces.FirstOrDefault(p => p.PlaceId == placeId);
            RecentPlaces.RemoveAll(p => p.PlaceId == placeId);

            RecentPlaces.Insert(0, new RecentPlace
            {
                PlaceId = placeId,
                Name = string.IsNullOrWhiteSpace(name) ? existing?.Name ?? "" : name,
                LastPlayedUtc = DateTime.UtcNow,
            });

            while (RecentPlaces.Count > 10)
                RecentPlaces.RemoveAt(RecentPlaces.Count - 1);
        }

        // LIVE version the user has already seen. Used to detect when Roblox
        // has shipped a new build since the last open so we can show a banner.
        public string DismissedLiveHash { get; set; } = "";

        // Hash → first-seen UTC time. Each LIVE hash this install observes gets
        // timestamped here the first time we see it. Useful because the CDN's
        // Last-Modified header reflects when the package was uploaded — often many
        // hours before Roblox flips the LIVE pointer to this build.
        public Dictionary<string, DateTime> LiveHashFirstSeenUtc { get; set; } = new();

        // v420.28+: last LIVE hash a "Roblox just shipped X" toast was fired for.
        // Stops the same toast from re-firing every launcher open / tray poll once
        // the user has already seen it. Updated atomically with the toast call.
        public string LastNotifiedLiveHash { get; set; } = "";

        // v420.29.5+: last BeastStrap release tag a "new version available" toast
        // was fired for. Stops the same toast re-firing every launcher open / tray poll.
        // Seeded silently on first observation so we never toast just for noticing the
        // current installed version.
        public string LastNotifiedAppVersion { get; set; } = "";

        // How many update checks in a row have failed, and when one last succeeded.
        //
        // A silently dead updater is worse than no updater. When the project moved off GitHub the
        // old endpoint started returning 404 forever, and every affected install just logged the
        // exception and carried on — a 2026-07-27 crash report came from a v420.40 copy that had
        // been failing this check on every single launch for weeks and had never once said so.
        // The user was stranded on a build with a known, already-fixed crash bug.
        //
        // Reset on any successful check. A user-cancelled or budgeted-out check does NOT count,
        // otherwise a slow connection would eventually nag.
        public int UpdateCheckFailureStreak { get; set; } = 0;

        public DateTime? LastSuccessfulUpdateCheckUtc { get; set; } = null;

        // App version the "we can't reach the update server" warning was last shown for, so it
        // fires once per installed version and re-arms after an upgrade rather than nagging.
        public string LastNotifiedDeadUpdaterVersion { get; set; } = "";

        // QoL: remember last visited settings page + collapsed nav sections.
        public string LastVisitedPage { get; set; } = "HomePage";
        public List<string> CollapsedSidebarSections { get; set; } = new();

        // Tags of sidebar tabs the user pinned to the top. Stored as a list of Tag values
        // matching the NavigationItem.Tag entries (e.g. "mods", "fastflags", "appearance").
        public List<string> PinnedSidebarTabs { get; set; } = new();
    }
}
