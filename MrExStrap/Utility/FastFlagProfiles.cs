namespace BeastStrap.Utility
{
    // Per-Versions-Manager-profile fast flag storage. Each profile's flag set lives at
    // Paths.FastFlagProfiles\<profileId>.json, kept OUTSIDE Modifications\ so the launch
    // overlay copy never ships these into the Roblox install.
    //
    // At launch the ACTIVE profile's set is materialised into the canonical
    // Modifications\ClientSettings\ClientAppSettings.json that the existing overlay copy
    // applies — so the fragile Bootstrapper apply path stays exactly as it was.
    public static class FastFlagProfiles
    {
        private const string LOG_IDENT = "FastFlagProfiles";

        private static string CanonicalFile =>
            Path.Combine(Paths.Modifications, "ClientSettings", "ClientAppSettings.json");

        public static string PathFor(string profileId) =>
            Path.Combine(Paths.FastFlagProfiles, $"{profileId}.json");

        // First-run move of the old single global flag file onto the active (default LIVE)
        // profile so existing users keep their flags. No-op once any per-profile file exists.
        public static void MigrateGlobalIfNeeded()
        {
            try
            {
                Directory.CreateDirectory(Paths.FastFlagProfiles);

                if (Directory.EnumerateFiles(Paths.FastFlagProfiles, "*.json").Any())
                    return;

                if (!File.Exists(CanonicalFile))
                    return;

                string targetId = !string.IsNullOrEmpty(App.Settings.Prop.ActiveVersionProfileId)
                    ? App.Settings.Prop.ActiveVersionProfileId
                    : App.LiveBuiltInProfileId;

                File.Copy(CanonicalFile, PathFor(targetId), overwrite: false);
                App.Logger.WriteLine(LOG_IDENT, $"Migrated legacy global fast flags -> profile '{targetId}'.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::MigrateGlobalIfNeeded", ex);
            }
        }

        // Write the active profile's flags into the canonical file the launch overlay applies.
        // When the manager is off, or the active profile has no flag file, the canonical file
        // is emptied so nothing stale gets applied.
        //
        // Takes the profile the bootstrapper actually resolved for THIS launch rather than
        // re-reading the global ActiveVersionProfileId, because a -versionprofile launch
        // override picks a different profile without ever writing the global id — and that
        // launch must get the override profile's flags, not the global one's.
        public static void MaterializeActiveToCanonical(VersionProfile? activeProfile)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CanonicalFile)!);

                if (!App.Settings.Prop.UseFastFlagManager)
                {
                    WriteEmpty();
                    return;
                }

                string activeId = activeProfile?.Id ?? App.Settings.Prop.ActiveVersionProfileId;
                string source = string.IsNullOrEmpty(activeId) ? "" : PathFor(activeId);

                if (!string.IsNullOrEmpty(source) && File.Exists(source))
                {
                    File.Copy(source, CanonicalFile, overwrite: true);
                }
                else
                {
                    // Emptying the canonical file is correct for a profile the user just created,
                    // but it is also how a migration ordering bug used to destroy people's flags
                    // silently. Say so in the log when we're about to discard real content, so the
                    // next report of "my flags stopped applying" is one grep away from an answer.
                    if (HasContent(CanonicalFile))
                    {
                        App.Logger.WriteLine(LOG_IDENT,
                            $"Clearing non-empty {CanonicalFile} because profile '{activeId}' has no flag file at {source}. "
                            + "If this profile is supposed to have flags, they can be recovered from the per-profile files in "
                            + Paths.FastFlagProfiles + ".");
                    }

                    WriteEmpty();
                }

                App.Logger.WriteLine(LOG_IDENT, $"Materialised fast flags for active profile '{activeId}'.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::MaterializeActiveToCanonical", ex);
            }
        }

        private static void WriteEmpty() => File.WriteAllText(CanonicalFile, "{}");

        // "Has flags worth worrying about" — an absent file, an empty one, or a bare {} are all
        // nothing to lose.
        private static bool HasContent(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return false;

                string text = File.ReadAllText(path).Trim();
                return text.Length > 0 && text != "{}";
            }
            catch
            {
                return false; // unreadable is not a reason to hold up a launch
            }
        }

        public static void Delete(string profileId)
        {
            try
            {
                string path = PathFor(profileId);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    App.Logger.WriteLine(LOG_IDENT, $"Deleted fast flag file for profile '{profileId}'.");
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Delete", ex);
            }
        }
    }
}
