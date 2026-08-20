using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;

using Microsoft.Win32;

namespace BeastStrap
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public const string ProjectName = "BeastStrap";
        public const string ProjectDisplayName = "BeastStrap";
        public const string ProjectOwner = "BeastStrap";
        public const string ProjectRepository = "revenantsupport-spec/BeastStrap";

        // Hosted on real GitHub now. The website and the REST API live on different hosts, so
        // ProjectApiBase is decoupled from ProjectHost (on the old self-hosted Forgejo the API
        // was /api/v1 under the same host). GithubRelease still deserializes the same
        // tag_name / assets / browser_download_url shape the GitHub release JSON returns.
        public const string ProjectHost = "https://github.com";
        public const string ProjectApiBase = "https://api.github.com";
        public const string ProjectDownloadLink = $"{ProjectHost}/{ProjectRepository}";
        public const string ProjectHelpLink = $"{ProjectHost}/{ProjectRepository}";
        public const string ProjectSupportLink = $"{ProjectHost}/{ProjectRepository}/issues/new";

        // Fork support channels — where users send their crash logs. Surfaced on every
        // error/crash dialog so a non-developer audience never has to touch GitHub.
        public const string ProjectSupportEmail = "admin@robloxscripts.com";
        public const string ProjectDiscordLink = "https://discord.robloxscripts.com";

        public const string RobloxPlayerAppName = "RobloxPlayerBeta";
        public const string RobloxStudioAppName = "RobloxStudioBeta";

        // simple shorthand for extremely frequently used and long string - this goes under HKCU
        public const string UninstallKey = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{ProjectName}";

        public static LaunchSettings LaunchSettings { get; private set; } = null!;

        public static BuildMetadataAttribute BuildMetadata = Assembly.GetExecutingAssembly().GetCustomAttribute<BuildMetadataAttribute>()!;

        public static string Version = FormatAssemblyVersion(Assembly.GetExecutingAssembly().GetName().Version!);

        // Fork versioning: single-integer major ("420"), optional minor for point releases
        // ("420.6"), optional build for patch releases ("420.6.1"). Trailing zero segments are
        // hidden — but a non-zero build MUST be shown, otherwise the auto-updater compares the
        // displayed version against the GitHub tag and loops forever re-installing itself.
        private static string FormatAssemblyVersion(System.Version v)
        {
            if (v.Build > 0)
                return $"{v.Major}.{v.Minor}.{v.Build}";
            if (v.Minor > 0)
                return $"{v.Major}.{v.Minor}";
            return v.Major.ToString();
        }

        public static Bootstrapper? Bootstrapper { get; set; } = null!;

        // BeastStrap fork feature: portable mode. Set at startup when a "portable.txt" flag
        // file sits next to the exe. In portable mode we run in-place, store all user data
        // next to the exe, and skip registry writes / Start-menu shortcuts.
        public static bool IsPortableMode { get; private set; } = false;

        // When IsPortableMode is true and portable.txt contains "cache=local", the heavy
        // Roblox binaries (Versions/, Downloads/) cache to local AppData on the host machine
        // instead of staying with the portable folder. Config still travels.
        public static bool IsPortableFastCache { get; private set; } = false;

        public static bool IsActionBuild => !String.IsNullOrEmpty(BuildMetadata.CommitRef);

        public static bool IsProductionBuild => IsActionBuild && BuildMetadata.CommitRef.StartsWith("tag", StringComparison.Ordinal);

        public static bool IsPlayerInstalled => App.PlayerState.IsSaved && !String.IsNullOrEmpty(App.PlayerState.Prop.VersionGuid);

        public static bool IsStudioInstalled => App.StudioState.IsSaved && !String.IsNullOrEmpty(App.StudioState.Prop.VersionGuid);

        public static readonly MD5 MD5Provider = MD5.Create();

        public static readonly Logger Logger = new();

        public static readonly Dictionary<string, BaseTask> PendingSettingTasks = new();

        public static readonly JsonManager<Settings> Settings = new();

        public static readonly JsonManager<State> State = new();

        // Multi Instance tab account store. Separate file ("Accounts.json") so DPAPI-encrypted
        // cookies stay out of Settings.json and out of the diagnostic crash-export bundle.
        public static readonly JsonManager<AccountsData> Accounts = new("Accounts");

        public static readonly LazyJsonManager<DistributionState> PlayerState = new(nameof(PlayerState));

        public static readonly LazyJsonManager<DistributionState> StudioState = new(nameof(StudioState));

        public static readonly FastFlagManager FastFlags = new();

        // UseCookies = false is REQUIRED for multi-account launching — do not remove it.
        // The Multi Instance tab mints a launch ticket per saved account by setting the
        // account's .ROBLOSECURITY on each request by hand (see RobloxAuth). With a cookie
        // container (the HttpClientHandler default) the handler caches the .ROBLOSECURITY
        // that auth.roblox.com rotates back via Set-Cookie and then re-attaches it to the
        // NEXT account's request — so every alt's ticket resolves to whichever account's
        // cookie got cached first, and they all launch as the same account. No other call
        // in the app relies on the container; every Roblox auth call sets the cookie itself.
        public static readonly HttpClient HttpClient = new(
            new HttpClientLoggingHandler(
                new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false }
            )
        );

        // 0 = no dialog up, 1 = one is up. Int rather than bool so Interlocked can own it.
        private static int _showingExceptionDialog = 0;

        private static string? _webUrl = null;
        public static string WebUrl
        {
            get {
                if (_webUrl != null)
                    return _webUrl;

                string url = ConstructBeastStrapWebUrl();
                if (Settings.Loaded) // only cache if settings are done loading
                    _webUrl = url;
                return url;
            }
        }
        
        public static void Terminate(ErrorCode exitCode = ErrorCode.ERROR_SUCCESS)
        {
            int exitCodeNum = (int)exitCode;

            Logger.WriteLine("App::Terminate", $"Terminating with exit code {exitCodeNum} ({exitCode})");

            // Take down any shell-registered tray icon BEFORE the process dies.
            //
            // Environment.Exit runs no finalizers and does not raise Application.Exit, so anything
            // relying on either to send the shell NIM_DELETE simply never runs. The icon then stays
            // in the notification area, owned by a process that no longer exists, until the user
            // happens to hover the tray and the shell prunes it. With ShutdownMode=OnExplicitShutdown
            // this method is the only exit path, so it is the one place that can be relied on.
            //
            // Never let cleanup stop the exit — a throw here would leak the whole process, which is
            // strictly worse than a stale icon.
            try { Utility.LiveChannelToast.DisposeAll(); }
            catch (Exception ex) { Logger.WriteException("App::Terminate", ex); }

            try { LaunchHandler.DisposeTrayLauncher(); }
            catch (Exception ex) { Logger.WriteException("App::Terminate", ex); }

            Environment.Exit(exitCodeNum);
        }

        // Built-in Versions Manager profile id used by the seed "Latest LIVE" entry.
        // Stable string so user state persists across upgrades.
        public const string LiveBuiltInProfileId = "live-builtin";

        private static void MigrateVersionProfilesIfNeeded()
        {
            const string LOG_IDENT = "App::MigrateVersionProfilesIfNeeded";
            try
            {
                if (Settings.Prop.VersionProfiles.Count > 0)
                    return; // already migrated or user has been here before

                Settings.Prop.VersionProfiles.Add(new VersionProfile
                {
                    Id = LiveBuiltInProfileId,
                    Name = "Latest LIVE",
                    VersionGuid = "",
                    IsBuiltIn = true
                });

                if (Settings.Prop.UseCustomVersion
                    && Utility.VersionGuidValidator.IsWellFormed(Settings.Prop.CustomVersionGuid))
                {
                    var migrated = new VersionProfile
                    {
                        Name = "Migrated pin",
                        VersionGuid = Settings.Prop.CustomVersionGuid
                    };
                    Settings.Prop.VersionProfiles.Add(migrated);
                    Settings.Prop.ActiveVersionProfileId = migrated.Id;
                    Logger.WriteLine(LOG_IDENT, $"Migrated existing pin {Settings.Prop.CustomVersionGuid} into profile {migrated.Id}");
                }
                else
                {
                    Settings.Prop.ActiveVersionProfileId = LiveBuiltInProfileId;
                }

                Settings.Save();
                Logger.WriteLine(LOG_IDENT, $"Seeded VersionProfiles; active = {Settings.Prop.ActiveVersionProfileId}");
            }
            catch (Exception ex)
            {
                Logger.WriteException(LOG_IDENT, ex);
            }
        }

        // Executor title of the active Versions Manager profile, or null when the active
        // profile is the clean built-in LIVE (or has no executor attached). Used to tailor
        // crash messaging — an executor is the prime suspect when Roblox falls over. Safe to
        // call from any process that has loaded Settings (bootstrapper and watcher both do).
        public static string? GetActiveExecutorTitle()
        {
            try
            {
                string activeId = Settings?.Prop?.ActiveVersionProfileId ?? "";
                if (string.IsNullOrEmpty(activeId))
                    return null;

                var active = Settings!.Prop.VersionProfiles.FirstOrDefault(p => p.Id == activeId);
                return string.IsNullOrWhiteSpace(active?.ExecutorTitle) ? null : active!.ExecutorTitle!.Trim();
            }
            catch
            {
                return null;
            }
        }

        public static void SoftTerminate(ErrorCode exitCode = ErrorCode.ERROR_SUCCESS)
        {
            int exitCodeNum = (int)exitCode;

            Logger.WriteLine("App::SoftTerminate", $"Terminating with exit code {exitCodeNum} ({exitCode})");

            Current.Dispatcher.Invoke(() => Current.Shutdown(exitCodeNum));
        }

        void GlobalExceptionHandler(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;

            Logger.WriteLine("App::GlobalExceptionHandler", "An exception occurred");

            FinalizeExceptionHandling(e.Exception);
        }

        // App.xaml's DispatcherUnhandledException only covers the UI thread. Everything else —
        // the bootstrapper's extraction threads, the multi-instance sweep, the window tiler, the
        // singleton mutex holder — runs on plain background threads or fire-and-forget Tasks, and
        // until these two hooks existed a failure there killed the process with no dialog and, worse,
        // no log line at all. That is exactly the class of failure that leaves a user reporting "it
        // just closed" with nothing in the bundle to explain it.
        //
        // Wired up in OnStartup, before any of that work can start.
        private static void HookBackgroundThreadExceptions()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                // The CLR is tearing the process down either way here, so this is about making sure
                // the reason is written down before it goes.
                if (e.ExceptionObject is Exception ex)
                    Logger.WriteException("App::UnhandledException", ex);
                else
                    Logger.WriteLine("App::UnhandledException", $"Non-Exception thrown: {e.ExceptionObject}");
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                // Unobserved by definition — nobody awaited the Task, so nothing else will ever
                // report this. Log it and mark it observed so it doesn't also trip the finalizer.
                Logger.WriteException("App::UnobservedTaskException", e.Exception);
                e.SetObserved();
            };
        }

        public static void FinalizeExceptionHandling(AggregateException ex)
        {
            foreach (var innerEx in ex.InnerExceptions)
                Logger.WriteException("App::FinalizeExceptionHandling", innerEx);

            FinalizeExceptionHandling(ex.GetBaseException(), false);
        }

        public static void FinalizeExceptionHandling(Exception ex, bool log = true)
        {
            if (log)
                Logger.WriteException("App::FinalizeExceptionHandling", ex);

            // Interlocked rather than a plain bool: two threads can fault at the same moment (the
            // extraction pipeline runs six at once), and with a non-atomic check both could get past
            // it and race to show a dialog. Losing threads fall through and return — the winner is
            // the one that terminates the process.
            if (Interlocked.Exchange(ref _showingExceptionDialog, 1) == 1)
                return;

            SendLog();

            if (Bootstrapper?.Dialog != null)
            {
                if (Bootstrapper.Dialog.TaskbarProgressValue == 0)
                    Bootstrapper.Dialog.TaskbarProgressValue = 1; // make sure it's visible

                Bootstrapper.Dialog.TaskbarProgressState = TaskbarItemProgressState.Error;
            }

            Frontend.ShowExceptionDialog(ex);

            Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
        }

        public static string ConstructBeastStrapWebUrl() => "invalid.invalid";

        public static bool CanSendLogs() => false;

        public static async Task<GithubRelease?> GetLatestRelease(CancellationToken token = default)
        {
            const string LOG_IDENT = "App::GetLatestRelease";

            try
            {
                var releaseInfo = await Http.GetJson<GithubRelease>($"{ProjectApiBase}/repos/{ProjectRepository}/releases/latest", token);

                if (releaseInfo is null || releaseInfo.Assets is null)
                {
                    Logger.WriteLine(LOG_IDENT, "Encountered invalid data");
                    RecordUpdateCheckFailure();
                    return null;
                }

                // If the latest release is up but its assets haven't landed yet (a publish/upload
                // race), an update check would fail with "no .exe asset attached" even though the
                // binary exists. Fall back to the most recent release that actually has an exe so
                // the user is never told the installer is missing.
                if (!releaseInfo.Assets.Any(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                {
                    Logger.WriteLine(LOG_IDENT, $"Latest release {releaseInfo.TagName} has no .exe asset yet — scanning for the newest release that does.");
                    GithubRelease? fallback = await FindLatestReleaseWithExeAsync(token);
                    if (fallback is not null)
                        releaseInfo = fallback;
                }

                RecordUpdateCheckSuccess();
                return releaseInfo;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The caller set a budget and gave up — let it handle the cancel,
                // otherwise its timeout logging can never fire. Deliberately NOT counted as a
                // failure: a slow connection shouldn't eventually accuse the update server of
                // being dead.
                throw;
            }
            catch (Exception ex)
            {
                Logger.WriteException(LOG_IDENT, ex);
                RecordUpdateCheckFailure();
            }

            return null;
        }

        // Newest published release that carries a .exe asset. Used as a fallback when
        // /releases/latest is asset-less — the updater publishes after uploading the exe, but a
        // freshly-published release can still be caught mid-population (and the CI attaches the
        // versioned exe + SHA256SUMS a couple of minutes after publish).
        private static async Task<GithubRelease?> FindLatestReleaseWithExeAsync(CancellationToken token)
        {
            const string LOG_IDENT = "App::FindLatestReleaseWithExe";

            try
            {
                var releases = await Http.GetJson<List<GithubRelease>>($"{ProjectApiBase}/repos/{ProjectRepository}/releases?per_page=100", token);
                if (releases is null)
                    return null;

                return releases.FirstOrDefault(r =>
                    r.Assets is not null
                    && r.Assets.Any(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex)
            {
                Logger.WriteException(LOG_IDENT, ex);
                return null;
            }
        }

        // Number of consecutive failed update checks before we tell the user their copy can no
        // longer update itself. Five is enough to ride out a CDN blip or a few minutes offline,
        // and low enough that a genuinely dead endpoint is called out within a handful of launches.
        private const int DeadUpdaterFailureThreshold = 5;

        private static void RecordUpdateCheckSuccess()
        {
            try
            {
                if (State.Prop.UpdateCheckFailureStreak == 0 && State.Prop.LastSuccessfulUpdateCheckUtc is not null)
                    return; // nothing changed, don't churn the file

                State.SaveMerged(s =>
                {
                    s.UpdateCheckFailureStreak = 0;
                    s.LastSuccessfulUpdateCheckUtc = DateTime.UtcNow;
                });
            }
            catch (Exception ex) { Logger.WriteException("App::RecordUpdateCheckSuccess", ex); }
        }

        private static void RecordUpdateCheckFailure()
        {
            try
            {
                // Merged, because this is a read-modify-write on a counter. Two processes
                // incrementing from the same stale base would lose one of the failures, and the
                // "your updater is dead" warning is gated on the streak reaching five.
                State.SaveMerged(s => s.UpdateCheckFailureStreak++);

                if (State.Prop.UpdateCheckFailureStreak == DeadUpdaterFailureThreshold)
                {
                    Logger.WriteLine("App::RecordUpdateCheckFailure",
                        $"Update check has now failed {State.Prop.UpdateCheckFailureStreak} times in a row. " +
                        $"Last success: {State.Prop.LastSuccessfulUpdateCheckUtc?.ToString("u") ?? "never"}.");
                }

                WarnIfUpdaterUnreachable();
            }
            catch (Exception ex) { Logger.WriteException("App::RecordUpdateCheckFailure", ex); }
        }

        /// <summary>
        /// Tells the user, once per installed version, that BeastStrap can no longer reach its
        /// update server and they need to download the new build by hand. Non-blocking toast — this
        /// can run mid-launch and must never hold Roblox up.
        /// </summary>
        public static void WarnIfUpdaterUnreachable()
        {
            if (State.Prop.UpdateCheckFailureStreak < DeadUpdaterFailureThreshold)
                return;

            if (string.Equals(State.Prop.LastNotifiedDeadUpdaterVersion, Version, StringComparison.OrdinalIgnoreCase))
                return; // already told them about this build

            if (LaunchSettings.QuietFlag.Active)
                return;

            Logger.WriteLine("App::WarnIfUpdaterUnreachable",
                $"Warning the user that the update server is unreachable (streak {State.Prop.UpdateCheckFailureStreak}).");

            Utility.LiveChannelToast.ShowToast(
                title: "BeastStrap can't check for updates",
                message: $"We haven't been able to reach the update server, so this copy (v{Version}) can't update itself. "
                       + $"Download the latest build from {ProjectDownloadLink} to make sure you're not missing fixes.",
                icon: System.Windows.Forms.ToolTipIcon.Warning);

            State.SaveMerged(s => s.LastNotifiedDeadUpdaterVersion = Version);
        }

        public static void SendStat(string key, string value) { /* analytics disabled in fork */ }

        public static void SendLog() { /* analytics disabled in fork */ }

        public static void AssertWindowsOSVersion()
        {
            const string LOG_IDENT = "App::AssertWindowsOSVersion";

            int major = Environment.OSVersion.Version.Major;
            if (major < 10) // Windows 10 and newer only
            {
                Logger.WriteLine(LOG_IDENT, $"Detected unsupported Windows version ({Environment.OSVersion.Version}).");

                if (!LaunchSettings.QuietFlag.Active)
                    Frontend.ShowMessageBox(Strings.App_OSDeprecation_Win7_81, MessageBoxImage.Error);

                Terminate(ErrorCode.ERROR_INVALID_FUNCTION);
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            const string LOG_IDENT = "App::OnStartup";

            // First thing, before any background work can be scheduled — otherwise a failure on a
            // non-UI thread takes the process down without leaving a trace in the log.
            HookBackgroundThreadExceptions();

            Locale.Initialize();

            base.OnStartup(e);

            // Apply dark + the BeastStrap neon-cyan brand accent app-wide as early as possible,
            // so every surface — including a pure bootstrapper launch with no settings window —
            // renders on-brand. (WpfUiWindow.ApplyTheme re-applies the same per settings window.)
            Utility.ThemeManager.ApplyFromSettings();

            Logger.WriteLine(LOG_IDENT, $"Starting {ProjectName} v{Version}");

            string userAgent = $"{ProjectName}/{Version}";

            if (IsActionBuild)
            {
                Logger.WriteLine(LOG_IDENT, $"Compiled {BuildMetadata.Timestamp.ToFriendlyString()} from commit {BuildMetadata.CommitHash} ({BuildMetadata.CommitRef})");

                if (IsProductionBuild)
                    userAgent += $" (Production)";
                else
                    userAgent += $" (Artifact {BuildMetadata.CommitHash}, {BuildMetadata.CommitRef})";
            }
            else
            {
                Logger.WriteLine(LOG_IDENT, $"Compiled {BuildMetadata.Timestamp.ToFriendlyString()} from {BuildMetadata.Machine}");

#if QA_BUILD
                userAgent += " (QA)";
#else
                userAgent += $" (Build {Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildMetadata.Machine))})";
#endif
            }

            Logger.WriteLine(LOG_IDENT, $"OSVersion: {Environment.OSVersion}");

            // BanAsync: when the user opted out of persistent MAC spoofing, clear the registry
            // overrides on process exit. Registered once at startup so it fires for any exit
            // path, including Environment.Exit via App.Terminate.
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    if (!Settings.Loaded || Settings.Prop.BanAsyncPersistent)
                        return;
                    if (Settings.Prop.BanAsyncSpoofedAdapterGuids.Count == 0)
                        return;

                    foreach (var guid in Settings.Prop.BanAsyncSpoofedAdapterGuids.ToList())
                        Utility.BanAsync.MacSpoofer.DeleteNetworkAddressByGuid(guid);

                    Logger.WriteLine("App::ProcessExit", $"BanAsync: cleared {Settings.Prop.BanAsyncSpoofedAdapterGuids.Count} spoof override(s) (Persistent=off)");
                }
                catch (Exception ex)
                {
                    Logger.WriteException("App::ProcessExit::BanAsync", ex);
                }
            };

            Logger.WriteLine(LOG_IDENT, $"Loaded from {Paths.Process}");
            Logger.WriteLine(LOG_IDENT, $"Temp path is {Paths.Temp}");
            Logger.WriteLine(LOG_IDENT, $"WindowsStartMenu path is {Paths.WindowsStartMenu}");

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();

            HttpClient.Timeout = TimeSpan.FromSeconds(30);
            HttpClient.DefaultRequestHeaders.Add("User-Agent", userAgent);

            LaunchSettings = new LaunchSettings(e.Args);

            // installation check begins here
            string? installLocation = null;
            bool fixInstallLocation = false;

            // Portable-mode detection (BeastStrap fork): a "portable.txt" flag next to the exe
            // opts into portable mode. When portable, we skip the installer + registry flow
            // entirely — data lives next to the exe, no LocalAppData, no Start-menu shortcuts.
            //
            // If portable.txt contains the line "cache=local" (case-insensitive), the heavy
            // Roblox binaries cache to %LocalAppData%\BeastStrap-Cache\ on the host machine
            // instead. Config (settings, state, logs, mods, themes) still travels with the USB.
            string? exeDir = Directory.GetParent(Paths.Process)?.FullName;
            if (!string.IsNullOrEmpty(exeDir))
            {
                string portableFlag = Path.Combine(exeDir, "portable.txt");
                if (File.Exists(portableFlag))
                {
                    IsPortableMode = true;
                    installLocation = exeDir;

                    try
                    {
                        string content = File.ReadAllText(portableFlag);
                        if (content.Contains("cache=local", StringComparison.OrdinalIgnoreCase))
                            IsPortableFastCache = true;
                    }
                    catch (Exception ex)
                    {
                        Logger.WriteLine(LOG_IDENT, $"Could not read portable.txt: {ex.Message}");
                    }

                    Logger.WriteLine(LOG_IDENT,
                        $"Portable mode enabled (portable.txt at {exeDir}); fast-cache={IsPortableFastCache}");
                }
            }

            if (!IsPortableMode)
            {
                // BeastStrap rebrand: bridge an existing pre-rebrand "MrExBloxstrap" install over
                // to the new identifier BEFORE the install-detection read below, so an auto-updating
                // user is recognised as installed (keeping their settings) instead of being sent to
                // the installer. No-op for fresh installs and portable mode.
                Installer.MigrateBranding();

                using var uninstallKey = Registry.CurrentUser.OpenSubKey(UninstallKey);

                if (uninstallKey?.GetValue("InstallLocation") is string value)
                {
                    if (Directory.Exists(value))
                    {
                        installLocation = value;
                    }
                    else
                    {
                        // check if user profile folder has been renamed
                        var match = Regex.Match(value, @"^[a-zA-Z]:\\Users\\([^\\]+)", RegexOptions.IgnoreCase);

                        if (match.Success)
                        {
                            string newLocation = value.Replace(match.Value, Paths.UserProfile, StringComparison.InvariantCultureIgnoreCase);

                            if (Directory.Exists(newLocation))
                            {
                                installLocation = newLocation;
                                fixInstallLocation = true;
                            }
                        }
                    }
                }

                // silently change install location if we detect a portable run
                if (installLocation is null && Directory.GetParent(Paths.Process)?.FullName is string processDir)
                {
                    var files = Directory.GetFiles(processDir).Select(x => Path.GetFileName(x)).ToArray();

                    // check if settings.json and state.json are the only files in the folder
                    if (files.Length <= 3 && files.Contains("Settings.json") && files.Contains("State.json"))
                    {
                        installLocation = processDir;
                        fixInstallLocation = true;
                    }
                }

                if (fixInstallLocation && installLocation is not null)
                {
                    var installer = new Installer
                    {
                        InstallLocation = installLocation,
                        IsImplicitInstall = true
                    };

                    if (installer.CheckInstallLocation())
                    {
                        Logger.WriteLine(LOG_IDENT, $"Changing install location to '{installLocation}'");
                        installer.DoInstall();
                    }
                    else
                    {
                        // force reinstall
                        installLocation = null;
                    }
                }
            }

            if (installLocation is null)
            {
                Logger.Initialize(true);
                Logger.WriteLine(LOG_IDENT, "Not installed, launching the installer");
                AssertWindowsOSVersion(); // prevent new installs from unsupported operating systems
                LaunchHandler.LaunchInstaller();
            }
            else
            {
                string? cacheRoot = null;
                if (IsPortableFastCache)
                {
                    cacheRoot = Path.Combine(Paths.LocalAppData, $"{ProjectName}-Cache");
                    try
                    {
                        Directory.CreateDirectory(cacheRoot);
                        Logger.WriteLine(LOG_IDENT, $"Fast-portable cache root: {cacheRoot}");
                    }
                    catch (Exception ex)
                    {
                        Logger.WriteLine(LOG_IDENT, $"Could not create fast-portable cache dir, falling back to portable folder: {ex.Message}");
                        cacheRoot = null;
                        IsPortableFastCache = false;
                    }
                }

                Paths.Initialize(installLocation, cacheRoot);

                Logger.WriteLine(LOG_IDENT, "Entering main logic");

                // ensure executable is in the install directory — skipped in portable mode
                // since the running exe already IS the install
                if (!IsPortableMode && Paths.Process != Paths.Application && !File.Exists(Paths.Application))
                {
                    Logger.WriteLine(LOG_IDENT, "Copying to install directory");
                    Installer.DeployExecutable();
                }
#if DEBUG
                // Local/dev builds run from bin\Debug while the shortcut, roblox-player: protocol
                // handler, and account-launch watchers all spawn Paths.Application (the installed
                // copy). If a release install exists, the guard above skips the deploy and every
                // shortcut keeps launching the STALE release exe — old code, old flag behavior.
                // So in Debug builds always refresh the installed copy to match what we just built.
                else if (!IsPortableMode && Paths.Process != Paths.Application)
                {
                    Logger.WriteLine(LOG_IDENT, "Debug build — refreshing installed copy so shortcuts launch the current build");
                    Installer.DeployExecutable();
                }
#endif

                Logger.Initialize(LaunchSettings.UninstallFlag.Active);

                if (!Logger.Initialized && !Logger.NoWriteMode)
                {
                    Logger.WriteLine(LOG_IDENT, "Possible duplicate launch detected, terminating.");
                    Terminate();
                }

                // Move any parked install out of Versions, so that folder holds only the active
                // version-<hash> one. Executors and flag injectors identify the build from what is
                // in there, and a parked profile is a whole second Roblox install.
                //
                // Here rather than in Installer.HandleUpgrade's versioned migration block: that only
                // runs during an actual exe replacement, so anyone sideloading a build or running
                // portable would never migrate. Needs Paths (set above) and the logger, nothing else.
                Utility.VersionProfileLayout.MigrateLegacyParkedInstalls();

                Settings.Load();

                // Re-apply the theme now that the saved palette + effect toggles are loaded — the early
                // apply in OnStartup ran on defaults (before this). Keeps even a pure bootstrapper launch
                // on the user's chosen palette.
                Utility.ThemeManager.ApplyFromSettings();

                State.Load();
                Accounts.Load();

                // Versions Manager (v420.19+) migration. Run after Settings is loaded.
                // Seed a built-in "Latest LIVE" profile so the Versions Manager tab is
                // never empty. If the user previously pinned a custom version via the
                // Downgrading tab, carry that over as a "Migrated" profile and set it
                // active so launch behaviour doesn't silently change under their feet.
                //
                // MUST run before the two fast flag calls below. Both resolve their file path
                // from ActiveVersionProfileId, and this is what sets it. With the old ordering,
                // a user upgrading with a pinned custom version had their flags migrated onto
                // 'live-builtin' (the fallback, because ActiveVersionProfileId was still empty)
                // and then this line made a brand new "Migrated pin" profile active instead.
                // The next launch found no flag file for that profile and wrote {} over the
                // canonical one, so every flag they had configured silently stopped applying.
                MigrateVersionProfilesIfNeeded();

                Utility.FastFlagProfiles.MigrateGlobalIfNeeded();
                FastFlags.Load();

                if (!Locale.SupportedLocales.ContainsKey(Settings.Prop.Locale))
                {
                    Settings.Prop.Locale = "nil";
                    Settings.Save();
                }

                Logger.WriteLine(LOG_IDENT, $"Developer mode: {Settings.Prop.DeveloperMode}");
                Logger.WriteLine(LOG_IDENT, $"Web environment: {Settings.Prop.WebEnvironment}");

                Locale.Set(Settings.Prop.Locale);

                if (!LaunchSettings.BypassUpdateCheck)
                    Installer.HandleUpgrade();

                LaunchHandler.ProcessLaunchArgs();
            }

            // you must *explicitly* call terminate when everything is done, it won't be called implicitly
            Logger.WriteLine(LOG_IDENT, "Startup finished");
        }
    }
}
