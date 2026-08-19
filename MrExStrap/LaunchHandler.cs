using System.Windows;

using Windows.Win32;
using Windows.Win32.Foundation;

using BeastStrap.UI.Elements.Dialogs;
using BeastStrap.Enums;

namespace BeastStrap
{
    public static class LaunchHandler
    {
        public static void ProcessNextAction(NextAction action, bool isUnfinishedInstall = false)
        {
            const string LOG_IDENT = "LaunchHandler::ProcessNextAction";

            switch (action)
            {
                case NextAction.LaunchSettings:
                    App.Logger.WriteLine(LOG_IDENT, "Opening settings");
                    LaunchSettings();
                    break;

                case NextAction.LaunchRoblox:
                    App.Logger.WriteLine(LOG_IDENT, "Opening Roblox");
                    LaunchRoblox(LaunchMode.Player);
                    break;

                case NextAction.LaunchRobloxStudio:
                    App.Logger.WriteLine(LOG_IDENT, "Opening Roblox Studio");
                    LaunchRoblox(LaunchMode.Studio);
                    break;

                default:
                    App.Logger.WriteLine(LOG_IDENT, "Closing");
                    App.Terminate(isUnfinishedInstall ? ErrorCode.ERROR_INSTALL_USEREXIT : ErrorCode.ERROR_SUCCESS);
                    break;
            }
        }

        public static void ProcessLaunchArgs()
        {
            const string LOG_IDENT = "LaunchHandler::ProcessLaunchArgs";

            // this order is specific

            if (App.LaunchSettings.UninstallFlag.Active)
            {
                App.Logger.WriteLine(LOG_IDENT, "Opening uninstaller");
                LaunchUninstaller();
            }
            else if (App.LaunchSettings.TrayFlag.Active)
            {
                App.Logger.WriteLine(LOG_IDENT, "Opening system tray launcher");
                LaunchTray();
            }
            else if (App.LaunchSettings.MenuFlag.Active)
            {
                App.Logger.WriteLine(LOG_IDENT, "Opening settings");
                LaunchSettings();
            }
            else if (App.LaunchSettings.WatcherFlag.Active)
            {
                App.Logger.WriteLine(LOG_IDENT, "Opening watcher");
                LaunchWatcher();
            }
            else if (App.LaunchSettings.BackgroundUpdaterFlag.Active)
            {
                App.Logger.WriteLine(LOG_IDENT, "Opening background updater");
                LaunchBackgroundUpdater();
            }
            else if (App.LaunchSettings.RobloxLaunchMode != LaunchMode.None)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Opening bootstrapper ({App.LaunchSettings.RobloxLaunchMode})");
                LaunchRoblox(App.LaunchSettings.RobloxLaunchMode);
            }
            else if (!App.LaunchSettings.QuietFlag.Active)
            {
                App.Logger.WriteLine(LOG_IDENT, "Opening menu");
                LaunchMenu();
            }
            else
            {
                App.Logger.WriteLine(LOG_IDENT, "Closing - quiet flag active");
                App.Terminate();
            }
        }

        public static void LaunchInstaller()
        {
            using var interlock = new InterProcessLock("Installer");

            if (!interlock.IsAcquired)
            {
                Frontend.ShowMessageBox(Strings.Dialog_AlreadyRunning_Installer, MessageBoxImage.Stop);
                App.Terminate();
                return;
            }

            if (App.LaunchSettings.UninstallFlag.Active)
            {
                Frontend.ShowMessageBox(Strings.Bootstrapper_FirstRunUninstall, MessageBoxImage.Error);
                App.Terminate(ErrorCode.ERROR_INVALID_FUNCTION);
                return;
            }

            if (App.LaunchSettings.QuietFlag.Active)
            {
                var installer = new Installer();

                if (!installer.CheckInstallLocation())
                    App.Terminate(ErrorCode.ERROR_INSTALL_FAILURE);

                installer.DoInstall();

                interlock.Dispose();

                ProcessLaunchArgs();
            }
            else
            {
#if QA_BUILD
                Frontend.ShowMessageBox("You are about to install a QA build of BeastStrap. The red window border indicates that this is a QA build.\n\nQA builds are handled completely separately of your standard installation, like a virtual environment.", MessageBoxImage.Information);
#endif

                new LanguageSelectorDialog().ShowDialog();

                var installer = new UI.Elements.Installer.MainWindow();
                installer.ShowDialog();

                interlock.Dispose();

                ProcessNextAction(installer.CloseAction, !installer.Finished);
            }

        }

        public static void LaunchUninstaller()
        {
            using var interlock = new InterProcessLock("Uninstaller");

            if (!interlock.IsAcquired)
            {
                Frontend.ShowMessageBox(Strings.Dialog_AlreadyRunning_Uninstaller, MessageBoxImage.Stop);
                App.Terminate();
                return;
            }

            bool confirmed = false;
            bool keepData = true;

            if (App.LaunchSettings.QuietFlag.Active)
            {
                confirmed = true;
            }
            else
            {
                var dialog = new UninstallerDialog();
                dialog.ShowDialog();

                confirmed = dialog.Confirmed;
                keepData = dialog.KeepData;
            }

            if (!confirmed)
            {
                App.Terminate();
                return;
            }

            Installer.DoUninstall(keepData);

            Frontend.ShowMessageBox(Strings.Bootstrapper_SuccessfullyUninstalled, MessageBoxImage.Information);

            App.Terminate();
        }

        public static void LaunchSettings()
        {
            const string LOG_IDENT = "LaunchHandler::LaunchSettings";

            using var interlock = new InterProcessLock("Settings");

            if (interlock.IsAcquired)
            {
                bool showAlreadyRunningWarning = Process.GetProcessesByName(App.ProjectName).Length > 1;

                var window = new UI.Elements.Settings.MainWindow(showAlreadyRunningWarning);

                // typically we'd use Show(), but we need to block to ensure IPL stays in scope
                window.ShowDialog();
            }
            else
            {
                App.Logger.WriteLine(LOG_IDENT, "Found an already existing menu window");

                var process = Utilities.GetProcessesSafe().Where(x => x.MainWindowTitle == Strings.Menu_Title).FirstOrDefault();

                if (process is not null)
                    PInvoke.SetForegroundWindow((HWND)process.MainWindowHandle);

                App.Terminate();
            }
        }

        // The IPL needs to outlive LaunchTray (which returns immediately so
        // the WPF dispatcher loop can start pumping messages for the
        // NotifyIcon). Stash it in a static field so it stays in scope for
        // the lifetime of the process.
        private static InterProcessLock? _trayLock;
        private static UI.TrayLauncher? _trayInstance;

        public static void LaunchTray()
        {
            const string LOG_IDENT = "LaunchHandler::LaunchTray";

            // Only one tray launcher per machine. If a previous launch already
            // claimed the lock, the user has another tray running — bail
            // silently rather than spawn a second tray icon.
            _trayLock = new InterProcessLock("TrayLauncher");
            if (!_trayLock.IsAcquired)
            {
                App.Logger.WriteLine(LOG_IDENT, "Another tray launcher is already running — exiting.");
                _trayLock.Dispose();
                _trayLock = null;
                App.Terminate();
                return;
            }

            // Long-lived: the TrayLauncher owns its NotifyIcon and update
            // timer for the lifetime of the process. We return immediately
            // so WPF's main dispatcher loop starts (driven by Application.Run
            // after OnStartup completes), which pumps messages for the
            // NotifyIcon. ShutdownMode is OnExplicitShutdown so the process
            // stays alive until the user clicks "Exit tray launcher".
            try
            {
                _trayInstance = new UI.TrayLauncher();

                // Cleanup hangs off App.Terminate (see DisposeTrayLauncher), NOT Application.Exit.
                // "Exit tray launcher" calls App.Terminate -> Environment.Exit, which never raises
                // Application.Exit, so a handler here would not run and the icon would sit in the
                // tray after the process was gone — the user clicks Exit, nothing appears to
                // happen, they click again and nothing responds.
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                App.Terminate();
            }
        }

        /// <summary>
        /// Tears down the tray launcher and releases its single-instance lock. Safe to call from any
        /// exit path, and safe to call when no tray launcher was ever created.
        /// </summary>
        internal static void DisposeTrayLauncher()
        {
            try { _trayInstance?.Dispose(); } catch { }
            try { _trayLock?.Dispose(); } catch { }

            _trayInstance = null;
            _trayLock = null;
        }

        public static void LaunchMenu()
        {
            // Auto-update check on menu open. The Bootstrapper has its own check on the Roblox
            // launch path, but users who open BeastStrap directly (Start menu shortcut, etc.)
            // used to never see new releases. This adds the same flow as a Roblox launch: prompt
            // first, then download with a progress dialog, relaunch with the menu reopened on
            // the new exe. App.Terminate is mandatory on the success path because the App-level
            // ShutdownMode is OnExplicitShutdown — without it the old process would hang as a
            // ghost with no visible window after the new exe takes over.
            //
            // The gate matches Bootstrapper.CheckForUpdates so dev/QA builds don't overwrite
            // themselves with a release exe mid-iteration.
#if (!DEBUG || DEBUG_UPDATER) && !QA_BUILD
            if (TryMenuAutoUpgrade())
            {
                App.Terminate();
                return;
            }
#endif

            var dialog = new LaunchMenuDialog();
            dialog.ShowDialog();

            ProcessNextAction(dialog.CloseAction);
        }

        private static bool TryMenuAutoUpgrade()
        {
            const string LOG_IDENT = "LaunchHandler::TryMenuAutoUpgrade";

            if (!AppUpdater.IsAutoUpdateEligible())
                return false;

            try
            {
                var release = Task.Run(() => App.GetLatestRelease()).GetAwaiter().GetResult();
                if (release is null)
                    return false;

                VersionComparison cmp;
                try
                {
                    cmp = Utilities.CompareVersions(App.Version, release.TagName);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::Compare", ex);
                    return false;
                }

                if (cmp != VersionComparison.LessThan)
                    return false;

                var prompt = Frontend.ShowMessageBox(
                    $"A new version of BeastStrap is available.\n\nYou're on v{App.Version}. Latest is {release.TagName}.\n\n" +
                    "Install now? BeastStrap will download the update and reopen the menu on the new version.",
                    MessageBoxImage.Question,
                    MessageBoxButton.YesNo,
                    MessageBoxResult.Yes);

                if (prompt != MessageBoxResult.Yes)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"User declined update to {release.TagName}");
                    return false;
                }

                // Show the progress dialog modally. It kicks off the download in its Loaded
                // handler and closes itself when done — ShowDialog returns once the bar fills.
                var progressDialog = new UI.Elements.Dialogs.UpdateProgressDialog(release, new[] { "-menu" });
                progressDialog.ShowDialog();

                if (!progressDialog.UpdateStarted)
                {
                    string reason = string.IsNullOrEmpty(progressDialog.FailureReason)
                        ? "Unknown error."
                        : progressDialog.FailureReason!;

                    Frontend.ShowMessageBox(
                        $"Couldn't download {release.TagName}.\n\n" +
                        $"Reason: {reason}\n\n" +
                        "Opening the menu on your current version. You can grab the installer manually from the GitHub releases page, " +
                        "or open Settings → Debug mode → Open log folder for the full diagnostic log.",
                        MessageBoxImage.Warning);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                return false;
            }
        }

        public static void LaunchRoblox(LaunchMode launchMode)
        {
            const string LOG_IDENT = "LaunchHandler::LaunchRoblox";

            if (launchMode == LaunchMode.None)
                throw new InvalidOperationException("No Roblox launch mode set");

            if (!File.Exists(Path.Combine(Paths.System, "mfplat.dll")))
            {
                Frontend.ShowMessageBox(Strings.Bootstrapper_WMFNotFound, MessageBoxImage.Error);

                if (!App.LaunchSettings.QuietFlag.Active)
                    Utilities.ShellExecute("https://support.microsoft.com/en-us/topic/media-feature-pack-list-for-windows-n-editions-c1c6fffa-d052-8338-7a79-a4bb980a700a");

                App.Terminate(ErrorCode.ERROR_FILE_NOT_FOUND);
            }

            if (App.Settings.Prop.ConfirmLaunches && Mutex.TryOpenExisting("ROBLOX_singletonMutex", out var _))
            {
                // this currently doesn't work very well since it relies on checking the existence of the singleton mutex
                // which often hangs around for a few seconds after the window closes
                // it would be better to have this rely on the activity tracker when we implement IPC in the planned refactoring

                var result = Frontend.ShowMessageBox(Strings.Bootstrapper_ConfirmLaunch, MessageBoxImage.Warning, MessageBoxButton.YesNo);

                if (result != MessageBoxResult.Yes)
                {
                    App.Terminate();
                    return;
                }
            }

            // Game picker runs first: it can put a placeId into the launch args, which the VIP
            // picker below then has something to work with.
            MaybeShowGamePicker(launchMode);

            MaybeShowVipServerPicker(launchMode);

            // Version picker (v420.22+). Pops a small "pick a version" dialog AFTER the
            // VIP server picker so the user can switch profiles without opening the
            // settings UI. Returns false when the user cancels the dialog — we abort
            // the launch entirely in that case rather than silently launching whatever
            // was active before.
            if (!MaybeShowVersionPicker(launchMode))
            {
                App.Logger.WriteLine(LOG_IDENT, "Launch cancelled by user at the version picker.");
                App.Terminate();
                return;
            }

            // start bootstrapper and show the bootstrapper modal if we're not running silently
            App.Logger.WriteLine(LOG_IDENT, "Initializing bootstrapper");
            App.Bootstrapper = new Bootstrapper(launchMode);
            IBootstrapperDialog? dialog = null;

            if (!App.LaunchSettings.QuietFlag.Active)
            {
                App.Logger.WriteLine(LOG_IDENT, "Initializing bootstrapper dialog");
                dialog = App.Settings.Prop.BootstrapperStyle.GetNew();
                App.Bootstrapper.Dialog = dialog;
                dialog.Bootstrapper = App.Bootstrapper;
            }

            Task.Run(App.Bootstrapper.Run).ContinueWith(t =>
            {
                try
                {
                    App.Logger.WriteLine(LOG_IDENT, "Bootstrapper task has finished");

                    if (t.IsFaulted)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "An exception occurred when running the bootstrapper");

                        if (t.Exception is not null)
                            App.FinalizeExceptionHandling(t.Exception);
                    }
                }
                finally
                {
                    // See LaunchWatcher: this is the only thing that ends the process, so it must
                    // not be reachable-only-on-success. FinalizeExceptionHandling can itself throw.
                    App.Terminate();
                }
            });

            dialog?.ShowBootstrapper();

            App.Logger.WriteLine(LOG_IDENT, "Exiting");
        }

        // Pre-launch hook: when the user has VIP server picker enabled, show a small
        // WebView2 dialog over the rbxservers.xyz embed for the place being launched.
        // If the user picks a server, append its accessCode to the launch args before
        // the bootstrapper snapshots them. Skip when conditions aren't met (Studio,
        // quiet/silent launches, no place ID, accessCode already specified, etc.).
        private static void MaybeShowVipServerPicker(LaunchMode launchMode)
        {
            const string LOG_IDENT = "LaunchHandler::MaybeShowVipServerPicker";

            if (!App.Settings.Prop.EnableVipServerPrompt)
            {
                App.Logger.WriteLine(LOG_IDENT, "Skipping VIP picker: EnableVipServerPrompt is off.");
                return;
            }

            if (launchMode != LaunchMode.Player)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Skipping VIP picker: launch mode is {launchMode}, not Player.");
                return;
            }

            if (App.LaunchSettings.QuietFlag.Active)
            {
                App.Logger.WriteLine(LOG_IDENT, "Skipping VIP picker: quiet flag active (silent/background launch).");
                return;
            }

            string args = App.LaunchSettings.RobloxLaunchArgs;
            long? placeId = Utility.LaunchArgsUtility.TryExtractPlaceId(args);
            if (!placeId.HasValue)
            {
                // Log a truncated preview so the user can see the actual args shape if extraction fails.
                string preview = string.IsNullOrEmpty(args)
                    ? "<empty>"
                    : args.Substring(0, Math.Min(args.Length, 200));
                App.Logger.WriteLine(LOG_IDENT, $"Skipping VIP picker: no placeId found in launch args. Args preview: {preview}");
                return;
            }

            if (Utility.LaunchArgsUtility.TryExtractAccessCode(args) is not null)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Skipping VIP picker: launch args already include an accessCode (joining a specific server, place {placeId.Value}).");
                return;
            }

            string argsPreview = args.Length > 200 ? args.Substring(0, 200) + "..." : args;
            App.Logger.WriteLine(LOG_IDENT, $"Showing VIP server picker for place {placeId.Value}. Args: {argsPreview}");

            try
            {
                var dialog = new UI.Elements.Dialogs.VipServerDialog(placeId.Value);
                dialog.ShowDialog();

                if (!string.IsNullOrEmpty(dialog.PickedAccessCode))
                {
                    App.LaunchSettings.RobloxLaunchArgs = Utility.LaunchArgsUtility.AppendAccessCode(args, dialog.PickedAccessCode);
                    App.Logger.WriteLine(LOG_IDENT, "VIP server picked; appended accessCode to launch args.");
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, "VIP picker closed without a selection; launching normally.");
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "VIP picker failed; falling back to normal launch.");
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        // Pre-launch hook (v420.22+): when ShowVersionPickerOnLaunch is on, pop the
        // Versions Manager tile grid as a small modal so the user can pick which
        // profile to launch without opening the settings UI. The selected profile
        // becomes the active one (persisted) so it sticks for subsequent launches.
        //
        // Returns true if the launch should proceed (either picker disabled, user
        // confirmed a selection, or the optional non-LIVE confirmation passed).
        // Returns false if the user cancelled — caller App.Terminate's the launch.
        // Pre-launch hook that makes "join the emptiest server on launch" work no matter how the
        // user started Roblox. Launching from a game page already carries a placeId, so there's
        // nothing to ask. Launching from BeastStrap or the tray carries nothing at all, and the
        // setting would silently do nothing — so ask which game, then synthesize the same kind of
        // deeplink a website launch would have produced.
        //
        // Everything downstream is unchanged: the bootstrapper's MaybeSelectEmptiestServerAsync
        // sees a normal plain place join and rewrites it to the emptiest server as usual.
        private static void MaybeShowGamePicker(LaunchMode launchMode)
        {
            const string LOG_IDENT = "LaunchHandler::MaybeShowGamePicker";

            if (!App.Settings.Prop.JoinEmptiestServerOnLaunch)
                return;

            if (launchMode != LaunchMode.Player)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Skipping game picker: launch mode is {launchMode}, not Player.");
                return;
            }

            if (App.LaunchSettings.QuietFlag.Active)
            {
                App.Logger.WriteLine(LOG_IDENT, "Skipping game picker: quiet flag active (silent/background launch).");
                return;
            }

            // Already joining something specific (game page, friend join, VIP, rejoin) — leave it
            // alone. This is the path that already worked.
            if (Utility.LaunchArgsUtility.TryExtractPlaceId(App.LaunchSettings.RobloxLaunchArgs) is not null)
            {
                App.Logger.WriteLine(LOG_IDENT, "Skipping game picker: the launch already carries a placeId.");
                return;
            }

            App.Logger.WriteLine(LOG_IDENT, "Launch has no game attached and the emptiest-server setting is on — asking which game to join.");

            long? picked;
            try
            {
                var dialog = new UI.Elements.Dialogs.GamePickerDialog();
                dialog.ShowDialog();
                picked = dialog.PickedPlaceId;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Game picker dialog failed; launching normally.");
                App.Logger.WriteException(LOG_IDENT, ex);
                return;
            }

            if (picked is null or <= 0)
            {
                App.Logger.WriteLine(LOG_IDENT, "User skipped the game picker — launching Roblox normally.");
                return;
            }

            // Same deeplink shape the Server Browser's join uses. No auth ticket needed: the Roblox
            // client joins as whoever is already signed in.
            App.LaunchSettings.RobloxLaunchArgs = $"roblox://experiences/start?placeId={picked}";
            App.Logger.WriteLine(LOG_IDENT, $"User picked place {picked}; launch args set to a plain place join for the emptiest-server pass.");
        }

        private static bool MaybeShowVersionPicker(LaunchMode launchMode)
        {
            const string LOG_IDENT = "LaunchHandler::MaybeShowVersionPicker";

            if (!App.Settings.Prop.ShowVersionPickerOnLaunch)
                return true;

            if (launchMode != LaunchMode.Player)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Skipping version picker: launch mode is {launchMode}, not Player.");
                return true;
            }

            if (App.LaunchSettings.QuietFlag.Active)
            {
                App.Logger.WriteLine(LOG_IDENT, "Skipping version picker: quiet flag active (silent/background launch).");
                return true;
            }

            if (App.Settings.Prop.VersionProfiles.Count == 0)
            {
                App.Logger.WriteLine(LOG_IDENT, "Skipping version picker: no profiles configured.");
                return true;
            }

            Models.Persistable.VersionProfile? picked;
            try
            {
                var dialog = new UI.Elements.Dialogs.VersionPickerDialog();
                dialog.ShowDialog();
                picked = dialog.PickedProfile;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Version picker dialog failed; launching with the currently-active profile.");
                App.Logger.WriteException(LOG_IDENT, ex);
                return true;
            }

            if (picked == null)
            {
                App.Logger.WriteLine(LOG_IDENT, "User cancelled the version picker — aborting launch.");
                return false;
            }

            App.Settings.Prop.ActiveVersionProfileId = picked.Id;
            // Mirror into the legacy single-pin so downstream readers (logs, third-
            // party consumers) see a consistent value. Built-in LIVE profile clears
            // it; pinned profiles set it to their hash.
            if (picked.IsBuiltIn || string.IsNullOrEmpty(picked.VersionGuid))
            {
                App.Settings.Prop.UseCustomVersion = false;
                App.Settings.Prop.CustomVersionGuid = "";
            }
            else
            {
                App.Settings.Prop.UseCustomVersion = true;
                App.Settings.Prop.CustomVersionGuid = picked.VersionGuid;
            }
            App.Settings.Save();
            App.Logger.WriteLine(LOG_IDENT, $"Picker activated profile '{picked.Name}' ({picked.Id})");

            // Non-LIVE confirmation: skip for built-in / empty version (which IS LIVE),
            // skip when the toggle is off. Otherwise ask the user explicitly so they
            // know they're launching a downgraded build.
            if (App.Settings.Prop.ConfirmNonLiveLaunch
                && !picked.IsBuiltIn
                && !string.IsNullOrEmpty(picked.VersionGuid))
            {
                string body =
                    $"Launching Roblox on a non-LIVE build:\n\n" +
                    $"Profile: {picked.Name}\n" +
                    $"Version: {picked.VersionGuid}\n\n" +
                    "Older builds may fail to join public games and using them can violate Roblox TOS. Continue?\n\n" +
                    "(Turn off 'Confirm non-LIVE launches' in Settings → Behaviour if you don't want this prompt next time.)";

                var result = Frontend.ShowMessageBox(body, MessageBoxImage.Warning, MessageBoxButton.YesNo, MessageBoxResult.Yes);
                if (result != MessageBoxResult.Yes)
                {
                    App.Logger.WriteLine(LOG_IDENT, "User declined the non-LIVE confirmation — aborting launch.");
                    return false;
                }
            }

            return true;
        }

        public static void LaunchWatcher()
        {
            const string LOG_IDENT = "LaunchHandler::LaunchWatcher";

            // this whole topology is a bit confusing, bear with me:
            // main thread: strictly UI only, handles showing of the notification area icon, context menu, server details dialog
            // - server information task: queries server location, invoked if either the explorer notification is shown or the server details dialog is opened
            // - discord rpc thread: handles rpc connection with discord
            //    - discord rich presence tasks: handles querying and displaying of game information, invoked on activity watcher events
            // - watcher task: runs activity watcher + waiting for roblox to close, terminates when it has

            var watcher = new Watcher();

            // App.Terminate() goes in a finally. ShutdownMode is OnExplicitShutdown, so it is the
            // only thing that ends this process — anything that throws on the way to it leaves a
            // windowless BeastStrap running forever, one per Roblox session. Both statements
            // above it can throw (watcher.Dispose did, on every single normal shutdown, because
            // releasing its mutex from this thread-pool thread is illegal).
            Task.Run(watcher.Run).ContinueWith(t =>
            {
                try
                {
                    App.Logger.WriteLine(LOG_IDENT, "Watcher task has finished");

                    watcher.Dispose();

                    if (t.IsFaulted)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "An exception occurred when running the watcher");

                        if (t.Exception is not null)
                            App.FinalizeExceptionHandling(t.Exception);
                    }
                }
                finally
                {
                    App.Terminate();
                }
            });
        }

        public static void LaunchBackgroundUpdater()
        {
            const string LOG_IDENT = "LaunchHandler::LaunchBackgroundUpdater";

            // Activate some LaunchFlags we need
            App.LaunchSettings.QuietFlag.Active = true;
            App.LaunchSettings.NoLaunchFlag.Active = true;

            if (!Enum.TryParse(App.LaunchSettings.BackgroundUpdaterFlag.Data, out LaunchMode launchMode))
                throw new ApplicationException($"Invalid launch mode arg ({App.LaunchSettings.BackgroundUpdaterFlag.Data})");

            if (launchMode != LaunchMode.Player && launchMode != LaunchMode.Studio)
                throw new ApplicationException($"Unsupported launch mode {launchMode} provided");

            App.Logger.WriteLine(LOG_IDENT, "Initializing bootstrapper");
            App.Bootstrapper = new Bootstrapper(launchMode)
            {
                MutexNamePrefix = "BeastStrap-BackgroundUpdater",
                QuitIfMutexExists = true
            };

            CancellationTokenSource cts = new CancellationTokenSource();

            Task.Run(() =>
            {
                App.Logger.WriteLine(LOG_IDENT, "Started event waiter");
                using (EventWaitHandle handle = new EventWaitHandle(false, EventResetMode.AutoReset, "BeastStrap-BackgroundUpdaterKillEvent"))
                    handle.WaitOne();

                App.Logger.WriteLine(LOG_IDENT, "Received close event, killing it all!");
                App.Bootstrapper.Cancel();
            }, cts.Token);

            Task.Run(App.Bootstrapper.Run).ContinueWith(t =>
            {
                try
                {
                    App.Logger.WriteLine(LOG_IDENT, "Bootstrapper task has finished");
                    cts.Cancel(); // stop event waiter

                    if (t.IsFaulted)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "An exception occurred when running the bootstrapper");

                        if (t.Exception is not null)
                            App.FinalizeExceptionHandling(t.Exception);
                    }
                }
                finally
                {
                    // See LaunchWatcher. A background updater that fails to terminate is the worst
                    // of the three — it is invisible, it holds the updater mutex, and it makes every
                    // later launch wait on KillBackgroundUpdater before it can upgrade.
                    App.Terminate();
                }
            });

            App.Logger.WriteLine(LOG_IDENT, "Exiting");
        }
    }
}
