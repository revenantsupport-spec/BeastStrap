namespace BeastStrap.Utility
{
    // Pre-launch hook for executor-tracked Versions Manager profiles.
    //
    // Profiles that were created via the "From executor" branch of the
    // AddVersionProfileDialog carry an ExecutorRefreshKey (the WEAO Title at
    // time of add). On every launch we re-query WEAO and update the profile's
    // VersionGuid to whatever the executor is currently supporting — so when
    // Solara / Velocity / Matrix Hub etc. push a new build, the user gets it
    // on the next launch without re-adding the profile.
    //
    // The refresh is best-effort and bounded: a slow or dead WEAO never blocks
    // launch. If the request can't complete inside the budget, we fall through
    // to whatever VersionGuid is already on the profile.
    public static class ExecutorProfileRefresher
    {
        // Refresh the currently active profile against WEAO, returning when the
        // update is saved or the budget elapses (whichever comes first).
        public static async Task RefreshActiveAsync(TimeSpan budget)
        {
            const string LOG_IDENT = "ExecutorProfileRefresher::RefreshActiveAsync";

            string activeId = App.Settings.Prop.ActiveVersionProfileId;
            if (string.IsNullOrEmpty(activeId))
                return;

            var profile = App.Settings.Prop.VersionProfiles.FirstOrDefault(p => p.Id == activeId);
            if (profile is null || string.IsNullOrWhiteSpace(profile.ExecutorRefreshKey))
                return;

            using var cts = new CancellationTokenSource(budget);
            try
            {
                var result = await WeaoClient.GetWindowsExploitsAsync(cts.Token);
                if (!result.Success || result.Exploits.Count == 0)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"WEAO refresh skipped: {result.Error ?? "empty list"}");
                    return;
                }

                var match = result.Exploits.FirstOrDefault(e =>
                    string.Equals(e.Title, profile.ExecutorRefreshKey, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"No WEAO match for executor key '{profile.ExecutorRefreshKey}' — leaving profile at {profile.VersionGuid}.");
                    return;
                }

                if (!VersionGuidValidator.IsWellFormed(match.RbxVersion))
                {
                    App.Logger.WriteLine(LOG_IDENT, $"WEAO returned malformed RbxVersion '{match.RbxVersion}' for '{match.Title}' — leaving profile at {profile.VersionGuid}.");
                    return;
                }

                if (!string.Equals(profile.VersionGuid, match.RbxVersion, StringComparison.OrdinalIgnoreCase))
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Profile '{profile.Name}': WEAO advertises {match.RbxVersion} (was {profile.VersionGuid}). Updating.");
                    profile.VersionGuid = match.RbxVersion;
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Profile '{profile.Name}' is already up to date ({profile.VersionGuid}).");
                }
                profile.LastExecutorRefreshUtc = DateTime.UtcNow;
                App.Settings.Save();
            }
            catch (OperationCanceledException)
            {
                App.Logger.WriteLine(LOG_IDENT, $"WEAO refresh exceeded {budget.TotalSeconds:F1}s budget — using cached version {profile.VersionGuid}.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        // Refresh every executor-tracked profile (not just the active one). Used by
        // the Versions Manager's Refresh button so a user who clicks it sees all of
        // their pinned executor profiles converge to whatever WEAO currently says.
        public static async Task RefreshAllAsync(TimeSpan budget)
        {
            const string LOG_IDENT = "ExecutorProfileRefresher::RefreshAllAsync";

            var trackedProfiles = App.Settings.Prop.VersionProfiles
                .Where(p => !string.IsNullOrWhiteSpace(p.ExecutorRefreshKey))
                .ToArray();
            if (trackedProfiles.Length == 0)
                return;

            using var cts = new CancellationTokenSource(budget);
            try
            {
                var result = await WeaoClient.GetWindowsExploitsAsync(cts.Token);
                if (!result.Success || result.Exploits.Count == 0)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"WEAO refresh skipped: {result.Error ?? "empty list"}");
                    return;
                }

                bool anyChanged = false;
                foreach (var profile in trackedProfiles)
                {
                    var match = result.Exploits.FirstOrDefault(e =>
                        string.Equals(e.Title, profile.ExecutorRefreshKey, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"No WEAO match for '{profile.ExecutorRefreshKey}' — leaving '{profile.Name}' at {profile.VersionGuid}.");
                        continue;
                    }
                    if (!VersionGuidValidator.IsWellFormed(match.RbxVersion))
                        continue;

                    if (!string.Equals(profile.VersionGuid, match.RbxVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Profile '{profile.Name}': {profile.VersionGuid} -> {match.RbxVersion}.");
                        profile.VersionGuid = match.RbxVersion;
                        anyChanged = true;
                    }
                    profile.LastExecutorRefreshUtc = DateTime.UtcNow;
                }

                App.Settings.Save();
                if (!anyChanged)
                    App.Logger.WriteLine(LOG_IDENT, "All executor profiles already up to date.");
            }
            catch (OperationCanceledException)
            {
                App.Logger.WriteLine(LOG_IDENT, $"WEAO refresh exceeded {budget.TotalSeconds:F1}s budget — no profiles updated.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }
    }
}
