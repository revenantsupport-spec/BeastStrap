namespace BeastStrap.Utility
{
    // Recursively sums file sizes under each Paths.Versions\<guid> install so the
    // Versions Manager tab can show users how much disk space each profile uses.
    // All calls are best-effort and return 0 on permission errors or missing dirs
    // — never throw, since the UI binds these values straight to TextBlocks.
    public static class VersionsDiskUsage
    {
        private const string LOG_IDENT = "VersionsDiskUsage";

        public static long GetUsageBytes(string versionGuid)
        {
            if (string.IsNullOrEmpty(versionGuid) || string.IsNullOrEmpty(Paths.Versions))
                return 0;

            string dir = Path.Combine(Paths.Versions, versionGuid);
            return GetDirectorySize(dir);
        }

        /// <summary>
        /// Disk used by a profile's install wherever it currently sits — unparked at
        /// Versions\version-&lt;hash&gt;\ if it is the active one, otherwise in the parked root.
        /// </summary>
        /// <remarks>
        /// Only <see cref="GetUsageBytes"/> existed before, and it looks exclusively at
        /// Versions\&lt;versionGuid&gt;\. That meant every INACTIVE profile reported 0 bytes, and the
        /// built-in "Latest LIVE" profile (whose VersionGuid is empty by design, it tracks whatever
        /// is current) reported 0 unconditionally — so the "X across N profiles" total was wrong for
        /// anyone with more than one profile, which is everyone this feature is for.
        /// </remarks>
        public static long GetProfileUsageBytes(string profileId, string? versionGuid, string? installedVersionGuid)
        {
            if (string.IsNullOrEmpty(profileId))
                return 0;

            // Unparked: its files are at the active path under whichever build it has installed.
            // Prefer InstalledVersionGuid over VersionGuid — the built-in "Latest LIVE" profile has
            // an empty VersionGuid by design because it tracks whatever is current, so reading only
            // VersionGuid reported 0 for it forever.
            if (VersionProfileLayout.IsInstallTarget(profileId))
            {
                string installed = string.IsNullOrEmpty(installedVersionGuid)
                    ? versionGuid ?? ""
                    : installedVersionGuid;

                if (!string.IsNullOrEmpty(installed))
                    return GetUsageBytes(installed);

                // Active but nothing recorded yet (a fresh profile mid-first-install). Fall through
                // to the parked lookup, which finds nothing and reports 0 — which is correct.
            }

            return GetDirectorySize(VersionProfileLayout.FindParked(profileId) ?? "");
        }

        private static long GetDirectorySize(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return 0;

            long size = 0;
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { size += new FileInfo(file).Length; }
                    catch { /* file gone or no access — skip */ }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::GetDirectorySize", ex);
            }
            return size;
        }

        // "1.4 GB" / "684 MB" / "0 B" — fits a one-line disk usage label in the UI.
        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024L * 1024) return $"{bytes / 1024.0:0.0} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:0.0} MB";
            return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB";
        }
    }
}
