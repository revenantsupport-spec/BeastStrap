using System.Windows;

using BeastStrap.Models.APIs;
using BeastStrap.Models.Persistable;
using BeastStrap.RobloxInterfaces;
using BeastStrap.UI;
using WinForms = System.Windows.Forms;

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
    // v420.50.1+: when the executor is DOWN for the newest Roblox (LIVE moved to a
    // newer hash than the executor advertises), the profile is not silently pinned
    // down. The user is asked once per Roblox update whether to downgrade to the last
    // version the executor still supports (see RefreshActiveAsync / PromptDowngrade).
    //
    // The refresh is best-effort and bounded: a slow or dead WEAO never blocks
    // launch. If the request can't complete inside the budget, we fall through
    // to whatever VersionGuid is already on the profile.
    public static class ExecutorProfileRefresher
    {
        // Refresh the currently active profile against WEAO, returning when the
        // update is saved or the budget elapses (whichever comes first).
        //
        // v420.50.1+: this is also where the auto-downgrade protection lives. When the
        // tracked executor is down for the newest LIVE build (Roblox just shipped a new
        // hash, the executor still advertises an older one), we ask the user once per
        // Roblox update before pinning the profile down to the version the executor still
        // supports. When the executor catches back up the pin follows forward silently.
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

                // Best-effort LIVE lookup so we can tell "executor up to date" from
                // "executor is down for the newest build". If it fails we can't reason
                // about a downgrade, so we fall back to the old behaviour: follow the
                // executor's advertised version without asking.
                string? liveHash = null;
                try
                {
                    liveHash = (await Deployment.GetInfo(token: cts.Token)).VersionGuid;
                }
                catch (OperationCanceledException)
                {
                    App.Logger.WriteLine(LOG_IDENT, "LIVE hash lookup exceeded budget — following executor version without downgrade policy.");
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"LIVE hash lookup failed ({ex.GetType().Name}) — following executor version without downgrade policy.");
                }

                bool executorUpToDate = string.IsNullOrEmpty(liveHash)
                    || string.Equals(match.RbxVersion, liveHash, StringComparison.OrdinalIgnoreCase);

                if (executorUpToDate)
                {
                    bool recovering = !string.IsNullOrEmpty(profile.DowngradeDismissedForHash);
                    profile.DowngradeDismissedForHash = "";

                    if (!string.Equals(profile.VersionGuid, match.RbxVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Profile '{profile.Name}': WEAO advertises {match.RbxVersion} (was {profile.VersionGuid}). Updating.");
                        profile.VersionGuid = match.RbxVersion;

                        if (recovering && !string.IsNullOrEmpty(liveHash))
                        {
                            App.Logger.WriteLine(LOG_IDENT, $"{match.Title} caught up to {liveHash} — profile moved back to the newest build.");
                            LiveChannelToast.ShowToast(
                                title: $"{match.Title} is back on the latest Roblox",
                                message: $"Your profile was pinned to an older build while it was down. It's now following the newest Roblox again ({liveHash}).",
                                icon: WinForms.ToolTipIcon.Info);
                        }
                    }
                    else
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Profile '{profile.Name}' is already up to date ({profile.VersionGuid}).");
                    }
                    profile.LastExecutorRefreshUtc = DateTime.UtcNow;
                    App.Settings.Save();
                    return;
                }

                // The executor is down: Roblox LIVE is newer than what it supports.
                if (App.LaunchSettings.QuietFlag.Active)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"{match.Title} is down for {liveHash} but quiet mode is active — leaving profile at {profile.VersionGuid}.");
                    return;
                }

                if (!App.Settings.Prop.AutoDowngradeExecutors)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"{match.Title} is down for {liveHash} but auto-downgrade is off — leaving profile at {profile.VersionGuid}.");
                    return;
                }

                if (string.Equals(profile.DowngradeDismissedForHash, liveHash, StringComparison.OrdinalIgnoreCase))
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Already asked about this Roblox update ({liveHash}) — leaving profile at {profile.VersionGuid}.");
                    return;
                }

                bool downgrade = PromptDowngrade(profile, match, liveHash!);
                profile.DowngradeDismissedForHash = liveHash!;

                if (downgrade)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"User approved downgrade for '{match.Title}': {profile.VersionGuid} -> {match.RbxVersion}.");
                    profile.VersionGuid = match.RbxVersion;
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, $"User declined downgrade for '{match.Title}' — staying on {profile.VersionGuid}.");
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

        // v420.50.1+: asks once per Roblox update whether to pin the active executor
        // profile down to the last Roblox version the executor still supports. Runs on
        // the UI thread in the normal launch chain; a dispatcher hop keeps it safe if
        // it's ever reached from a background thread. Quiet mode short-circuits inside
        // Frontend.ShowMessageBox (returns the default result) and is excluded above.
        private static bool PromptDowngrade(VersionProfile profile, WeaoExploit match, string liveHash)
        {
            const string LOG_IDENT = "ExecutorProfileRefresher::PromptDowngrade";

            string executorName = string.IsNullOrWhiteSpace(profile.ExecutorTitle) ? match.Title : profile.ExecutorTitle!;
            string statusNote = "";
            if (!match.UpdateStatus)
                statusNote = "\nWEAO currently marks it as not updated for the newest Roblox.";
            else if (match.HasIssues)
                statusNote = "\nWEAO currently reports issues with it.";

            string body =
                $"Roblox LIVE just moved to:\n{liveHash}\n\n" +
                $"'{executorName}' still supports:\n{match.RbxVersion}\n" +
                $"so it may not work with the newest build.{statusNote}\n\n" +
                "Pin your profile to the version your executor still works with? BeastStrap " +
                "switches back to the newest Roblox automatically once the executor updates.\n\n" +
                "Yes = downgrade now\nNo = stay on the newest build";

            MessageBoxResult result;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
                result = dispatcher.Invoke(() => Frontend.ShowMessageBox(body, MessageBoxImage.Warning, MessageBoxButton.YesNo, MessageBoxResult.No));
            else
                result = Frontend.ShowMessageBox(body, MessageBoxImage.Warning, MessageBoxButton.YesNo, MessageBoxResult.No);

            App.Logger.WriteLine(LOG_IDENT, $"User response: {result}");
            return result == MessageBoxResult.Yes;
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
