// To debug the automatic updater:
// - Uncomment the definition below
// - Publish the executable
// - Launch the executable (click no when it asks you to upgrade)
// - Launch Roblox (for testing web launches, run it from the command prompt)
// - To re-test the same executable, delete it from the installation folder

// #define DEBUG_UPDATER

#if DEBUG_UPDATER
#warning "Automatic updater debugging is enabled"
#endif

using System.ComponentModel;
using System.Data;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Shell;

using Microsoft.Win32;

using BeastStrap.AppData;
using BeastStrap.RobloxInterfaces;
using BeastStrap.UI.Elements.Bootstrapper.Base;

using ICSharpCode.SharpZipLib.Zip;

namespace BeastStrap
{
    public class Bootstrapper
    {
        #region Properties
        private const int ProgressBarMaximum = 10000;

        private const double TaskbarProgressMaximumWpf = 1; // this can not be changed. keep it at 1.
        private const int TaskbarProgressMaximumWinForms = WinFormsDialogBase.TaskbarProgressMaximum;

        private const string AppSettings =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
            "<Settings>\r\n" +
            "	<ContentFolder>content</ContentFolder>\r\n" +
            "	<BaseUrl>http://www.roblox.com</BaseUrl>\r\n" +
            "</Settings>\r\n";

        private readonly FastZipEvents _fastZipEvents = new();
        private readonly CancellationTokenSource _cancelTokenSource = new();

        // Keeps the update-handoff mutex alive until this process exits. See the
        // acquisition site in CheckForUpdates for the full protocol.
        private static InterProcessLock? _upgradeHandoffLock;

        private IAppData AppData = default!;
        private LaunchMode _launchMode;

        private string _launchCommandLine = App.LaunchSettings.RobloxLaunchArgs;
        private Version? _latestVersion = null;
        private string _latestVersionGuid = null!;
        private string _latestVersionDirectory = null!;
        private PackageManifest _versionPackageManifest = null!;
        private bool _channelFetched = false;

        // Versions Manager profile that drives this launch, if any. Resolved against
        // Settings on demand so we don't go stale if the user activates a different
        // profile mid-launch (shouldn't happen, but cheap to re-read).
        // Studio launches deliberately bypass profile mode — the profile system only
        // applies to LaunchMode.Player.
        // Multi-instance is active for this launch when the user's global toggle is on OR the
        // launch carried -multiinstance (every Multi Instance tab launch does — see
        // AccountLauncher). The latter guarantees account launches start an independent client
        // even when the toggle is off, instead of being swallowed by a running client.
        private static bool MultiInstanceActive =>
            App.Settings.Prop.MultiInstanceEnabled || App.LaunchSettings.MultiInstanceFlag.Active;

        private VersionProfile? GetActiveProfileForBootstrap()
        {
            if (_launchMode != LaunchMode.Player)
                return null;

            var profiles = App.Settings.Prop.VersionProfiles;

            // Per-account override from the Multi Instance tab (-versionprofile <id>). Applies to
            // THIS launch only — the global ActiveVersionProfileId is never written. An unknown id
            // (e.g. the profile was deleted) falls through to the global active profile below.
            if (App.LaunchSettings.VersionProfileFlag.Active
                && !string.IsNullOrEmpty(App.LaunchSettings.VersionProfileFlag.Data))
            {
                var overridden = profiles.FirstOrDefault(p => p.Id == App.LaunchSettings.VersionProfileFlag.Data);
                if (overridden != null)
                    return overridden;
            }

            if (string.IsNullOrEmpty(App.Settings.Prop.ActiveVersionProfileId))
                return null;
            return profiles.FirstOrDefault(p => p.Id == App.Settings.Prop.ActiveVersionProfileId);
        }

        // What Roblox version is actually installed for this launch?
        //
        // For a profile-driven launch the answer lives on the profile, NOT on the
        // global DistributionState. DistributionState.VersionGuid holds whichever
        // profile launched last, so reading it here made switching from an executor
        // profile (e.g. Synapse Z) to "Latest LIVE" redownload Roblox on every launch
        // even though the Latest LIVE profile's own folder already had the right build.
        //
        // When the profile has no recorded hash, recover from the actual client on
        // disk: if its file version matches the build we're about to launch, adopt it
        // instead of redownloading. The exe version is authoritative, so a genuinely
        // stale install (a newer LIVE build shipped) still fails the match and upgrades.
        private string ResolveInstalledVersionForLaunch(VersionProfile? activeProfile)
        {
            const string LOG_IDENT = "Bootstrapper::ResolveInstalledVersionForLaunch";

            if (activeProfile is null)
                return AppData.DistributionState.VersionGuid;

            if (!string.IsNullOrEmpty(activeProfile.InstalledVersionGuid))
            {
                // Self-heal stale bookkeeping: when the profile claims it is already on the
                // current LIVE build but the Roblox client actually on disk reports a
                // DIFFERENT version, the stored hash is lying (e.g. a past upgrade was
                // interrupted, or an orphan dir got adopted, and the new hash was stamped
                // without the matching files ever landing). Trusting it launches a stale
                // client that Roblox kicks with "version out of date" (Error 280) on every
                // join. Only act on a CONFIRMED mismatch — the profile points at the live
                // hash, _latestVersion is known, the exe is present, and its file version
                // differs — so pinned/downgrade profiles (where _latestVersion is null) and
                // any case where we cannot read a version keep trusting the stored hash and
                // never trigger a spurious reinstall (the v420.24 regression).
                if (activeProfile.InstalledVersionGuid == _latestVersionGuid
                    && _latestVersion is not null
                    && File.Exists(AppData.ExecutablePath)
                    && !InstalledExeMatchesLatest())
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Profile '{activeProfile.Name}' is bookmarked at {_latestVersionGuid} but the on-disk client is a different build — clearing the stale hash to force an upgrade to v{_latestVersion}.");
                    activeProfile.InstalledVersionGuid = "";
                    App.Settings.Save();
                    return "";
                }

                return activeProfile.InstalledVersionGuid;
            }

            if (InstalledExeMatchesLatest())
            {
                activeProfile.InstalledVersionGuid = _latestVersionGuid;
                App.Settings.Save();
                App.Logger.WriteLine(LOG_IDENT, $"Recovered InstalledVersionGuid for profile '{activeProfile.Name}' from on-disk client v{_latestVersion} -> {_latestVersionGuid}");
                return _latestVersionGuid;
            }

            return "";
        }

        // True when the Roblox client already present at the resolved install dir reports
        // the same file version as the build we're about to launch. Lets us recognise an
        // existing, current install whose per-profile bookkeeping was lost without ever
        // trusting a stale build.
        private bool InstalledExeMatchesLatest()
        {
            const string LOG_IDENT = "Bootstrapper::InstalledExeMatchesLatest";
            try
            {
                if (_latestVersion is null)
                    return false;

                string exePath = AppData.ExecutablePath;
                if (!File.Exists(exePath))
                    return false;

                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);

                // The numeric file-version fields come from Win32 VS_FIXEDFILEINFO, where each
                // of the four parts is a 16-bit WORD. Roblox's build number (the 4th part, e.g.
                // 7271199) overflows that, so FilePrivatePart reads back wrapped mod 65536
                // (7271199 -> 62239) and never equals _latestVersion's full value. Comparing the
                // two directly reported "differs" on every single launch of the live profile,
                // forcing a full re-extract each time — and because several account launches
                // re-extract into the one shared install folder at once, that trampled the
                // already-running clients and broke Multi Instance.
                //
                // Gate the decision on the numeric parts compared against _latestVersion wrapped
                // through the same 16-bit fields: when those differ a genuinely different client
                // is on disk (a real version bump moves the major/minor/build too), so the
                // Error 280 self-heal still fires for real staleness. When they match, confirm
                // with the untruncated StringFileInfo version (which keeps the full build
                // number) so the astronomically-rare case of two builds congruent mod 65536
                // can't masquerade as current.
                var onDisk = new Version(fvi.FileMajorPart, fvi.FileMinorPart, fvi.FileBuildPart, fvi.FilePrivatePart);
                var latestTruncated = TruncateToFileVersionFields(_latestVersion);

                if (onDisk != latestTruncated)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"On-disk {exePath} v{onDisk} vs latest v{_latestVersion} (compared as v{latestTruncated}): differs");
                    return false;
                }

                Version? onDiskFull = TryParseFullVersionString(fvi.FileVersion)
                                   ?? TryParseFullVersionString(fvi.ProductVersion);
                bool match = onDiskFull is null || onDiskFull == _latestVersion;
                App.Logger.WriteLine(LOG_IDENT, $"On-disk {exePath} v{onDiskFull?.ToString() ?? onDisk.ToString()} vs latest v{_latestVersion}: {(match ? "match" : "differs")}");
                return match;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                return false;
            }
        }

        // The Win32 file-version fields (VS_FIXEDFILEINFO) are each 16 bits, so a value like
        // Roblox's build number 7271199 is stored wrapped (7271199 & 0xFFFF = 62239). Mirror
        // that wrapping on the latest version so it lines up with what FileVersionInfo reads
        // back from the on-disk exe's numeric parts.
        private static Version TruncateToFileVersionFields(Version v) => new(
            v.Major & 0xFFFF,
            Math.Max(v.Minor, 0) & 0xFFFF,
            Math.Max(v.Build, 0) & 0xFFFF,
            Math.Max(v.Revision, 0) & 0xFFFF);

        // Parse the full, untruncated version Roblox writes into the exe's StringFileInfo block.
        // It comes back comma-separated ("0, 727, 0, 7271199"); normalise to a dotted form
        // Version.Parse accepts. Returns null if absent or malformed so the caller can fall
        // back to the numeric comparison.
        private static Version? TryParseFullVersionString(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            string normalized = raw.Replace(" ", "").Replace(',', '.');
            return Version.TryParse(normalized, out var parsed) ? parsed : null;
        }

        // v420.24: each profile owns its real Roblox install at
        // Versions\profile-<id>\, and the active profile's launch exposes that
        // dir under the standard Versions\version-<active-hash>\ name via a
        // directory junction. Executors that detect the Roblox version from
        // the install-dir name (Severe, etc.) still get "version-<16hex>", and
        // same-hash profiles don't leak files into each other anymore — each
        // has its own real folder, only the junction changes per launch.
        //
        // Called once per Player launch from GetLatestVersionInfo, after
        // _latestVersionGuid is resolved. Best-effort throughout: any failure
        // logs and falls back to the standard version-<hash> path.

        // Clear the contents of a junction's target without deleting the
        // junction itself. Used by UpgradeRoblox and the cancel-cleanup path
        // since Directory.Delete on a junction (even with recursive=true)
        // unlinks the junction, and Directory.CreateDirectory on the same path
        // would then create a real dir — which is exactly what tripped up
        // v420.24 (flippi's bug report 2026-05-24): the install landed in
        // Versions\version-<hash>\ as a real dir while the per-profile dir
        // stayed empty.
        private static void ClearJunctionTargetContents(string junctionPath)
        {
            const string LOG_IDENT = "Bootstrapper::ClearJunctionTargetContents";

            try
            {
                foreach (string sub in Directory.EnumerateDirectories(junctionPath))
                {
                    try { Directory.Delete(sub, true); }
                    catch (Exception ex) { App.Logger.WriteException(LOG_IDENT, ex); }
                }
                foreach (string file in Directory.EnumerateFiles(junctionPath))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) { App.Logger.WriteException(LOG_IDENT, ex); }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        private bool _isInstalling = false;
        private double _progressIncrement;
        private double _taskbarProgressIncrement;
        private double _taskbarProgressMaximum;
        private long _totalDownloadedBytes = 0;
        private long _totalPackedBytes = 0;

        // Speed/ETA tracking for the loading dialog. Sampled every UpdateProgressBar call;
        // smoothed via exponential moving average so the rate doesn't whiplash on each chunk.
        private DateTime? _speedSampleTime = null;
        private long _speedSampleBytes = 0;
        private double _smoothedBytesPerSec = 0;
        private bool _packageExtractionSuccess = true;

        private bool _mustUpgrade => App.LaunchSettings.ForceFlag.Active || App.State.Prop.ForceReinstall || String.IsNullOrEmpty(AppData.DistributionState.VersionGuid) || !File.Exists(AppData.ExecutablePath);
        private bool _noConnection = false;

        private AsyncMutex? _mutex;

        private int _appPid = 0;

        // v420.46: main window handle of the launched client, captured in StartRoblox for
        // window manipulation (custom icon / title / fake borderless). Zero when off or the
        // window never showed in time.
        private IntPtr _appWindowHandle = IntPtr.Zero;

        public IBootstrapperDialog? Dialog = null;

        public bool IsStudioLaunch => _launchMode != LaunchMode.Player;

        public string MutexName => $"{MutexNamePrefix}-{_launchMode}";
        public string BackgroundUpdaterMutexName => $"BeastStrap-BackgroundUpdater-{_launchMode}";

        public string MutexNamePrefix { get; set; } = "BeastStrap-Bootstrapper";
        public bool QuitIfMutexExists { get; set; } = false;
        #endregion

        #region Core
        public Bootstrapper(LaunchMode launchMode)
        {
            _launchMode = launchMode;

            // https://github.com/icsharpcode/SharpZipLib/blob/master/src/ICSharpCode.SharpZipLib/Zip/FastZip.cs/#L669-L680
            // exceptions don't get thrown if we define events without actually binding to the failure events. probably a bug. ¯\_(ツ)_/¯
            _fastZipEvents.FileFailure += (_, e) =>
            {
                // only give a pass to font files (no idea whats wrong with them)
                if (!e.Name.EndsWith(".ttf"))
                    throw e.Exception;

                App.Logger.WriteLine("FastZipEvents::OnFileFailure", $"Failed to extract {e.Name}");
                _packageExtractionSuccess = false;
            };
            _fastZipEvents.DirectoryFailure += (_, e) => throw e.Exception;
            _fastZipEvents.ProcessFile += (_, e) => e.ContinueRunning = !_cancelTokenSource.IsCancellationRequested;

            SetupAppData();
        }

        private void SetupAppData()
        {
            AppData = IsStudioLaunch ? new RobloxStudioData() : new RobloxPlayerData();
            Deployment.BinaryType = AppData.BinaryType;
        }

        private void SetStatus(string message)
        {
            App.Logger.WriteLine("Bootstrapper::SetStatus", message);

            message = message.Replace("{product}", AppData.ProductName);

            if (Dialog is not null)
                Dialog.Message = message;
        }

        // Ticks of the last dispatcher post, shared across all download tasks.
        private long _lastProgressPostTicks;

        private static readonly long ProgressPostIntervalTicks = TimeSpan.FromMilliseconds(100).Ticks;

        /// <summary>
        /// Rate-limited <see cref="UpdateProgressBar"/> for the per-chunk download path. At most
        /// one UI update per 100 ms across every concurrent download, so the byte counters stay
        /// exact while the dispatcher queue stays short.
        /// </summary>
        private void MaybeUpdateProgressBar()
        {
            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref _lastProgressPostTicks);

            if (now - last < ProgressPostIntervalTicks)
                return;

            // Only the task that wins the swap posts, so a burst of chunks across six threads
            // still produces one update.
            if (Interlocked.CompareExchange(ref _lastProgressPostTicks, now, last) != last)
                return;

            UpdateProgressBar();
        }

        private void UpdateProgressBar()
        {
            if (Dialog is null)
                return;

            // Parallel downloads call this from worker threads — bounce to the WPF dispatcher
            // before touching dialog properties (especially TaskbarItemProgressState, which is
            // strictly UI-thread-only). BeginInvoke is fire-and-forget so the download loop
            // doesn't block on the UI.
            //
            // Background priority on purpose: the default (Normal) outranks Render and Input, so a
            // flood of these posts kept the dialog from actually painting.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke((Action)UpdateProgressBar, System.Windows.Threading.DispatcherPriority.Background);
                return;
            }

            // UI progress
            int progressValue = (int)Math.Floor(_progressIncrement * _totalDownloadedBytes);

            // bugcheck: if we're restoring a file from a package, it'll incorrectly increment the progress beyond 100
            // too lazy to fix properly so lol
            progressValue = Math.Clamp(progressValue, 0, ProgressBarMaximum);

            Dialog.ProgressValue = progressValue;

            // taskbar progress
            double taskbarProgressValue = _taskbarProgressIncrement * _totalDownloadedBytes;
            taskbarProgressValue = Math.Clamp(taskbarProgressValue, 0, _taskbarProgressMaximum);

            Dialog.TaskbarProgressValue = taskbarProgressValue;

            // BeastStrap fork: show "X MB / Y MB" next to the progress bar plus a smoothed
            // speed/ETA line. The speed line is what tells you "this is slow but progressing"
            // vs "this is genuinely stuck" — the gap that confused users on USB installs.
            if (_totalPackedBytes > 0 && Dialog is UI.Elements.Bootstrapper.FluentDialog fluent)
            {
                long clampedDownloaded = Math.Clamp(_totalDownloadedBytes, 0, _totalPackedBytes);
                fluent.DownloadSizeText = $"{FormatBytes(clampedDownloaded)} / {FormatBytes(_totalPackedBytes)}";
                fluent.DownloadSpeedText = ComputeSpeedAndEtaText(clampedDownloaded, _totalPackedBytes);
            }
        }

        // Sample bytes-over-time and produce a "3.2 MB/s · ~30s remaining" string.
        // Uses an exponential moving average (alpha 0.3) so the rate is responsive but not jumpy.
        private string ComputeSpeedAndEtaText(long downloaded, long total)
        {
            DateTime now = DateTime.UtcNow;

            if (_speedSampleTime is null)
            {
                _speedSampleTime = now;
                _speedSampleBytes = downloaded;
                return ""; // need a second sample before we can show a rate
            }

            double secs = (now - _speedSampleTime.Value).TotalSeconds;
            if (secs < 0.25)
                return FormatSpeedAndEta(_smoothedBytesPerSec, downloaded, total);

            long deltaBytes = downloaded - _speedSampleBytes;
            if (deltaBytes < 0) deltaBytes = 0;

            double instantBps = deltaBytes / secs;
            // Seed with the first real reading; smooth thereafter.
            _smoothedBytesPerSec = _smoothedBytesPerSec == 0
                ? instantBps
                : (0.3 * instantBps) + (0.7 * _smoothedBytesPerSec);

            _speedSampleTime = now;
            _speedSampleBytes = downloaded;

            return FormatSpeedAndEta(_smoothedBytesPerSec, downloaded, total);
        }

        private static string FormatSpeedAndEta(double bytesPerSec, long downloaded, long total)
        {
            if (bytesPerSec <= 0)
                return ""; // no speed yet, show nothing rather than "0 B/s · forever"

            string speed = $"{FormatBytes((long)bytesPerSec)}/s";

            long remaining = total - downloaded;
            if (remaining <= 0)
                return speed;

            double etaSecs = remaining / bytesPerSec;
            string eta;
            if (etaSecs < 5) eta = "almost done";
            else if (etaSecs < 60) eta = $"~{(int)etaSecs}s remaining";
            else if (etaSecs < 3600) eta = $"~{(int)(etaSecs / 60)}m {(int)(etaSecs % 60)}s remaining";
            else eta = "over 1 hour remaining";

            return $"{speed}  ·  {eta}";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            double n = bytes;
            int u = 0;
            while (n >= 1024 && u < units.Length - 1) { n /= 1024; u++; }
            return $"{n:0.#} {units[u]}";
        }

        private void HandleConnectionError(Exception exception)
        {
            const string LOG_IDENT = "Bootstrapper::HandleConnectionError";

            _noConnection = true;

            App.Logger.WriteLine(LOG_IDENT, "Connectivity check failed");
            App.Logger.WriteException(LOG_IDENT, exception);

            string message = Strings.Dialog_Connectivity_BadConnection;

            if (exception is AggregateException)
                exception = exception.InnerException!;

            // https://gist.github.com/pizzaboxer/4b58303589ee5b14cc64397460a8f386
            if (exception is HttpRequestException && exception.InnerException is null)
                message = String.Format(Strings.Dialog_Connectivity_RobloxDown, "[status.roblox.com](https://status.roblox.com)");

            if (_mustUpgrade)
                message += $"\n\n{Strings.Dialog_Connectivity_RobloxUpgradeNeeded}\n\n{Strings.Dialog_Connectivity_TryAgainLater}";
            else
                message += $"\n\n{Strings.Dialog_Connectivity_RobloxUpgradeSkip}";

            Frontend.ShowConnectivityDialog(
                String.Format(Strings.Dialog_Connectivity_UnableToConnect, "Roblox"), 
                message, 
                _mustUpgrade ? MessageBoxImage.Error : MessageBoxImage.Warning,
                exception);

            if (_mustUpgrade)
                App.Terminate(ErrorCode.ERROR_CANCELLED);
        }
        
        public async Task Run()
        {
            const string LOG_IDENT = "Bootstrapper::Run";

            App.Logger.WriteLine(LOG_IDENT, "Running bootstrapper");

            // this is now always enabled as of v2.8.0
            if (Dialog is not null)
                Dialog.CancelEnabled = true;

            SetStatus(Strings.Bootstrapper_Status_Connecting);

            var connectionResult = await Deployment.InitializeConnectivity();

            App.Logger.WriteLine(LOG_IDENT, "Connectivity check finished");

            if (connectionResult is not null)
                HandleConnectionError(connectionResult);
            
#if (!DEBUG || DEBUG_UPDATER) && !QA_BUILD
            if (App.Settings.Prop.CheckForUpdates && !App.LaunchSettings.UpgradeFlag.Active)
            {
                bool updatePresent = await CheckForUpdates();
                
                if (updatePresent)
                    return;
            }
#endif

            App.AssertWindowsOSVersion();

            // if we dont know our launch type, find out now!
            if (_launchMode == LaunchMode.Unknown)
            {
                await SafeGetLatestVersionInfo();

                if (_launchMode == LaunchMode.Unknown)
                    throw new ApplicationException("Failed to deduce launch type");
            }

            // ensure only one instance of the bootstrapper is running at the time
            // so that we don't have stuff like two updates happening simultaneously

            bool mutexExists = Utilities.DoesMutexExist(MutexName);

            if (mutexExists)
            {
                if (!QuitIfMutexExists)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"{MutexName} mutex exists, waiting...");
                    SetStatus(Strings.Bootstrapper_Status_WaitingOtherInstances);
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, $"{MutexName} mutex exists, exiting!");
                    return;
                }
            }

            // wait for mutex to be released if it's not yet
            await using var mutex = new AsyncMutex(false, MutexName);
            await mutex.AcquireAsync(_cancelTokenSource.Token);

            _mutex = mutex;

            // reload our configs since they've likely changed by now
            if (mutexExists)
            {
                App.Settings.Load();
                App.State.Load();
                AppData.DistributionStateManager.Load();
            }

            await SafeGetLatestVersionInfo();

            CleanupVersionsFolder(); // cleanup after background updater

            bool allModificationsApplied = true;

            if (!_noConnection)
            {
                // v420.20+: when a Versions Manager profile is driving this launch, the
                // "currently installed version" lives on the profile itself rather than
                // the global DistributionState — that way two profiles whose Roblox
                // hashes happen to match still install into separate dirs and the
                // up-to-date check stays accurate per profile.
                //
                // v420.24 fix: the previous null-coalesce (?? on the property value)
                // only fired when the profile object itself was null. A freshly-created
                // profile's InstalledVersionGuid defaults to "" (empty string), which
                // is NOT null, so it would short-circuit installedForThisLaunch to ""
                // and fail the equality check below — triggering a spurious reinstall
                // every single launch (flippi's 2026-05-24 report). Explicit
                // IsNullOrEmpty check fixes the fallback path.
                var activeProfileForCheck = GetActiveProfileForBootstrap();
                string installedForThisLaunch = ResolveInstalledVersionForLaunch(activeProfileForCheck);

                if (installedForThisLaunch != _latestVersionGuid || _mustUpgrade)
                {
                    bool backgroundUpdaterMutexOpen = !App.LaunchSettings.BackgroundUpdaterFlag.Active && Utilities.DoesMutexExist(BackgroundUpdaterMutexName);

                    App.Logger.WriteLine(LOG_IDENT, $"Background updater running: {backgroundUpdaterMutexOpen}");

                    if (backgroundUpdaterMutexOpen && _mustUpgrade)
                    {
                        // I am Forced Upgrade, killer of Background Updates.
                        //
                        // Wait for it to actually die. We used to fire the kill event and
                        // immediately clear the flag, then fall into UpgradeRoblox — while the
                        // other process was still winding down through Cancel(), which recursively
                        // deletes the very version directory this one is about to install into.
                        backgroundUpdaterMutexOpen = !Utilities.KillBackgroundUpdater(BackgroundUpdaterMutexName, TimeSpan.FromSeconds(10));

                        if (backgroundUpdaterMutexOpen)
                            App.Logger.WriteLine(LOG_IDENT, "Background updater didn't exit in time — leaving the upgrade to it rather than racing it.");
                    }
                   
                    if (!backgroundUpdaterMutexOpen)
                    {
                        if (IsEligibleForBackgroundUpdate())
                            StartBackgroundUpdater();
                        else
                            await UpgradeRoblox();
                    }
                }

                if (_cancelTokenSource.IsCancellationRequested)
                    return;

                // Per-profile fast flags: materialise the active Versions Manager profile's
                // flag set into the canonical ClientAppSettings.json that ApplyModifications
                // copies into the install. Keeps the overlay-copy path itself unchanged.
                // Pass the profile resolved for THIS launch so a -versionprofile override
                // gets its own flags, not the global active profile's.
                Utility.FastFlagProfiles.MaterializeActiveToCanonical(activeProfileForCheck);

                // Multi-instance RAM reducer: layer the lean flag set over whatever the active
                // profile materialised, so farm launches run on capped FPS / low textures. No-op
                // for normal single launches and for reducer-off.
                Utility.MultiInstanceRamReducer.LayerOverCanonicalIfActive();

                // we require deployment details for applying modifications for a worst case scenario,
                // where we'd need to restore files from a package that isn't present on disk and needs to be redownloaded
                allModificationsApplied = await ApplyModifications();

                // FullBright can't ride the Modifications overlay — that copies files in, and this
                // works by taking one out. Runs after the overlay so a Roblox update, which restores
                // the texture in a fresh version folder, is picked up on the very next launch.
                //
                // Sits inside the !_noConnection block deliberately: it needs _latestVersionDirectory,
                // which only resolves once we've reached deployment info. So, like every other mod, it
                // no-ops on an offline launch — including the revert. Turning the toggle off then
                // launching offline leaves the texture removed until the next online launch.
                if (!IsStudioLaunch)
                    Utility.FullBright.Apply(_latestVersionDirectory, App.Settings.Prop.EnableFullBright);
            }

            // check registry entries for every launch, just in case the stock bootstrapper changes it back

            if (IsStudioLaunch)
                WindowsRegistry.RegisterStudio();
            else
                WindowsRegistry.RegisterPlayer();

            if (_launchMode != LaunchMode.Player)
                await mutex.ReleaseAsync();

            if (!App.LaunchSettings.NoLaunchFlag.Active && !_cancelTokenSource.IsCancellationRequested)
            {
                if (!App.LaunchSettings.QuietFlag.Active)
                {
                    // show some balloon tips
                    if (!_packageExtractionSuccess)
                        Frontend.ShowBalloonTip(Strings.Bootstrapper_ExtractionFailed_Title, Strings.Bootstrapper_ExtractionFailed_Message, ToolTipIcon.Warning);
                    else if (!allModificationsApplied)
                        Frontend.ShowBalloonTip(Strings.Bootstrapper_ModificationsFailed_Title, Strings.Bootstrapper_ModificationsFailed_Message, ToolTipIcon.Warning);
                }

                await MaybeSelectEmptiestServerAsync();
                StartRoblox();
            }

            await mutex.ReleaseAsync();

            Dialog?.CloseBootstrapper();
        }

        // When "join the emptiest server on launch" is on and this is a plain game launch (not a
        // specific server, VIP, or home screen), find the least-populated public server and rewrite the
        // launch to join it. Best-effort — any failure leaves the launch untouched.
        private async Task MaybeSelectEmptiestServerAsync()
        {
            const string LOG_IDENT = "Bootstrapper::MaybeSelectEmptiestServer";

            if (!App.Settings.Prop.JoinEmptiestServerOnLaunch)
            {
                App.Logger.WriteLine(LOG_IDENT, "Not selecting a server: the 'join emptiest server on launch' setting is off.");
                return;
            }

            App.Logger.WriteLine(LOG_IDENT, "'Join emptiest server on launch' is ON — checking whether this launch can be redirected.");
            App.Logger.WriteLine(LOG_IDENT, $"Launch mode: {_launchMode}. Launch args: {Utility.LogScrubber.ScrubLaunchArgs(_launchCommandLine)}");

            if (_launchMode != LaunchMode.Player)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Not selecting a server: launch mode is {_launchMode}, and only a Player launch can be redirected.");
                return;
            }

            string? notPlain = Utility.LaunchArgsUtility.ExplainNotPlainPlaceJoin(_launchCommandLine);
            if (notPlain is not null)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Not selecting a server: {notPlain}.");
                return;
            }

            long? placeId = Utility.LaunchArgsUtility.TryExtractPlaceId(_launchCommandLine);
            if (placeId is null || placeId <= 0)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Not selecting a server: could not read a usable placeId (got {placeId?.ToString() ?? "none"}).");
                return;
            }

            try
            {
                App.Logger.WriteLine(LOG_IDENT, $"Asking Roblox for the emptiest public server of place {placeId}...");

                var server = await Utility.ServerBrowserClient.GetEmptiestServerAsync(placeId.Value);
                if (server is null || string.IsNullOrEmpty(server.Id))
                {
                    App.Logger.WriteLine(LOG_IDENT, "Not selecting a server: Roblox returned no joinable public server for this place. Launching normally.");
                    return;
                }

                App.Logger.WriteLine(LOG_IDENT, $"Picked server {server.Id} with {server.Playing}/{server.MaxPlayers} players.");

                string rewritten = Utility.LaunchArgsUtility.InjectGameJob(_launchCommandLine, server.Id);

                // InjectGameJob returns its input untouched when the launch args don't match either
                // shape it knows how to rewrite. Claiming success there would send us hunting for a
                // Roblox-side cause when the real failure is right here, so check before believing it.
                if (rewritten == _launchCommandLine)
                {
                    App.Logger.WriteLine(LOG_IDENT, "FAILED to redirect: the launch arguments matched no known rewrite shape (no placelauncherurl and no experiences/start deeplink), so they were left alone. Roblox will pick the server. Please report this log.");
                    return;
                }

                _launchCommandLine = rewritten;
                App.Logger.WriteLine(LOG_IDENT, $"Rewrote launch to join server {server.Id} ({server.Playing}/{server.MaxPlayers}).");
                App.Logger.WriteLine(LOG_IDENT, $"Rewritten launch args: {Utility.LogScrubber.ScrubLaunchArgs(_launchCommandLine)}");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                // leave _launchCommandLine unchanged → normal launch
            }
        }

        private void FetchCurrentChannel()
        {
            // Fork behavior: channel is locked to LIVE. Ignore CLI flags, registry state,
            // and any other override source. See also UpdateChannelRegistry().
            const string LOG_IDENT = "Bootstrapper::FetchCurrentChannel";

            if (_channelFetched)
                return;

            Deployment.Channel = Deployment.DefaultChannel;
            App.Logger.WriteLine(LOG_IDENT, $"Channel forced to {Deployment.DefaultChannel}");
            _channelFetched = true;
        }

        private void UpdateChannelRegistry()
        {
            // Always blank the Roblox-side channel key on launch, then verify the write.
            // Roblox interprets an empty value (or "production") as the LIVE channel.
            // Any non-empty, non-"production" value means some other tool flipped the key;
            // we overwrite it regardless.
            const string LOG_IDENT = "Bootstrapper::UpdateChannelRegistry";
            string subKeyPath = $"SOFTWARE\\ROBLOX Corporation\\Environments\\{AppData.RegistryName}\\Channel";
            const string valueName = "www.roblox.com";

            // Captured so we can forward the most informative failure to the toast instead
            // of the user only seeing "couldn't be verified" with no detail.
            string? lastReason = null;

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using (RegistryKey writeKey = Registry.CurrentUser.CreateSubKey(subKeyPath))
                    {
                        writeKey.SetValueSafe(valueName, "");
                    }

                    string? readBack;
                    using (RegistryKey? verifyKey = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false))
                    {
                        readBack = verifyKey?.GetValue(valueName) as string;
                    }

                    bool locked = string.IsNullOrEmpty(readBack)
                        || string.Equals(readBack, Deployment.DefaultChannel, StringComparison.OrdinalIgnoreCase);

                    if (locked)
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Channel lock verified: LIVE (attempt {attempt})");
                        return;
                    }

                    App.Logger.WriteLine(LOG_IDENT, $"Verification MISMATCH on attempt {attempt}: read back '{readBack}', expected empty or '{Deployment.DefaultChannel}'");
                    lastReason = $"another process wrote '{readBack}' back into the key";
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Registry access failed on attempt {attempt}");
                    App.Logger.WriteException(LOG_IDENT, ex);
                    lastReason = $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            App.Logger.WriteLine(LOG_IDENT, "WARNING: Channel lock could not be verified after retry. Roblox will still launch.");
            Utility.LiveChannelToast.ShowChannelLockFailed(lastReason);
        }

        /// <summary>
        /// Will throw whatever HttpClient can throw
        /// </summary>
        /// <returns></returns>
        private async Task GetLatestVersionInfo()
        {
            const string LOG_IDENT = "Bootstrapper::GetLatestVersionInfo";

            // before we do anything, we need to query our channel
            // if it's set in the launch uri, we need to use it and set the registry key for it
            // else, check if the registry key for it exists, and use it
            FetchCurrentChannel();

            string? newVersionGuid = null;
            Version? newVersion = null;

            // Version-resolution priority:
            //   1. CLI --version flag (session-scoped override)
            //   2. Versions Manager active profile (v420.19+) — preferred when set
            //   3. Settings.UseCustomVersion + CustomVersionGuid (legacy single-pin) — fallback only
            //   4. Fetch latest from clientsettingscdn
            // UpdateChannelRegistry() is called in every branch — channel lock must stay active
            // regardless of which version we're launching.

            bool cliVersion = App.LaunchSettings.VersionFlag.Active && !string.IsNullOrEmpty(App.LaunchSettings.VersionFlag.Data);

            // v420.23: if the active profile is executor-tracked (came from the WEAO
            // dropdown), refresh its VersionGuid from WEAO before resolving. Bounded
            // to 6s so a slow/dead WEAO never blocks launch — we just fall through to
            // the cached value. v420.50.1: the budget also covers a best-effort LIVE
            // hash lookup that feeds the auto-downgrade protection.
            if (_launchMode == LaunchMode.Player && !cliVersion)
                await ExecutorProfileRefresher.RefreshActiveAsync(TimeSpan.FromSeconds(6));

            // Resolve the Versions Manager profile for this launch (honors a per-account
            // -versionprofile override; otherwise the global active profile).
            string? activeProfileGuid = null;
            string? activeProfileName = null;
            var resolvedProfile = GetActiveProfileForBootstrap();
            if (resolvedProfile != null
                && !string.IsNullOrEmpty(resolvedProfile.VersionGuid)
                && Utility.VersionGuidValidator.IsWellFormed(resolvedProfile.VersionGuid))
            {
                activeProfileGuid = resolvedProfile.VersionGuid;
                activeProfileName = resolvedProfile.Name;
            }

            bool pinnedVersion = activeProfileGuid != null
                || (App.Settings.Prop.UseCustomVersion
                    && Utility.VersionGuidValidator.IsWellFormed(App.Settings.Prop.CustomVersionGuid));

            string pinnedGuid = activeProfileGuid ?? App.Settings.Prop.CustomVersionGuid;
            string pinnedSource = activeProfileGuid != null
                ? $"Versions Manager profile '{activeProfileName}'"
                : "Downgrading single-pin";

            // Captured for the downgrade-badge comparison further down. In the no-pin/no-CLI
            // branch we already fetch LIVE via Deployment.GetInfo and reuse that result; in
            // the pin/CLI branches we fetch separately so we can tell whether the pinned hash
            // is genuinely older than LIVE. If the comparison fetch fails we leave this null
            // and the badge stays hidden — better than misclaiming a downgrade.
            string? liveVersionGuid = null;

            if (cliVersion)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Version set to {App.LaunchSettings.VersionFlag.Data} from arguments");
                newVersionGuid = App.LaunchSettings.VersionFlag.Data;
                UpdateChannelRegistry();
            }
            else if (pinnedVersion)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Version pinned to {pinnedGuid} via {pinnedSource}");
                newVersionGuid = pinnedGuid;
                UpdateChannelRegistry();
            }
            else
            {
                ClientVersion clientVersion;

                try
                {
                    clientVersion = await Deployment.GetInfo();
                }
                catch (InvalidChannelException ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Resetting channel from {Deployment.Channel} because {ex.StatusCode}");

                    Deployment.Channel = Deployment.DefaultChannel;
                    clientVersion = await Deployment.GetInfo();
                }

                UpdateChannelRegistry();

                newVersionGuid = clientVersion.VersionGuid;
                newVersion = Utilities.ParseVersionSafe(clientVersion.Version);
                liveVersionGuid = clientVersion.VersionGuid;
            }

            if (liveVersionGuid is null && (cliVersion || pinnedVersion))
            {
                try
                {
                    var liveInfo = await Deployment.GetInfo();
                    liveVersionGuid = liveInfo.VersionGuid;
                    App.Logger.WriteLine(LOG_IDENT, $"LIVE comparison hash: {liveVersionGuid}");
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Could not fetch LIVE hash for downgrade comparison; badge will stay hidden.");
                    App.Logger.WriteException(LOG_IDENT, ex);
                }
            }

            if (newVersionGuid != _latestVersionGuid)
            {
                _latestVersionGuid = newVersionGuid!;
                _latestVersion = newVersion;

                // v420.24: per-profile real dirs at Versions\profile-<id>\ plus a
                // launch-time junction at Versions\version-<active-hash>\ that points
                // at the active profile's dir. Executors still see a standard
                // version-<hash> install path (junction is transparent to most APIs),
                // and same-hash profiles no longer share storage — flippi's wave/syn z
                // file-leak scenario can't happen anymore because each profile has its
                // own real folder. Studio launches stay on the legacy version-hash
                // layout (no profile system there).
                Directory.CreateDirectory(Paths.Versions);

                var activeProfileForLaunch = GetActiveProfileForBootstrap();
                if (activeProfileForLaunch != null)
                {
                    // Park-and-rename, replacing the v420.24 junction. The active profile's install
                    // is moved to a REAL Versions\version-<hash>\ directory and the outgoing profile
                    // is parked at Versions\profile-<id>\, so the client is never launched through a
                    // reparse point — which is what was killing it 41-58s into a session. See
                    // Utility/VersionProfileLayout.cs for the evidence and the full rationale.
                    _latestVersionDirectory = Utility.VersionProfileLayout.EnsureActive(
                        activeProfileForLaunch, _latestVersionGuid);
                }
                else
                {
                    _latestVersionDirectory = Path.Combine(Paths.Versions, _latestVersionGuid);
                }

                // Override AppData.Directory regardless: DistributionState.VersionGuid
                // is global and only updates after a download, so on profile switches
                // (no download needed) it'd still resolve to the previously active
                // profile's hash. Pinning the override here keeps Process.Start and
                // File.Exists honest.
                AppData.InstallDirectoryOverride = _latestVersionDirectory;

                string pkgManifestUrl = Deployment.GetLocation($"/{_latestVersionGuid}-rbxPkgManifest.txt");
                var pkgManifestData = await App.HttpClient.GetStringAsync(pkgManifestUrl);

                _versionPackageManifest = new(pkgManifestData);
            }

            // BeastStrap fork: surface version info + downgrade state on the loading screen.
            if (Dialog is UI.Elements.Bootstrapper.FluentDialog fluent)
            {
                string versionLabel = _latestVersion is not null
                    ? $"Roblox v{_latestVersion} \u00B7 {_latestVersionGuid}"
                    : _latestVersionGuid;
                fluent.VersionInfoText = versionLabel;

                // Only flag as downgraded when we can prove it: there's a CLI/pinned override,
                // we fetched a LIVE hash to compare against, and the launching hash differs.
                // Pinning to the actual LIVE hash (e.g. via "Pin this version" or picking an
                // up-to-date executor) intentionally hides the badge.
                bool launchingOverride = cliVersion || pinnedVersion;
                bool launchingDiffersFromLive = !string.IsNullOrEmpty(liveVersionGuid)
                    && !string.Equals(_latestVersionGuid, liveVersionGuid, StringComparison.OrdinalIgnoreCase);
                fluent.IsDowngraded = launchingOverride && launchingDiffersFromLive;

                // Place info (player launches only): parse placeId from the raw launch args.
                // We don't know the game name without a network call — just show the place id so
                // the user can confirm they're joining the right experience.
                if (!IsStudioLaunch)
                {
                    long? placeId = Utility.LaunchArgsUtility.TryExtractPlaceId(_launchCommandLine);
                    if (placeId.HasValue)
                    {
                        fluent.PlaceInfoText = Utility.StreamMode.IsActive
                            ? Utility.StreamMode.MaskedPlaceInfo
                            : $"Joining Roblox place #{placeId.Value}";
                    }
                }
            }

            // this can happen if version is set through arguments
            if (_launchMode == LaunchMode.Unknown)
            {
                if (_versionPackageManifest.Count != 0)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Identifying launch mode from package manifest");

                    bool isPlayer = _versionPackageManifest.Exists(x => x.Name == "RobloxApp.zip");
                    App.Logger.WriteLine(LOG_IDENT, $"isPlayer: {isPlayer}");

                    _launchMode = isPlayer ? LaunchMode.Player : LaunchMode.Studio;

                    SetupAppData(); // we need to set it up again

                    // lets set the registry now
                    UpdateChannelRegistry();
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, "Could not identify launch mode as package manifest is empty");
                }
            }
        }

        private async Task SafeGetLatestVersionInfo()
        {
            if (!_noConnection)
            {
                try
                {
                    await GetLatestVersionInfo();
                }
                catch (Exception ex)
                {
                    HandleConnectionError(ex);
                }
            }
        }

        private bool IsEligibleForBackgroundUpdate()
        {
            const string LOG_IDENT = "Bootstrapper::IsEligibleForBackgroundUpdate";

            if (App.LaunchSettings.BackgroundUpdaterFlag.Active)
            {
                App.Logger.WriteLine(LOG_IDENT, "Not eligible: Is the background updater process");
                return false;
            }

            if (!App.Settings.Prop.BackgroundUpdatesEnabled)
            {
                App.Logger.WriteLine(LOG_IDENT, "Not eligible: Background updates disabled");
                return false;
            }

            if (_mustUpgrade)
            {
                App.Logger.WriteLine(LOG_IDENT, "Not eligible: Must upgrade is true");
                return false;
            }

            // A background update relies on the new build going into a directory nothing is
            // running from — upstream gets that for free because the old client sits in
            // version-<oldHash>\ and the installer writes version-<newHash>\. Park-and-rename
            // breaks that assumption: the active profile's install is renamed INTO
            // version-<newHash>\ before the version check, so the client this launch is about to
            // start and the directory the updater would extract into are the same folder. The
            // updater deliberately skips the shutdown-and-wipe step, so it would unzip a new
            // RobloxPlayerBeta.exe over one that's mid-session and leave a half-and-half install.
            //
            // This isn't a new restriction in practice — since the layout change, cleanup deleted
            // that directory before we got here, which made _mustUpgrade true and disqualified
            // every profile launch a few lines above. That's now fixed, so state the real reason
            // explicitly instead of relying on a side effect to keep us out of this path.
            if (GetActiveProfileForBootstrap() != null)
            {
                App.Logger.WriteLine(LOG_IDENT, "Not eligible: a Versions Manager profile owns this launch, and its install shares the directory the updater would write to");
                return false;
            }

            // at least 5GB of free space
            const long minimumFreeSpace = 5_000_000_000;
            long space = Filesystem.GetFreeDiskSpace(Paths.Base);
            if (space < minimumFreeSpace)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Not eligible: User has {space} free space, at least {minimumFreeSpace} is required");
                return false;
            }

            if (_latestVersion == default)
            {
                App.Logger.WriteLine(LOG_IDENT, "Not eligible: Latest version is undefined");
                return false;
            }

            Version? currentVersion = Utilities.GetRobloxVersion(AppData);
            if (currentVersion == default)
            {
                App.Logger.WriteLine(LOG_IDENT, "Not eligible: Current version is undefined");
                return false;
            }

            // always normally upgrade for downgrades
            if (currentVersion.Minor > _latestVersion.Minor)
            {
                App.Logger.WriteLine(LOG_IDENT, "Not eligible: Downgrade");
                return false;
            }

            // only background update if we're:
            // - one major update behind
            // - the same major update
            int diff = _latestVersion.Minor - currentVersion.Minor;
            if (diff == 0 || diff == 1)
            {
                App.Logger.WriteLine(LOG_IDENT, "Eligible");
                return true;
            }
            else
            {
                App.Logger.WriteLine(LOG_IDENT, $"Not eligible: Major version diff is {diff}");
                return false;
            }
        }

        private void StartRoblox()
        {
            const string LOG_IDENT = "Bootstrapper::StartRoblox";

            // Privacy mode: wipe Roblox's cookie cache right before the player process spawns.
            // Best-effort, never throws up to the caller — a file-locked cookie file shouldn't
            // prevent a launch.
            if (App.Settings.Prop.EnablePrivacyMode)
            {
                App.Logger.WriteLine(LOG_IDENT, "Privacy mode enabled — truncating RobloxCookies.dat");
                Utility.PrivacyMode.TruncateRobloxCookies();
            }

            // Multi-instance: hold Roblox's single-instance lock BEFORE the client starts.
            // While an BeastStrap process owns it, no client can elect itself the primary
            // instance, which is what triggers "the previous instance will be closed" and
            // kills the older client. Also sweeps the singleton event of any client that
            // became primary earlier (launched while this setting was off), so turning the
            // setting on mid-session works too.
            if (MultiInstanceActive && _launchMode == LaunchMode.Player)
                BeastStrap.Utility.MultiInstance.PrepareForLaunch();

            SetStatus(Strings.Bootstrapper_Status_Starting);

            var startInfo = new ProcessStartInfo()
            {
                FileName = AppData.ExecutablePath,
                Arguments = _launchCommandLine,
                WorkingDirectory = AppData.Directory
            };

            if (_launchMode == LaunchMode.Player && ShouldRunAsAdmin())
            {
                startInfo.Verb = "runas";
                startInfo.UseShellExecute = true;
            }
            else if (_launchMode == LaunchMode.StudioAuth)
            {
                Process.Start(startInfo);
                return;
            }

            string? logFileName = null;

            string rbxDir = Path.Combine(Paths.LocalAppData, "Roblox");
            if (!Directory.Exists(rbxDir))
                Directory.CreateDirectory(rbxDir);

            string rbxLogDir = Path.Combine(rbxDir, "logs");
            if (!Directory.Exists(rbxLogDir))
                Directory.CreateDirectory(rbxLogDir);

            var logWatcher = new FileSystemWatcher()
            {
                Path = rbxLogDir,
                Filter = "*.log",
                EnableRaisingEvents = true
            };

            var logCreatedEvent = new AutoResetEvent(false);

            logWatcher.Created += (_, e) =>
            {
                logWatcher.EnableRaisingEvents = false;
                logFileName = e.FullPath;
                logCreatedEvent.Set();
            };

            // When the launch fails outright, this is the start of the window CrashAnalyzer looks at
            // to decide whether BeastStrap was doing something destructive at the time.
            DateTime launchAttemptUtc = DateTime.UtcNow;

            // v2.2.0 - byfron will trip if we keep a process handle open for over a minute, so we're doing this now
            try
            {
                using var process = Process.Start(startInfo)!;
                _appPid = process.Id;

                // v420.46: window manipulation (custom icon / title / fake borderless) needs
                // the client's main window handle, which the watcher applies. Wait for the
                // window to show up while the process handle is still alive — the window
                // appears within a couple of seconds of the log file, well under the minute
                // byfron tolerance noted above.
                if (App.Settings.Prop.EnableWindowManipulation)
                {
                    while (process.MainWindowHandle == IntPtr.Zero && !process.HasExited)
                        Thread.Sleep(100);

                    _appWindowHandle = process.MainWindowHandle;
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // 1223 = ERROR_CANCELLED, gets thrown if a UAC prompt is cancelled
                return;
            }
            catch (Exception)
            {
                // Attempt a reinstall on next launch by deleting the exe so the package
                // pass redownloads it. Defensive try/catch so a missing exe (or parent
                // dir) doesn't replace the original Process.Start exception with a
                // misleading DirectoryNotFoundException — the user needs to see WHY
                // the launch actually failed.
                try
                {
                    if (File.Exists(AppData.ExecutablePath))
                        File.Delete(AppData.ExecutablePath);
                }
                catch (Exception cleanupEx)
                {
                    App.Logger.WriteException("Bootstrapper::StartRoblox::CleanupDelete", cleanupEx);
                }
                throw;
            }

            App.Logger.WriteLine(LOG_IDENT, $"Started Roblox (PID {_appPid}), waiting for log file");

            // Fork feature: single post-launch toast confirming the LIVE channel.
            // Runs once per launch. Handles its own dispatch and cleanup.
            BeastStrap.Utility.LiveChannelToast.Show();

            // The singleton sweep and the window-tile pass both used to be scheduled here, as
            // fire-and-forget tasks with 3-5 second delays before their first action. This process
            // reaches Environment.Exit roughly 2-4 seconds later, and Environment.Exit does not
            // drain thread-pool work — so the sweep usually got zero probes out of its 45-second
            // budget and the tiler often died mid-sleep, making window tiling look intermittently
            // broken rather than reliably absent. Both now run in the watcher, which lives for the
            // whole play session. See Watcher.Run.

            // Poll for the client's log rather than blocking the whole timeout in one call. A client
            // that starts while another Roblox process already owns ROBLOX_singletonMutex doesn't
            // run: it hands its launch request to that owner and exits within a second or two,
            // writing no log at all. Polling lets us catch that in about two seconds instead of
            // staring at "Starting Roblox..." for the full fifteen.
            var logDeadline = DateTime.UtcNow.AddSeconds(15);
            bool clientExitedEarly = false;

            while (DateTime.UtcNow < logDeadline)
            {
                if (logCreatedEvent.WaitOne(TimeSpan.FromMilliseconds(500)))
                    break;

                if (_appPid != 0 && !IsClientProcessAlive(_appPid))
                {
                    clientExitedEarly = true;
                    break;
                }
            }

            // Our process is gone before any log appeared. Normally that means a running client took
            // the request over, and its own log lands a moment later — that's a successful launch and
            // there's nothing to do. Give it a moment to show up before deciding otherwise.
            if (clientExitedEarly && String.IsNullOrEmpty(logFileName))
            {
                App.Logger.WriteLine(LOG_IDENT, $"PID {_appPid} exited before writing a log — another Roblox instance may have taken the launch. Waiting briefly for its log.");

                if (logCreatedEvent.WaitOne(TimeSpan.FromSeconds(4)))
                {
                    App.Logger.WriteLine(LOG_IDENT, "A log appeared — a running client picked the launch up.");
                }
                else if (_launchMode == LaunchMode.Player
                         && !MultiInstanceActive
                         && !_cancelTokenSource.IsCancellationRequested)
                {
                    // Multi-instance is excluded on purpose. We hold the singleton mutex there, so
                    // no client can be a handoff target and this can't be a swallowed launch — and
                    // its throwaway starter process exits after about a second by design, which
                    // would otherwise look exactly like one and get a second client started.
                    RetrySwallowedLaunch(startInfo, logCreatedEvent);
                }
            }

            if (String.IsNullOrEmpty(logFileName))
            {
                App.Logger.WriteLine(LOG_IDENT, "Unable to identify log file");
                LogRunningRobloxProcesses(LOG_IDENT);
                Frontend.ShowPlayerErrorDialog(clientStartUtc: launchAttemptUtc, clientEndUtc: DateTime.UtcNow);
                return;
            }
            else
            {
                App.Logger.WriteLine(LOG_IDENT, $"Got log file as {logFileName}");
            }

            _mutex?.ReleaseAsync();

            if (IsStudioLaunch)
                return;

            var autoclosePids = new List<int>();

            // launch custom integrations now
            foreach (var integration in App.Settings.Prop.CustomIntegrations)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Launching custom integration '{integration.Name}' ({integration.Location} {integration.LaunchArgs} - autoclose is {integration.AutoClose})");

                int pid = 0;

                try
                {
                    var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = integration.Location,
                        Arguments = integration.LaunchArgs.Replace("\r\n", " "),
                        WorkingDirectory = Path.GetDirectoryName(integration.Location),
                        UseShellExecute = true
                    })!;

                    pid = process.Id;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Failed to launch integration '{integration.Name}'!");
                    App.Logger.WriteLine(LOG_IDENT, ex.Message);
                }

                if (integration.AutoClose && pid != 0)
                    autoclosePids.Add(pid);
            }

            // v420.23: always spawn the watcher so RobloxPlayerBeta never gets left
            // running in the background after the user closes the window. Pre-v420.23
            // this only ran when EnableActivityTracking was on (or autoclose pids
            // existed), which meant users without activity tracking enabled saw the
            // Roblox process zombie out in Task Manager.
            {
                // Scoped to the client we just started, not to the machine. The lock used to be
                // the bare name "Watcher", and the watcher process holds it for the whole play
                // session — so with one client already running, every later launch waited the full
                // 5 seconds, failed to acquire, and spawned no watcher at all. That is the exact
                // opposite of what the comment above promises, and it hit every Multi Instance
                // client after the first: no crash detection, no autoclose cleanup.
                using var ipl = new InterProcessLock($"Watcher-{_appPid}", TimeSpan.FromSeconds(5));

                var watcherData = new WatcherData
                {
                    ProcessId = _appPid,
                    LogFile = logFileName,
                    AutoclosePids = autoclosePids,
                    Handle = _appWindowHandle.ToInt64()
                };

                string watcherDataArg = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(watcherData)));

                string args = $"-watcher \"{watcherDataArg}\"";

                if (App.LaunchSettings.TestModeFlag.Active)
                    args += " -testmode";

                // Propagate multi-instance so the watcher (the longest-lived process this
                // session) keeps holding Roblox's single-instance lock — even for account
                // launches that only set the flag and not the global toggle.
                if (MultiInstanceActive)
                    args += " -multiinstance";

                if (ipl.IsAcquired)
                    Process.Start(Paths.Process, args);
            }

            // allow for window to show, since the log is created pretty far beforehand
            Thread.Sleep(1000);
        }

        // True while the process we started is still alive. Checked by PID against the running
        // clients instead of by holding a Process handle: the v2.2.0 note in StartRoblox applies,
        // and this is called on a loop.
        private bool IsClientProcessAlive(int pid)
        {
            bool alive = false;

            foreach (var process in Process.GetProcessesByName(AppData.ProcessName))
            {
                if (process.Id == pid)
                    alive = true;

                process.Dispose();
            }

            return alive;
        }

        // Recovery for a launch that a pre-existing Roblox process accepted and then did nothing
        // with. Roblox leaves a tray-mode client behind for a few minutes after the user closes the
        // game — its log carries userAgent "AppState/TrayMode" and it has no window — and that
        // process owns ROBLOX_singletonMutex plus ROBLOX_singletonEvent. Any client started while
        // it's there signals the event, exits immediately, and leaves the tray process to do the
        // work. If that process is stuck (ours re-stamps the LIVE channel on every launch, which
        // sends its updater off chasing a reinstall it can't run) the request evaporates: no game,
        // no log, and the dialog sits on "Starting Roblox..." until the user gives up.
        //
        // Confirmed from a 2026-07-24 diagnostics bundle — two launches 13 seconds apart both died
        // without writing a log while a tray-mode client from 40 seconds earlier stayed alive, and
        // the same launch worked once that process timed itself out.
        //
        // Closing the singleton event is what the Multi Instance path already does before every
        // launch, and it's non-destructive: nothing is killed, the stalled process just stops being
        // a handoff target so the client we start next runs on its own.
        private void RetrySwallowedLaunch(ProcessStartInfo startInfo, AutoResetEvent logCreatedEvent)
        {
            const string LOG_IDENT = "Bootstrapper::RetrySwallowedLaunch";

            LogRunningRobloxProcesses(LOG_IDENT);

            // Only ever retry into an empty room. A client that could legitimately have taken this
            // launch has a window of its own, and a client still opening one has already written its
            // log seconds ago — so if anything windowed is running, assume the handoff worked and we
            // just didn't see the log, rather than risk starting a second client on top of a session
            // the user is playing. Note this only decides whether to RETRY: nothing is ever killed
            // here, unlike v420.23's window-handle check that closed live sessions.
            if (AnyRobloxWindowOpen())
            {
                App.Logger.WriteLine(LOG_IDENT, "A windowed Roblox client is running — leaving the launch to it rather than starting another.");
                return;
            }

            App.Logger.WriteLine(LOG_IDENT, "Launch was handed to a windowless Roblox process that never started the game — clearing the handoff and retrying once.");

            if (Utility.MultiInstance.ClearSingletonEvents() == 0)
                App.Logger.WriteLine(LOG_IDENT, "No singleton handle was closed; retrying anyway.");

            try
            {
                using var process = Process.Start(startInfo)!;
                _appPid = process.Id;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                return;
            }

            App.Logger.WriteLine(LOG_IDENT, $"Restarted Roblox (PID {_appPid}), waiting for log file");

            App.Logger.WriteLine(LOG_IDENT, logCreatedEvent.WaitOne(TimeSpan.FromSeconds(15))
                ? "Retry produced a log — the launch went through."
                : "Retry produced no log either.");
        }

        // True when any running Roblox client owns a top-level window, i.e. someone is looking at a
        // Roblox window right now. Used only to hold the retry back, never to close anything.
        private bool AnyRobloxWindowOpen()
        {
            bool windowed = false;

            foreach (var process in Process.GetProcessesByName(AppData.ProcessName))
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                        windowed = true;
                }
                catch (Exception)
                {
                    // Can't inspect it — assume it's a real client and stay out of the way.
                    windowed = true;
                }
                finally
                {
                    process.Dispose();
                }
            }

            return windowed;
        }

        // Dump the Roblox processes that are alive right now. When a launch produces no log this is
        // what separates "Roblox itself failed to start" from "something already held the singleton
        // and swallowed us", and the logs recorded neither before.
        private void LogRunningRobloxProcesses(string logIdent)
        {
            try
            {
                var lines = new List<string>();

                foreach (string name in new[] { AppData.ProcessName, "RobloxCrashHandler" })
                {
                    foreach (var process in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            string window = process.MainWindowHandle == IntPtr.Zero ? "no window" : "windowed";
                            lines.Add($"{name} pid={process.Id} up={(DateTime.Now - process.StartTime).TotalSeconds:F0}s {window}");
                        }
                        catch (Exception)
                        {
                            lines.Add($"{name} pid={process.Id} (details unavailable)");
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    }
                }

                App.Logger.WriteLine(logIdent, lines.Count == 0
                    ? "No Roblox processes are running."
                    : "Roblox processes running: " + String.Join(", ", lines));
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(logIdent, ex);
            }
        }

        private bool ShouldRunAsAdmin()
        {
            foreach (var root in WindowsRegistry.Roots)
            {
                using var key = root.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers");

                if (key is null)
                    continue;

                string? flags = (string?)key.GetValue(AppData.ExecutablePath);

                if (flags is not null && flags.Contains("RUNASADMIN", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public void Cancel()
        {
            const string LOG_IDENT = "Bootstrapper::Cancel";

            if (_cancelTokenSource.IsCancellationRequested)
                return;

            App.Logger.WriteLine(LOG_IDENT, "Cancelling launch...");

            _cancelTokenSource.Cancel();

            if (Dialog is not null)
                Dialog.CancelEnabled = false;

            if (_isInstalling)
            {
                try
                {
                    // clean up install — junction-aware: clear the target's contents
                    // rather than deleting the directory itself (would unlink the
                    // junction). See v420.25 notes in UpgradeRoblox.
                    if (Directory.Exists(_latestVersionDirectory))
                    {
                        if (Utility.VersionJunctionManager.IsJunction(_latestVersionDirectory))
                            ClearJunctionTargetContents(_latestVersionDirectory);
                        else
                            Directory.Delete(_latestVersionDirectory, true);
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Could not fully clean up installation!");
                    App.Logger.WriteException(LOG_IDENT, ex);
                }
            }
            else if (_appPid != 0)
            {
                try
                {
                    using var process = Process.GetProcessById(_appPid);
                    process.Kill();
                }
                catch (Exception ex)
                {
                    // Best-effort kill of the Roblox process we spawned. Failures here are
                    // usually "process already exited" (ArgumentException) — benign but log
                    // them so a real Kill failure stops being invisible during diagnostics.
                    App.Logger.WriteException("Bootstrapper::CancelKill", ex);
                }
            }

            Dialog?.CloseBootstrapper();

            App.SoftTerminate(ErrorCode.ERROR_CANCELLED);
        }
#endregion

        #region App Install
        private async Task<bool> CheckForUpdates()
        {
            const string LOG_IDENT = "Bootstrapper::CheckForUpdates";

            // Two separate questions, and conflating them was a real bug.
            //
            //   CAN WE CHECK?    Always. It costs one HTTP call and it's how the user finds out a
            //                    fix exists.
            //   CAN WE REPLACE?  Only when we're the sole BeastStrap process (nothing else has
            //                    the exe open) and we're not running in place from a portable
            //                    folder.
            //
            // Both used to be gated on the replace conditions, which meant portable users and —
            // far worse — every Multi Instance user never even ran the check. A multi-account
            // session always has a launcher plus a watcher alive, so the process count is never 1.
            // A 2026-07-27 crash report came from a v420.40 install that had been logging
            // "aborting update check" on every launch while sitting on an already-fixed crash bug.
            bool canReplaceExe = true;

            if (App.IsPortableMode)
            {
                App.Logger.WriteLine(LOG_IDENT, "Portable mode: will check for updates but won't auto-replace the exe.");
                canReplaceExe = false;
            }
            else if (Process.GetProcessesByName(App.ProjectName).Length > 1)
            {
                App.Logger.WriteLine(LOG_IDENT, "More than one BeastStrap instance running: will check for updates but won't auto-replace the exe this session.");
                canReplaceExe = false;
            }

            App.Logger.WriteLine(LOG_IDENT, "Checking for updates...");

#if !DEBUG_UPDATER
            var releaseInfo = await App.GetLatestRelease();

            if (releaseInfo is null)
                return false;

            VersionComparison versionComparison;
            try
            {
                versionComparison = Utilities.CompareVersions(App.Version, releaseInfo.TagName);
            }
            catch (Exception ex)
            {
                // Don't let a version-string parse failure block launch. Skip the update check
                // this session and move on — users can still manually update from the GitHub release.
                App.Logger.WriteException(LOG_IDENT, ex);
                App.Logger.WriteLine(LOG_IDENT, $"Update check aborted: couldn't compare '{App.Version}' with '{releaseInfo.TagName}'. Continuing launch.");
                return false;
            }

            // Skip update if our local version is already at or ahead of GitHub's latest.
            // The previous condition gated Equal on IsProductionBuild, which meant
            // locally-published builds with the same version as GitHub got force-replaced
            // by the GitHub release on every launch — making iterative dev impossible.
            if (versionComparison == VersionComparison.Equal || versionComparison == VersionComparison.GreaterThan)
            {
                App.Logger.WriteLine(LOG_IDENT, "No updates found");
                return false;
            }

            // There IS an update, we just can't install it from this process. Tell them rather
            // than returning silently — this is the branch every Multi Instance user lands on.
            if (!canReplaceExe)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Update {releaseInfo.TagName} available but this session can't replace the exe. Notifying instead.");
                Utility.UpdateMonitor.NotifyUpdateAvailable(releaseInfo.TagName);
                return false;
            }

            if (Dialog is not null)
                Dialog.CancelEnabled = false;

            string version = releaseInfo.TagName;
#else
            string version = App.Version;
#endif

            SetStatus(Strings.Bootstrapper_Status_UpgradingBloxstrap);

            try
            {
#if DEBUG_UPDATER
                string downloadLocation = Path.Combine(Paths.TempUpdates, "BeastStrap.exe");

                Directory.CreateDirectory(Paths.TempUpdates);

                File.Copy(Paths.Process, downloadLocation, true);
#else
                // Pick the .exe asset explicitly. GitHub returns assets in upload order, which
                // can put the portable zip first — blindly grabbing Assets[0] downloads the
                // wrong artifact and Process.Start fails on the zip. This bit users coming
                // from v420.1/2/3 trying to auto-update.
                var asset = releaseInfo.Assets?.FirstOrDefault(a =>
                    a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

                if (asset is null)
                {
                    App.Logger.WriteLine(LOG_IDENT, "No .exe asset on the latest release — cannot auto-update.");
                    return false;
                }

                string downloadLocation = Path.Combine(Paths.TempUpdates, asset.Name);

                Directory.CreateDirectory(Paths.TempUpdates);

                App.Logger.WriteLine(LOG_IDENT, $"Downloading {releaseInfo.TagName}...");

                // Three bugs used to live in this block, all of which AppUpdater already solved for
                // the menu-open path and none of which were back-ported:
                //
                //   1. `GetAsync` with no HttpCompletionOption buffers the ENTIRE ~160 MB installer
                //      inside the await, under App.HttpClient's global 30-second timeout — so
                //      launch-path auto-update simply could not finish on a slow connection.
                //   2. No status check. A 404 or a Cloudflare error page was written to disk as if
                //      it were the installer.
                //   3. `if (!File.Exists(downloadLocation))` then skipped the download forever,
                //      and nothing ever prunes Paths.TempUpdates — so one bad response wedged
                //      launch-path auto-update permanently.
                //
                // Stream it properly, verify it, and replace any stale file.
                if (File.Exists(downloadLocation))
                    File.Delete(downloadLocation);

                using (var response = await App.HttpClient.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, _cancelTokenSource.Token))
                {
                    response.EnsureSuccessStatusCode();

                    long? expected = response.Content.Headers.ContentLength;

                    await using (var fileStream = new FileStream(downloadLocation, FileMode.Create, FileAccess.Write))
                        await response.Content.CopyToAsync(fileStream, _cancelTokenSource.Token);

                    long actual = new FileInfo(downloadLocation).Length;

                    if (expected is not null && actual != expected)
                    {
                        File.Delete(downloadLocation);
                        throw new IOException($"Update download was truncated ({actual} of {expected} bytes).");
                    }
                }
#endif

                App.Logger.WriteLine(LOG_IDENT, $"Starting {version}...");

                ProcessStartInfo startInfo = new()
                {
                    FileName = downloadLocation,
                };

                startInfo.ArgumentList.Add("-upgrade");

                foreach (string arg in App.LaunchSettings.Args)
                    startInfo.ArgumentList.Add(arg);

                if (_launchMode == LaunchMode.Player && !startInfo.ArgumentList.Contains("-player"))
                    startInfo.ArgumentList.Add("-player");
                else if (_launchMode == LaunchMode.Studio && !startInfo.ArgumentList.Contains("-studio"))
                    startInfo.ArgumentList.Add("-studio");

                App.Settings.Save();

                // Handoff lock: the new exe's Installer.HandleUpgrade waits on "AutoUpdater"
                // (5s) before copying itself over the installed exe. Held until this process
                // exits (the OS abandons it then, which is the waiter's go signal). Rooted in
                // a static so the GC can't close the handle early. Deliberately never disposed.
                _upgradeHandoffLock = new InterProcessLock("AutoUpdater");

                Process.Start(startInfo);

                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "An exception occurred when running the auto-updater");
                App.Logger.WriteException(LOG_IDENT, ex);

                // Same idea as the menu-open update path — include the actual reason so the
                // user has something to act on instead of "auto-update failed, sorry".
                string reasonLine = $"Reason: {ex.GetType().Name}: {ex.Message}";

                Frontend.ShowMessageBox(
                    string.Format(Strings.Bootstrapper_AutoUpdateFailed, version)
                        + "\n\n" + reasonLine
                        + "\n\nOpening the GitHub releases page so you can grab the installer manually.",
                    MessageBoxImage.Information
                );

                Utilities.ShellExecute(App.ProjectDownloadLink);
            }

            return false;
        }
        #endregion

        #region Roblox Install
        private static bool TryDeleteRobloxInDirectory(string dir)
        {
            // check if the roblox executable is present in the directory
            string clientPath = Path.Combine(dir, "RobloxPlayerBeta.exe");
            if (!File.Exists(clientPath))
            {
                clientPath = Path.Combine(dir, "RobloxStudioBeta.exe");
                if (!File.Exists(clientPath))
                    return true; // ok???
            }

            try
            {
                File.Delete(clientPath);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void CleanupVersionsFolder()
        {
            const string LOG_IDENT = "Bootstrapper::CleanupVersionsFolder";

            if (App.LaunchSettings.BackgroundUpdaterFlag.Active)
            {
                App.Logger.WriteLine(LOG_IDENT, "Background updater tried to cleanup, stopping!");
                return;
            }

            // Everything below decides what to DELETE by asking which profiles exist. If Settings
            // couldn't be read, Prop is a factory-default instance holding no profiles at all, and
            // that reads as "the user deleted every profile" — so the sweep would erase every
            // profile-<id> install (~1 GB each) because a file was locked for a moment by antivirus,
            // a sync client or the search indexer. Refuse to run rather than GC against a list we
            // know is not the user's. The next launch that reads the file cleanly does the cleanup
            // instead; deferring it only costs disk space, whereas being wrong here is unrecoverable.
            if (App.Settings.LoadFailed)
            {
                App.Logger.WriteLine(LOG_IDENT, "Settings failed to load, so the profile list is not trustworthy. Skipping cleanup to avoid deleting installs that are still referenced.");
                return;
            }

            var profileIdsForParked = new HashSet<string>(
                App.Settings.Prop.VersionProfiles.Select(p => p.Id),
                StringComparer.OrdinalIgnoreCase);

            // BEFORE the Versions early-return below. The parked root is a separate directory and is
            // the only thing that reclaims a deleted profile's install (~3 GB each), so it must not
            // be skipped just because Versions happens to be missing.
            CleanupParkedVersionsFolder(profileIdsForParked);

            if (!Directory.Exists(Paths.Versions))
            {
                App.Logger.WriteLine(LOG_IDENT, "Versions directory does not exist, skipping cleanup.");
                return;
            }

            // Entries that can live under Paths.Versions:
            //   - profile-<id>\         : LEGACY parked install. These now live in
            //                             Paths.ParkedVersions, but one can still be here if the
            //                             startup migration couldn't move it. Keep while a
            //                             VersionProfile with that id exists — see the warning on
            //                             the branch below, removing it destroys user installs.
            //                             CleanupParkedVersionsFolder handles the new location.
            //   - version-<hash>\       : either a junction (active profile's
            //                             facade — keep if its hash matches some
            //                             profile.VersionGuid AND the target dir
            //                             exists), or a real dir (Studio install,
            //                             or the legacy player install for users
            //                             who never opened the Versions Manager).
            //   - version-<hash>.orphan-<utc>\ : v420.24-era leftover from when
            //                             a real dir at the version path got
            //                             set aside instead of adopted. Safe to
            //                             auto-delete from v420.27 onward —
            //                             v420.25+ no longer creates new ones.
            var profileIds = new HashSet<string>(
                App.Settings.Prop.VersionProfiles.Select(p => p.Id),
                StringComparer.OrdinalIgnoreCase);
            // Keep any version-<hash> dir/junction a profile either PINS (VersionGuid)
            // or currently HAS INSTALLED (InstalledVersionGuid). The built-in "Latest
            // LIVE" profile has an empty VersionGuid (it always tracks current LIVE) but
            // a populated InstalledVersionGuid — without including the latter, its active
            // junction was treated as unreferenced and pruned every launch, which made
            // the exe path vanish and forced a full re-extract on every launch for users
            // whose only profile is the built-in LIVE one. (Confirmed via laptop logs
            // 2026-06-01: "Pruned stale junction version-<hash>" each launch.)
            var profileVersionGuids = new HashSet<string>(
                App.Settings.Prop.VersionProfiles
                    .SelectMany(p => new[] { p.VersionGuid, p.InstalledVersionGuid })
                    .Where(g => !string.IsNullOrEmpty(g)),
                StringComparer.OrdinalIgnoreCase);

            foreach (string dir in Directory.GetDirectories(Paths.Versions))
            {
                string dirName = Path.GetFileName(dir);

                // Dot-prefixed names are reserved for our own in-flight staging directories, and
                // must never be pruned. Nothing creates one yet — this guard ships ahead of the
                // layout migration on purpose, so it's already in the field before any build starts
                // producing them.
                //
                // Without it a staging dir falls through every branch below (it isn't ".orphan-",
                // doesn't start with "profile-", isn't a junction, isn't the current state and isn't
                // referenced by a profile) and lands on the Directory.Delete(dir, true) at the bottom
                // of this loop — which would take the user's entire Roblox install with it, mid-move.
                // Same failure class as the two regressions already documented above.
                if (dirName.StartsWith('.'))
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Skipping in-flight staging directory {dirName} — not ours to prune.");
                    continue;
                }

                if (dirName.Contains(".orphan-"))
                {
                    // v420.27+: auto-delete v420.24's orphan-* leftovers. v420.25+
                    // no longer creates them, so anything here is from an upgrade
                    // and is known-safe to remove (documented as such in the
                    // v420.25 release notes — users can free a few GB).
                    if (!TryDeleteRobloxInDirectory(dir))
                        continue;

                    try
                    {
                        Directory.Delete(dir, true);
                        App.Logger.WriteLine(LOG_IDENT, $"Deleted orphan leftover {dirName} (v420.24 cleanup)");
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Failed to delete orphan {dir}");
                        App.Logger.WriteException(LOG_IDENT, ex);
                    }
                    continue;
                }

                // ⚠️ KEEP THIS BRANCH even though parked installs now live in Paths.ParkedVersions.
                // A legacy directory left here by an interrupted migration (one was locked, the user
                // rolled back and forward) is not dot-prefixed, has no ".orphan-", is not a junction
                // and is not a version hash — so with this branch gone it would match none of the
                // keep-flags below and fall straight into the Directory.Delete at the bottom of this
                // loop, destroying a profile's whole install. VersionProfileLayout.FindParked still
                // resolves these, so they stay usable until the migration gets another go.
                if (dirName.StartsWith("profile-", StringComparison.OrdinalIgnoreCase))
                {
                    string profileId = dirName.Substring("profile-".Length);
                    if (profileIds.Contains(profileId))
                    {
                        // Keep silently — common case, no need to spam the log.
                        continue;
                    }

                    if (!TryDeleteRobloxInDirectory(dir))
                        continue;

                    try
                    {
                        Directory.Delete(dir, true);
                        App.Logger.WriteLine(LOG_IDENT, $"Deleted {dirName} (its profile was removed)");
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Failed to delete {dir}");
                        App.Logger.WriteException(LOG_IDENT, ex);
                    }
                    continue;
                }

                bool isJunction = VersionJunctionManager.IsJunction(dir);
                bool referencedByProfile = profileVersionGuids.Contains(dirName);
                bool isCurrentState = dirName == App.PlayerState.Prop.VersionGuid
                                       || dirName == App.StudioState.Prop.VersionGuid;

                // The install VersionProfileLayout unparked for THIS launch, moments ago. It has to
                // be checked separately because on a Roblox version bump none of the three flags
                // above cover it: the profile still records the previous hash in
                // InstalledVersionGuid (that's only stamped once an upgrade succeeds) and
                // Player/StudioState still hold the old hash too. So the directory the layout had
                // just renamed the user's entire install into matched nothing and fell straight
                // through to the Directory.Delete at the bottom of this loop.
                //
                // Under the old junction layout that only cost a relink ("Pruned stale junction
                // version-<hash>" in every version-bump log). Under park-and-rename the same path
                // deletes ~1GB of real files before the upgrade has downloaded a single byte, which
                // means a cancelled or failed download leaves the user with no client at all, and
                // _mustUpgrade is left permanently true until a full reinstall completes.
                // UpgradeRoblox already clears this directory itself, after it has shut Roblox down
                // and put the dialog into its upgrade state — that is where the delete belongs.
                bool isActiveInstall = !string.IsNullOrEmpty(App.State.Prop.ActiveInstallVersionGuid)
                                       && dirName.Equals(App.State.Prop.ActiveInstallVersionGuid, StringComparison.OrdinalIgnoreCase);

                if (isJunction)
                {
                    // Junctions are the v420.24 layout and are no longer created — launching a
                    // client through a reparse point is what Hyperion was killing sessions over.
                    // Unlink any that survive from an older install, unconditionally. This only
                    // removes the link; the profile-<id> directory it pointed at stays put, already
                    // in the parked shape the new layout expects, so no data moves and the
                    // migration costs nothing. See Utility/VersionProfileLayout.cs.
                    if (VersionJunctionManager.DeleteJunction(dir))
                        App.Logger.WriteLine(LOG_IDENT, $"Removed legacy junction {dirName} (migrated to the park-and-rename layout)");

                    continue;
                }

                // Real version-<hash>\ dir. Could be Studio, the legacy Player
                // install, or a v420.23 leftover we couldn't adopt at launch.
                if (isCurrentState || referencedByProfile || isActiveInstall)
                {
                    if (isActiveInstall && !isCurrentState && !referencedByProfile)
                        App.Logger.WriteLine(LOG_IDENT, $"Keeping {dirName} (the active profile's install was just unparked here for this launch)");
                    else if (referencedByProfile && !isCurrentState)
                        App.Logger.WriteLine(LOG_IDENT, $"Keeping {dirName} (referenced by a Versions Manager profile — will adopt on its next launch)");
                    continue;
                }

                if (!TryDeleteRobloxInDirectory(dir))
                    continue;

                try
                {
                    Directory.Delete(dir, true);
                    App.Logger.WriteLine(LOG_IDENT, $"Deleted orphan {dirName}");
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Failed to delete {dir}");
                    App.Logger.WriteException(LOG_IDENT, ex);
                }
            }
        }

        /// <summary>
        /// Reclaims parked installs whose profile the user has deleted.
        /// </summary>
        /// <remarks>
        /// Parked installs live out of Versions, and this is the only thing that frees a removed
        /// profile's install — roughly 3 GB each. Deliberately far more timid than the Versions
        /// sweep above: it deletes ONLY <c>profile-&lt;id&gt;</c> directories whose id no longer
        /// matches a profile. Anything else it does not recognise is left alone rather than falling
        /// through to a catch-all delete, because there is no legitimate reason for this folder to
        /// contain something we did not put there.
        /// </remarks>
        private static void CleanupParkedVersionsFolder(HashSet<string> profileIds)
        {
            const string LOG_IDENT = "Bootstrapper::CleanupParkedVersionsFolder";

            try
            {
                if (string.IsNullOrEmpty(Paths.ParkedVersions) || !Directory.Exists(Paths.ParkedVersions))
                    return;

                foreach (string dir in Directory.GetDirectories(Paths.ParkedVersions))
                {
                    string dirName = Path.GetFileName(dir);

                    // Dot-prefixed are our own set-aside copies (.stale-*). Never automatic — the
                    // user is meant to be able to look at them and recover files.
                    if (dirName.StartsWith('.'))
                        continue;

                    if (!dirName.StartsWith("profile-", StringComparison.OrdinalIgnoreCase))
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Leaving unrecognised entry {dirName} alone.");
                        continue;
                    }

                    if (profileIds.Contains(dirName.Substring("profile-".Length)))
                        continue; // still owned, common case

                    if (!TryDeleteRobloxInDirectory(dir))
                        continue;

                    try
                    {
                        Directory.Delete(dir, true);
                        App.Logger.WriteLine(LOG_IDENT, $"Deleted parked {dirName} (its profile was removed)");
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Failed to delete {dir}");
                        App.Logger.WriteException(LOG_IDENT, ex);
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        private void MigrateCompatibilityFlags()
        {
            const string LOG_IDENT = "Bootstrapper::MigrateCompatibilityFlags";

            string oldClientLocation = Path.Combine(Paths.Versions, AppData.DistributionState.VersionGuid, AppData.ExecutableName);
            string newClientLocation = Path.Combine(_latestVersionDirectory, AppData.ExecutableName);

            // move old compatibility flags for the old location
            using RegistryKey appFlagsKey = Registry.CurrentUser.CreateSubKey($"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers");
            string? appFlags = appFlagsKey.GetValue(oldClientLocation) as string;

            if (appFlags is not null)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Migrating app compatibility flags from {oldClientLocation} to {newClientLocation}...");
                appFlagsKey.SetValueSafe(newClientLocation, appFlags);
                appFlagsKey.DeleteValueSafe(oldClientLocation);
            }
        }

        private void KillRobloxInstances()
        {
            const string LOG_IDENT = "Bootstrapper::KillRobloxInstances";

            List<Process> processes = new List<Process>();
            processes.AddRange(Process.GetProcessesByName(AppData.ProcessName));
            processes.AddRange(Process.GetProcessesByName("RobloxCrashHandler")); // roblox studio doesnt depend on crash handler being open, so this should be fine

            var killed = new List<int>();

            foreach (Process process in processes)
            {
                try
                {
                    process.Kill();
                    killed.Add(process.Id);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Failed to close process {process.Id}");
                    App.Logger.WriteException(LOG_IDENT, ex);
                }
            }

            // Log the SUCCESS path, not just failures. Crash attribution needs to know whether we
            // actually killed anyone, and until now a successful kill was completely silent while
            // only failures were recorded — exactly backwards for a rule whose whole job is to
            // detect kills. With nothing real to match on, that rule had been keyed to the
            // "Shutting down" status label instead and fired on every routine upgrade, telling
            // users we had killed their client when we had killed nothing at all.
            //
            // CrashRules matches on this line. Keep the wording and the PID list.
            if (killed.Count > 0)
                App.Logger.WriteLine(LOG_IDENT, $"Killed {killed.Count} Roblox process(es): {string.Join(", ", killed)}");
            else
                App.Logger.WriteLine(LOG_IDENT, "No Roblox processes were running, nothing to close");
        }

        private async Task GracefullyCloseRobloxInstances()
        {
            const string LOG_IDENT = "Bootstrapper::GracefullyCloseRobloxInstances";

            while (true)
            {
                Process[] processes = Process.GetProcessesByName(AppData.ProcessName);
                if (processes.Length == 0)
                    break;

                foreach (Process process in processes)
                {
                    try
                    {
                        process.CloseMainWindow();
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Failed to close process {process.Id}");
                        App.Logger.WriteException(LOG_IDENT, ex);
                    }
                }

                try
                {
                    await Task.Delay(1000, _cancelTokenSource.Token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }

        private async Task UpgradeRoblox()
        {
            const string LOG_IDENT = "Bootstrapper::UpgradeRoblox";

            Directory.CreateDirectory(Paths.Base);
            Directory.CreateDirectory(Paths.Downloads);
            Directory.CreateDirectory(Paths.Versions);

            _isInstalling = true;

            if (!App.LaunchSettings.BackgroundUpdaterFlag.Active)
            {
                SetStatus(Strings.Bootstrapper_Status_ShuttingDown);

                if (IsStudioLaunch)
                    await GracefullyCloseRobloxInstances();
                else
                    KillRobloxInstances();

                if (_cancelTokenSource.IsCancellationRequested)
                    return;

                // get a fully clean install
                if (Directory.Exists(_latestVersionDirectory))
                {
                    try
                    {
                        if (Utility.VersionJunctionManager.IsJunction(_latestVersionDirectory))
                        {
                            // v420.25 fix: _latestVersionDirectory is a junction (set up
                            // by GetLatestVersionInfo) — Directory.Delete on a junction
                            // would unlink it, then the CreateDirectory below would put
                            // a *real* dir at the junction path while the profile dir
                            // stays empty. (flippi's 2026-05-24 reproduction.) Clear the
                            // junction's target contents instead so the junction stays
                            // intact and the next install lands in the profile dir as
                            // intended.
                            ClearJunctionTargetContents(_latestVersionDirectory);
                        }
                        else
                        {
                            Directory.Delete(_latestVersionDirectory, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Failed to clear the latest version directory");
                        App.Logger.WriteException(LOG_IDENT, ex);
                    }
                }
            }

            if (String.IsNullOrEmpty(AppData.DistributionState.VersionGuid))
                SetStatus(Strings.Bootstrapper_Status_Installing);
            else
                SetStatus(Strings.Bootstrapper_Status_Upgrading);

            Directory.CreateDirectory(_latestVersionDirectory);

            var cachedPackageHashes = Directory.GetFiles(Paths.Downloads).Select(x => Path.GetFileName(x));

            // package manifest states packed size and uncompressed size in exact bytes
            int totalSizeRequired = 0;

            // packed size only matters if we don't already have the package cached on disk
            totalSizeRequired += _versionPackageManifest.Where(x => !cachedPackageHashes.Contains(x.Signature)).Sum(x => x.PackedSize);
            totalSizeRequired += _versionPackageManifest.Sum(x => x.Size);
            
            if (Filesystem.GetFreeDiskSpace(Paths.Base) < totalSizeRequired)
            {
                Frontend.ShowMessageBox(Strings.Bootstrapper_NotEnoughSpace, MessageBoxImage.Error);
                App.Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
                return;
            }

            if (Dialog is not null)
            {
                Dialog.ProgressStyle = ProgressBarStyle.Continuous;
                Dialog.TaskbarProgressState = TaskbarItemProgressState.Normal;

                Dialog.ProgressMaximum = ProgressBarMaximum;

                // compute total bytes to download
                int totalPackedSize = _versionPackageManifest.Sum(package => package.PackedSize);
                _totalPackedBytes = totalPackedSize;
                _progressIncrement = (double)ProgressBarMaximum / totalPackedSize;

                if (Dialog is WinFormsDialogBase)
                    _taskbarProgressMaximum = (double)TaskbarProgressMaximumWinForms;
                else
                    _taskbarProgressMaximum = (double)TaskbarProgressMaximumWpf;

                _taskbarProgressIncrement = _taskbarProgressMaximum / (double)totalPackedSize;
            }

            // BeastStrap fork: parallelize package downloads. Upstream BeastStrap downloads
            // packages one at a time, which is the dominant install bottleneck (~30-50 packages,
            // ~200 MB). With a small concurrency window the same install completes in a fraction
            // of the wall time on any reasonable connection. 6 is a sweet spot — enough to
            // saturate residential bandwidth, not so many that we hammer the CDN or starve the
            // disk on slower drives.
            const int maxConcurrentDownloads = 6;
            using var downloadSemaphore = new SemaphoreSlim(maxConcurrentDownloads);

            var pipelineTasks = _versionPackageManifest.Select(async package =>
            {
                await downloadSemaphore.WaitAsync(_cancelTokenSource.Token);
                try
                {
                    if (_cancelTokenSource.IsCancellationRequested)
                        return;

                    await DownloadPackage(package);

                    if (_cancelTokenSource.IsCancellationRequested)
                        return;

                    // WebView2 runtime is unpacked separately later (its installer needs a
                    // dedicated flow), so leave it on disk for now.
                    if (package.Name == "WebView2RuntimeInstaller.zip")
                        return;

                    // Extract on a background thread so it overlaps with the next package's
                    // download — same pipelined behaviour as upstream, just now multiple
                    // downloads in flight at once.
                    await Task.Run(() => ExtractPackage(package), _cancelTokenSource.Token);
                }
                finally
                {
                    downloadSemaphore.Release();
                }
            }).ToList();

            try
            {
                await Task.WhenAll(pipelineTasks);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_cancelTokenSource.IsCancellationRequested)
                return;

            if (Dialog is not null)
            {
                Dialog.ProgressStyle = ProgressBarStyle.Marquee;
                Dialog.TaskbarProgressState = TaskbarItemProgressState.Indeterminate;
                SetStatus(Strings.Bootstrapper_Status_Configuring);
            }
            
            App.Logger.WriteLine(LOG_IDENT, "Writing AppSettings.xml...");
            await File.WriteAllTextAsync(Path.Combine(_latestVersionDirectory, "AppSettings.xml"), AppSettings);

            if (_cancelTokenSource.IsCancellationRequested)
                return;

            if (App.State.Prop.PromptWebView2Install)
            {
                using var hklmKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\WOW6432Node\\Microsoft\\EdgeUpdate\\Clients\\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
                using var hkcuKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\EdgeUpdate\\Clients\\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");

                if (hklmKey is not null || hkcuKey is not null)
                {
                    // reset prompt state if the user has it installed
                    App.State.Prop.PromptWebView2Install = true;
                }   
                else
                {
                    var result = Frontend.ShowMessageBox(Strings.Bootstrapper_WebView2NotFound, MessageBoxImage.Warning, MessageBoxButton.YesNo, MessageBoxResult.Yes);

                    if (result != MessageBoxResult.Yes)
                    {
                        App.State.Prop.PromptWebView2Install = false;
                    }
                    else
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Installing WebView2 runtime...");

                        var package = _versionPackageManifest.Find(x => x.Name == "WebView2RuntimeInstaller.zip");

                        if (package is null)
                        {
                            App.Logger.WriteLine(LOG_IDENT, "Aborted runtime install because package does not exist, has WebView2 been added in this Roblox version yet?");
                            return;
                        }

                        string baseDirectory = Path.Combine(_latestVersionDirectory, AppData.PackageDirectoryMap[package.Name]);

                        ExtractPackage(package);

                        SetStatus(Strings.Bootstrapper_Status_InstallingWebView2);

                        var startInfo = new ProcessStartInfo()
                        {
                            WorkingDirectory = baseDirectory,
                            FileName = Path.Combine(baseDirectory, "MicrosoftEdgeWebview2Setup.exe"),
                            Arguments = "/silent /install"
                        };

                        await Process.Start(startInfo)!.WaitForExitAsync();

                        App.Logger.WriteLine(LOG_IDENT, "Finished installing runtime");

                        Directory.Delete(baseDirectory, true);
                    }
                }
            }

            // finishing and cleanup

            MigrateCompatibilityFlags();

            AppData.DistributionState.VersionGuid = _latestVersionGuid;

            // v420.20: mirror the installed version onto the active Versions Manager
            // profile so the per-launch up-to-date check stays accurate per profile.
            // Without this the next launch would compare the global state against the
            // wanted version and skip the install even though THIS profile's dir is
            // empty / stale.
            var bootstrapProfile = GetActiveProfileForBootstrap();
            if (bootstrapProfile != null)
            {
                bootstrapProfile.InstalledVersionGuid = _latestVersionGuid;
                App.Settings.Save();
            }

            AppData.DistributionState.PackageHashes.Clear();

            foreach (var package in _versionPackageManifest)
                AppData.DistributionState.PackageHashes.Add(package.Name, package.Signature);

            CleanupVersionsFolder();

            var allPackageHashes = new List<string>();

            allPackageHashes.AddRange(App.PlayerState.Prop.PackageHashes.Values);
            allPackageHashes.AddRange(App.StudioState.Prop.PackageHashes.Values);

            if (!App.Settings.Prop.DebugDisableVersionPackageCleanup)
            {
                foreach (string hash in cachedPackageHashes)
                {
                    if (!allPackageHashes.Contains(hash))
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Deleting unused package {hash}");

                        try
                        {
                            File.Delete(Path.Combine(Paths.Downloads, hash));
                        }
                        catch (Exception ex)
                        {
                            App.Logger.WriteLine(LOG_IDENT, $"Failed to delete {hash}!");
                            App.Logger.WriteException(LOG_IDENT, ex);
                        }
                    }
                }
            }

            App.Logger.WriteLine(LOG_IDENT, "Registering approximate program size...");

            int distributionSize = _versionPackageManifest.Sum(x => x.Size + x.PackedSize) / 1024;

            AppData.DistributionState.Size = distributionSize;

            // Was PlayerState + PlayerState — the player counted twice and Studio not at all,
            // so Add/Remove Programs reported roughly double the player size and ignored a Studio
            // install entirely. Lines 2261-2264 already treat both states as the pair they are.
            int totalSize = App.PlayerState.Prop.Size + App.StudioState.Prop.Size;

            using (var uninstallKey = Registry.CurrentUser.CreateSubKey(App.UninstallKey))
            {
                uninstallKey.SetValueSafe("EstimatedSize", totalSize);
            }

            App.Logger.WriteLine(LOG_IDENT, $"Registered as {totalSize} KB");

            App.State.Prop.ForceReinstall = false;

            App.State.Save();
            AppData.DistributionStateManager.Save();

            _isInstalling = false;
        }

        private void StartBackgroundUpdater()
        {
            const string LOG_IDENT = "Bootstrapper::StartBackgroundUpdater";

            if (Utilities.DoesMutexExist(BackgroundUpdaterMutexName))
            {
                App.Logger.WriteLine(LOG_IDENT, "Background updater already running");
                return;
            }

            App.Logger.WriteLine(LOG_IDENT, "Starting background updater");

            Process.Start(Paths.Process, $"-backgroundupdater {_launchMode}");
        }

        private async Task<bool> ApplyModifications()
        {
            const string LOG_IDENT = "Bootstrapper::ApplyModifications";

            bool success = true;

            SetStatus(Strings.Bootstrapper_Status_ApplyingModifications);

            // handle file mods
            App.Logger.WriteLine(LOG_IDENT, "Checking file mods...");

            // manifest has been moved to State.json
            File.Delete(Path.Combine(Paths.Base, "ModManifest.txt"));

            List<string> modFolderFiles = new();

            Directory.CreateDirectory(Paths.Modifications);

            // check custom font mod
            // instead of replacing the fonts themselves, we'll just alter the font family manifests

            string modFontFamiliesFolder = Path.Combine(Paths.Modifications, "content\\fonts\\families");

            if (File.Exists(Paths.CustomFont))
            {
                App.Logger.WriteLine(LOG_IDENT, "Begin font check");

                Directory.CreateDirectory(modFontFamiliesFolder);

                const string path = "rbxasset://fonts/CustomFont.ttf";

                // lets make sure the content/fonts/families path exists in the version directory
                string contentFolder = Path.Combine(_latestVersionDirectory, "content");
                Directory.CreateDirectory(contentFolder);

                string fontsFolder = Path.Combine(contentFolder, "fonts");
                Directory.CreateDirectory(fontsFolder);

                string familiesFolder = Path.Combine(fontsFolder, "families");
                Directory.CreateDirectory(familiesFolder);

                foreach (string jsonFilePath in Directory.GetFiles(familiesFolder))
                {
                    string jsonFilename = Path.GetFileName(jsonFilePath);
                    string modFilepath = Path.Combine(modFontFamiliesFolder, jsonFilename);

                    if (File.Exists(modFilepath))
                        continue;

                    App.Logger.WriteLine(LOG_IDENT, $"Setting font for {jsonFilename}");

                    var fontFamilyData = JsonSerializer.Deserialize<FontFamily>(File.ReadAllText(jsonFilePath));

                    if (fontFamilyData is null)
                        continue;

                    bool shouldWrite = false;

                    foreach (var fontFace in fontFamilyData.Faces)
                    {
                        if (fontFace.AssetId != path)
                        {
                            fontFace.AssetId = path;
                            shouldWrite = true;
                        }
                    }

                    if (shouldWrite)
                        File.WriteAllText(modFilepath, JsonSerializer.Serialize(fontFamilyData, new JsonSerializerOptions { WriteIndented = true }));
                }

                App.Logger.WriteLine(LOG_IDENT, "End font check");
            }
            else if (Directory.Exists(modFontFamiliesFolder))
            {
                Directory.Delete(modFontFamiliesFolder, true);
            }

            foreach (string file in Directory.GetFiles(Paths.Modifications, "*.*", SearchOption.AllDirectories))
            {
                if (_cancelTokenSource.IsCancellationRequested)
                    return true;

                // get relative directory path
                string relativeFile = file.Substring(Paths.Modifications.Length + 1);

                // v1.7.0 - README has been moved to the preferences menu now
                if (relativeFile == "README.txt")
                {
                    File.Delete(file);
                    continue;
                }

                if (!App.Settings.Prop.UseFastFlagManager && String.Equals(relativeFile, "ClientSettings\\ClientAppSettings.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (relativeFile.EndsWith(".lock"))
                    continue;

                modFolderFiles.Add(relativeFile);

                string fileModFolder = Path.Combine(Paths.Modifications, relativeFile);
                string fileVersionFolder = Path.Combine(_latestVersionDirectory, relativeFile);

                if (File.Exists(fileVersionFolder) && MD5Hash.FromFile(fileModFolder) == MD5Hash.FromFile(fileVersionFolder))
                {
                    App.Logger.WriteLine(LOG_IDENT, $"{relativeFile} already exists in the version folder, and is a match");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fileVersionFolder)!);

                Filesystem.AssertReadOnly(fileVersionFolder);
                try
                {
                    File.Copy(fileModFolder, fileVersionFolder, true);
                    Filesystem.AssertReadOnly(fileVersionFolder);
                    App.Logger.WriteLine(LOG_IDENT, $"{relativeFile} has been copied to the version folder");
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Failed to apply modification ({relativeFile})");
                    App.Logger.WriteException(LOG_IDENT, ex);
                    success = false;
                }
            }

            // the manifest is primarily here to keep track of what files have been
            // deleted from the modifications folder, so that we know when to restore the original files from the downloaded packages
            // now check for files that have been deleted from the mod folder according to the manifest

            var fileRestoreMap = new Dictionary<string, List<string>>();

            foreach (string fileLocation in AppData.DistributionState.ModManifest)
            {
                if (modFolderFiles.Contains(fileLocation))
                    continue;

                var packageMapEntry = AppData.PackageDirectoryMap.SingleOrDefault(x => !String.IsNullOrEmpty(x.Value) && fileLocation.StartsWith(x.Value));
                string packageName = packageMapEntry.Key;

                // package doesn't exist, likely mistakenly placed file
                if (String.IsNullOrEmpty(packageName))
                {
                    App.Logger.WriteLine(LOG_IDENT, $"{fileLocation} was removed as a mod but does not belong to a package");

                    string versionFileLocation = Path.Combine(_latestVersionDirectory, fileLocation);

                    if (File.Exists(versionFileLocation))
                        File.Delete(versionFileLocation);

                    continue;
                }

                string fileName = fileLocation.Substring(packageMapEntry.Value.Length);

                if (!fileRestoreMap.ContainsKey(packageName))
                    fileRestoreMap[packageName] = new();

                fileRestoreMap[packageName].Add(fileName);

                App.Logger.WriteLine(LOG_IDENT, $"{fileLocation} was removed as a mod, restoring from {packageName}");
            }

            foreach (var entry in fileRestoreMap)
            {
                var package = _versionPackageManifest.Find(x => x.Name == entry.Key);

                if (package is not null)
                {
                    if (_cancelTokenSource.IsCancellationRequested)
                        return true;

                    await DownloadPackage(package);
                    ExtractPackage(package, entry.Value);
                }
            }

            // make sure we're not overwriting a new update
            // if we're the background update process, always overwrite
            if (App.LaunchSettings.BackgroundUpdaterFlag.Active || !AppData.DistributionStateManager.HasFileOnDiskChanged())
            {
                AppData.DistributionState.ModManifest = modFolderFiles;
                AppData.DistributionStateManager.Save();
            }
            else
            {
                App.Logger.WriteLine(LOG_IDENT, $"{AppData.DistributionStateManager.ClassName} disk mismatch, not saving ModManifest");
            }

            App.Logger.WriteLine(LOG_IDENT, $"Finished checking file mods");

            if (!success)
                App.Logger.WriteLine(LOG_IDENT, "Failed to apply all modifications");

            return success;
        }

        private async Task DownloadPackage(Package package)
        {
            string LOG_IDENT = $"Bootstrapper::DownloadPackage.{package.Name}";
            
            if (_cancelTokenSource.IsCancellationRequested)
                return;

            Directory.CreateDirectory(Paths.Downloads);

            string packageUrl = Deployment.GetLocation($"/{_latestVersionGuid}-{package.Name}");
            string robloxPackageLocation = Path.Combine(Paths.LocalAppData, "Roblox", "Downloads", package.Signature);

            if (File.Exists(package.DownloadPath))
            {
                var file = new FileInfo(package.DownloadPath);

                string calculatedMD5 = MD5Hash.FromFile(package.DownloadPath);

                if (calculatedMD5 != package.Signature)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Package is corrupted ({calculatedMD5} != {package.Signature})! Deleting and re-downloading...");
                    file.Delete();
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Package is already downloaded, skipping...");

                    Interlocked.Add(ref _totalDownloadedBytes, package.PackedSize);
                    UpdateProgressBar();

                    return;
                }
            }
            else if (File.Exists(robloxPackageLocation))
            {
                // let's cheat! if the stock bootstrapper already previously downloaded the file,
                // then we can just copy the one from there

                App.Logger.WriteLine(LOG_IDENT, $"Found existing copy at '{robloxPackageLocation}'! Copying to Downloads folder...");
                File.Copy(robloxPackageLocation, package.DownloadPath);

                Interlocked.Add(ref _totalDownloadedBytes, package.PackedSize);
                UpdateProgressBar();

                return;
            }

            if (File.Exists(package.DownloadPath))
                return;

            const int maxTries = 5;

            App.Logger.WriteLine(LOG_IDENT, "Downloading...");

            // 64 KB rather than 4 KB: sixteen times fewer loop iterations, and each iteration used
            // to post a UI update (see below).
            var buffer = new byte[64 * 1024];

            for (int i = 1; i <= maxTries; i++)
            {
                if (_cancelTokenSource.IsCancellationRequested)
                    return;

                int totalBytesRead = 0;

                try
                {
                    using var response = await App.HttpClient.GetAsync(packageUrl, HttpCompletionOption.ResponseHeadersRead, _cancelTokenSource.Token);
                    await using var stream = await response.Content.ReadAsStreamAsync(_cancelTokenSource.Token);
                    await using var fileStream = new FileStream(package.DownloadPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Delete);

                    while (true)
                    {
                        if (_cancelTokenSource.IsCancellationRequested)
                        {
                            stream.Close();
                            fileStream.Close();
                            return;
                        }

                        int bytesRead = await stream.ReadAsync(buffer, _cancelTokenSource.Token);

                        if (bytesRead == 0)
                            break;

                        totalBytesRead += bytesRead;

                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), _cancelTokenSource.Token);

                        Interlocked.Add(ref _totalDownloadedBytes, bytesRead);

                        // Byte count every chunk, UI update at most ~10x a second. This used to
                        // call UpdateProgressBar() on every single 4 KB read from six concurrent
                        // download tasks, each one a dispatcher post at Normal priority — which
                        // sits ABOVE Render and Input, so the progress dialog starved itself of
                        // the very frames it was trying to update.
                        MaybeUpdateProgressBar();
                    }

                    string hash = MD5Hash.FromStream(fileStream);

                    if (hash != package.Signature)
                        throw new ChecksumFailedException($"Failed to verify download of {packageUrl}\n\nExpected hash: {package.Signature}\nGot hash: {hash}");

                    App.Logger.WriteLine(LOG_IDENT, $"Finished downloading! ({totalBytesRead} bytes total)");
                    break;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"An exception occurred after downloading {totalBytesRead} bytes. ({i}/{maxTries})");
                    App.Logger.WriteException(LOG_IDENT, ex);

                    bool isChecksumFailure = ex.GetType() == typeof(ChecksumFailedException);

                    // A checksum mismatch means the bytes arrived altered, not that the request was
                    // blocked outright. The usual culprit is an antivirus doing HTTPS/TLS inspection:
                    // it re-encrypts the stream and corrupts the payload, so the hash never matches.
                    // That's recoverable, so treat it like any other transient failure here - delete
                    // the partial file and retry, falling back to plain HTTP below (which AV TLS
                    // inspection can't touch). Only once every attempt is exhausted do we give up and
                    // show the connectivity dialog. Previously a single checksum failure terminated
                    // immediately with no retry, so a user behind an inspecting AV could never launch
                    // even though the HTTP fallback would have rescued them.
                    if (i >= maxTries)
                    {
                        if (isChecksumFailure)
                        {
                            App.SendStat("packageDownloadState", "httpFail");

                            Frontend.ShowConnectivityDialog(
                                Strings.Dialog_Connectivity_UnableToDownload,
                                String.Format(Strings.Dialog_Connectivity_UnableToDownloadReason, $"[{App.ProjectSupportLink}]({App.ProjectSupportLink})"),
                                MessageBoxImage.Error,
                                ex
                            );

                            App.Terminate(ErrorCode.ERROR_CANCELLED);
                        }

                        throw;
                    }

                    if (File.Exists(package.DownloadPath))
                        File.Delete(package.DownloadPath);

                    Interlocked.Add(ref _totalDownloadedBytes, -totalBytesRead);
                    UpdateProgressBar();

                    // attempt download over HTTP
                    // this isn't actually that unsafe - signatures were fetched earlier over HTTPS
                    // so we've already established that our signatures are legit, and that there's very likely no MITM anyway
                    // A checksum failure is the strongest signal that something (usually AV HTTPS
                    // inspection) is corrupting the encrypted stream, so switch to HTTP for those too,
                    // not just IOExceptions.
                    if ((ex.GetType() == typeof(IOException) || isChecksumFailure) && !packageUrl.StartsWith("http://"))
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Retrying download over HTTP...");
                        packageUrl = packageUrl.Replace("https://", "http://");
                    }
                }
            }
        }

        private void ExtractPackage(Package package, List<string>? files = null)
        {
            const string LOG_IDENT = "Bootstrapper::ExtractPackage";

            string? packageDir = AppData.PackageDirectoryMap.GetValueOrDefault(package.Name);

            if (packageDir is null)
            {
                // Standalone executables like RobloxPlayerInstaller.exe ship in the manifest but
                // are never extracted (there's nothing to unzip), so they legitimately have no
                // package-map entry. Don't cry WARNING about it in every user's logs — only flag
                // an archive that's genuinely missing a mapping.
                if (package.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    App.Logger.WriteLine(LOG_IDENT, $"{package.Name} is not an extractable package, skipping");
                else
                    App.Logger.WriteLine(LOG_IDENT, $"WARNING: {package.Name} was not found in the package map!");

                return;
            }

            string packageFolder = Path.Combine(_latestVersionDirectory, packageDir);
            string? fileFilter = null;

            // for sharpziplib, each file in the filter needs to be a regex
            if (files is not null)
            {
                var regexList = new List<string>();

                foreach (string file in files)
                    regexList.Add("^" + file.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + "$");

                fileFilter = String.Join(';', regexList);
            }

            App.Logger.WriteLine(LOG_IDENT, $"Extracting {package.Name}...");

            var fastZip = new FastZip(_fastZipEvents);
            fastZip.RestoreDateTimeOnExtract = false;
            fastZip.RestoreAttributesOnExtract = false;

            fastZip.ExtractZip(package.DownloadPath, packageFolder, fileFilter);

            App.Logger.WriteLine(LOG_IDENT, $"Finished extracting {package.Name}");
        }
        #endregion
    }
}
