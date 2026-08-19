using BeastStrap.RobloxInterfaces;
using WinForms = System.Windows.Forms;

namespace BeastStrap.Utility
{
    // v420.28: opt-in background checks that fire Windows balloon-tip toasts
    // when (a) Roblox's LIVE channel hash moves to a new build or (b) any
    // executor with a tracked profile updates on WEAO.
    //
    // Run-points:
    //   - LaunchMenu open (fire-and-forget so the menu doesn't block on it).
    //   - Tray launcher's 30-minute timer when the tray feature is enabled.
    //
    // Both checks are bounded by a 3-second cancellation token so a slow CDN
    // or WEAO outage never blocks the launcher or wedges the tray timer.
    // Failures are logged but never surface to the user — toast spam from
    // monitoring telemetry would be worse than the missed update.
    public static class UpdateMonitor
    {
        private const string LOG_IDENT = "UpdateMonitor";
        private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(3);

        // Runs the LIVE, executor, and app-update checks in parallel. Individual
        // failures don't suppress the others.
        public static async Task CheckAllAsync()
        {
            var liveTask = CheckLiveAsync();
            var exTask = CheckExecutorsAsync();
            var appTask = CheckAppUpdateAsync();
            await Task.WhenAll(liveTask, exTask, appTask);
        }

        // Toast when a newer BeastStrap release is on GitHub. Complements the
        // menu-open "install now?" prompt (LaunchHandler.TryMenuAutoUpgrade): that one
        // is modal and only fires when the user opens the menu, this one passively
        // notifies tray users and anyone who declined the prompt. Seed-once guard via
        // State.LastNotifiedAppVersion so it doesn't re-fire every poll for the same
        // release.
        public static async Task CheckAppUpdateAsync()
        {
            if (!App.Settings.Prop.NotifyOnAppUpdate)
                return;

            using var cts = new CancellationTokenSource(DefaultBudget);
            try
            {
                var release = await App.GetLatestRelease(cts.Token);
                if (release is null || string.IsNullOrEmpty(release.TagName))
                    return;

                // Only toast when the release is strictly newer than what's running.
                VersionComparison cmp;
                try
                {
                    cmp = Utilities.CompareVersions(App.Version, release.TagName);
                }
                catch
                {
                    return; // unparseable tag (e.g. an untagged-<sha> slug) — stay quiet
                }

                if (cmp != VersionComparison.LessThan)
                    return;

                NotifyUpdateAvailable(release.TagName);
            }
            catch (OperationCanceledException)
            {
                App.Logger.WriteLine(LOG_IDENT, $"App-update check exceeded {DefaultBudget.TotalSeconds:F1}s budget.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::CheckAppUpdateAsync", ex);
            }
        }

        /// <summary>
        /// Fires the "a newer build is out" toast, at most once per release tag.
        /// </summary>
        /// <remarks>
        /// Shared with Bootstrapper.CheckForUpdates, which reaches this when it finds an update it
        /// isn't allowed to install — portable mode, or another BeastStrap process holding the
        /// exe. That second case is every Multi Instance session, and before this existed those
        /// users were told nothing at all.
        /// </remarks>
        public static void NotifyUpdateAvailable(string tagName)
        {
            if (string.IsNullOrEmpty(tagName))
                return;

            if (!App.Settings.Prop.NotifyOnAppUpdate)
                return;

            string last = App.State.Prop.LastNotifiedAppVersion ?? "";
            if (string.Equals(tagName, last, StringComparison.OrdinalIgnoreCase))
                return; // already toasted this release

            App.Logger.WriteLine(LOG_IDENT, $"App update available: running v{App.Version}, latest {tagName}. Firing toast.");
            LiveChannelToast.ShowToast(
                title: "BeastStrap update available",
                message: $"Version {tagName} is out (you're on v{App.Version}). Open BeastStrap to update.",
                icon: WinForms.ToolTipIcon.Info);

            // SaveMerged, not Save: this runs on the tray's background timer, where our in-memory
            // copy can be hours stale. See JsonManager.SaveMerged.
            App.State.SaveMerged(s => s.LastNotifiedAppVersion = tagName);
        }

        public static async Task CheckLiveAsync()
        {
            if (!App.Settings.Prop.NotifyOnLiveChange)
                return;

            using var cts = new CancellationTokenSource(DefaultBudget);
            try
            {
                var info = await Deployment.GetInfo(token: cts.Token);
                string currentHash = info.VersionGuid;
                if (string.IsNullOrEmpty(currentHash))
                    return;

                string last = App.State.Prop.LastNotifiedLiveHash ?? "";
                if (string.Equals(currentHash, last, StringComparison.OrdinalIgnoreCase))
                    return;

                // First-ever notification: don't pop a toast — just record the
                // current hash silently. The toast is for *changes*, and on a
                // brand-new install we don't know whether the current hash is
                // brand new or has been live for days.
                if (string.IsNullOrEmpty(last))
                {
                    App.State.SaveMerged(s => s.LastNotifiedLiveHash = currentHash);
                    App.Logger.WriteLine(LOG_IDENT, $"Seeded LastNotifiedLiveHash with {currentHash} (no toast on first observation).");
                    return;
                }

                App.Logger.WriteLine(LOG_IDENT, $"LIVE hash changed: {last} -> {currentHash}. Firing toast.");
                LiveChannelToast.ShowToast(
                    title: "Roblox just shipped a new build",
                    message: $"LIVE is now {currentHash}. Your executor may need to update — check the Versions Manager.",
                    icon: WinForms.ToolTipIcon.Info);

                App.State.SaveMerged(s => s.LastNotifiedLiveHash = currentHash);
            }
            catch (OperationCanceledException)
            {
                App.Logger.WriteLine(LOG_IDENT, $"LIVE check exceeded {DefaultBudget.TotalSeconds:F1}s budget.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::CheckLiveAsync", ex);
            }
        }

        public static async Task CheckExecutorsAsync()
        {
            if (!App.Settings.Prop.NotifyOnExecutorUpdate)
                return;

            var trackedProfiles = App.Settings.Prop.VersionProfiles
                .Where(p => !string.IsNullOrWhiteSpace(p.ExecutorRefreshKey))
                .ToArray();
            if (trackedProfiles.Length == 0)
                return;

            using var cts = new CancellationTokenSource(DefaultBudget);
            try
            {
                var result = await WeaoClient.GetWindowsExploitsAsync(cts.Token);
                if (!result.Success || result.Exploits.Count == 0)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Executor check skipped: {result.Error ?? "empty list"}");
                    return;
                }

                // Collect the changes keyed by profile id rather than writing them onto the
                // profile objects directly. SaveMerged may re-read Settings from disk, which
                // replaces every VersionProfile instance — anything written to the objects we
                // captured above would be silently dropped along with the old Prop.
                var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var profile in trackedProfiles)
                {
                    var match = result.Exploits.FirstOrDefault(e =>
                        string.Equals(e.Title, profile.ExecutorRefreshKey, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                        continue;
                    if (!VersionGuidValidator.IsWellFormed(match.RbxVersion))
                        continue;

                    string last = profile.LastNotifiedExecutorHash ?? "";

                    // First-ever notification for this profile: seed silently.
                    if (string.IsNullOrEmpty(last))
                    {
                        updates[profile.Id] = match.RbxVersion;
                        continue;
                    }

                    if (string.Equals(last, match.RbxVersion, StringComparison.OrdinalIgnoreCase))
                        continue;

                    App.Logger.WriteLine(LOG_IDENT, $"Executor '{match.Title}' updated: {last} -> {match.RbxVersion}. Firing toast.");
                    LiveChannelToast.ShowToast(
                        title: $"{match.Title} just updated",
                        message: $"Now on {match.RbxVersion}. BeastStrap applies the new version on the profile's next launch.",
                        icon: WinForms.ToolTipIcon.Info);
                    updates[profile.Id] = match.RbxVersion;
                }

                if (updates.Count > 0)
                {
                    App.Settings.SaveMerged(s =>
                    {
                        // Re-resolve by id: a profile the user deleted while we were on the network
                        // simply isn't found, which is the right outcome.
                        foreach (var profile in s.VersionProfiles)
                        {
                            if (updates.TryGetValue(profile.Id, out string? hash))
                                profile.LastNotifiedExecutorHash = hash;
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Executor check exceeded {DefaultBudget.TotalSeconds:F1}s budget.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::CheckExecutorsAsync", ex);
            }
        }
    }
}
