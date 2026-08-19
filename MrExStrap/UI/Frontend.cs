using System.Windows;

using BeastStrap.UI.Elements.Bootstrapper;
using BeastStrap.UI.Elements.Dialogs;

namespace BeastStrap.UI
{
    static class Frontend
    {
        public static MessageBoxResult ShowMessageBox(string message, MessageBoxImage icon = MessageBoxImage.None, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            App.Logger.WriteLine("Frontend::ShowMessageBox", message);

            if (App.LaunchSettings.QuietFlag.Active)
                return defaultResult;

            return ShowFluentMessageBox(message, icon, buttons);
        }

        // crash=false: Roblox never produced a log / failed to start (shown from the bootstrapper).
        // crash=true:  Roblox started fine and then died on its own (shown from the watcher).
        //
        // Pass the dead client's lifetime when it's known so the self-check can look at what every
        // BeastStrap process was doing while that client was actually alive.
        //
        // The order here matters and it used to be backwards. This method opened by asserting the
        // crash was "usually a Roblox-side issue" and only ran the analyzer afterwards, so the
        // dialog led with a verdict it had never checked. A user's 2026-07-27 bundle has us
        // deleting the Roblox folder out from under their running client and then telling them to
        // turn off their NVIDIA overlay. Now nothing is written until we know who to blame.
        public static void ShowPlayerErrorDialog(bool crash = false, DateTime? clientStartUtc = null, DateTime? clientEndUtc = null)
        {
            if (App.LaunchSettings.QuietFlag.Active)
                return;

            var verdict = BeastStrap.Utility.CrashAnalyzer.Analyze(clientStartUtc, clientEndUtc);

            var info = new StringBuilder();

            if (verdict.Fault == CrashFault.Ours)
            {
                // Own it. No Roblox-side framing, and no pointing at the user's executor or their
                // drivers for something we did.
                info.Append(crash
                    ? "Roblox closed unexpectedly — and it looks like BeastStrap caused it."
                    : "Roblox failed to launch — and it looks like BeastStrap caused it.");
                info.Append("\n\n" + verdict.Message);
                info.Append("\n\nSorry about that. Sending us your logs still helps — it tells us how often "
                    + "this is happening in the wild.");
            }
            else
            {
                if (crash)
                    info.Append("Roblox closed unexpectedly — it looks like the game crashed.");
                else
                    info.Append(String.Format(Strings.Dialog_PlayerError_FailedLaunch, App.ProjectSupportLink));

                if (verdict.Fault == CrashFault.Theirs)
                    info.Append("\n\n" + verdict.Message);

                // Only say it wasn't us when we actually went and looked. If we couldn't read our
                // own logs we have no business claiming anything.
                if (verdict.SelfCleared)
                {
                    info.Append("\n\nWe checked BeastStrap's own logs for this launch and found nothing "
                        + "wrong on our end, so this one looks like it really is outside BeastStrap.");
                }
                else
                {
                    info.Append("\n\nWe couldn't read BeastStrap's own logs for this launch, so we can't "
                        + "rule BeastStrap out either. Send us your logs and we'll check properly.");
                }

                // If they're on an executor profile, that's a strong candidate: injected / external
                // tools crash the Roblox client constantly. Point them at the clean profile so they
                // can tell an executor problem from a real Roblox problem. Deliberately skipped when
                // the fault is ours — blaming their executor for our bug is exactly the failure this
                // rewrite exists to stop.
                string? executor = App.GetActiveExecutorTitle();
                if (!string.IsNullOrEmpty(executor))
                {
                    info.Append($"\n\nYou're launching with the **{executor}** executor. Crashes like this are very "
                        + "often caused by the executor or other external/injection tools, not by Roblox or "
                        + "BeastStrap. Try switching to the **Latest LIVE** profile in the Versions Manager and "
                        + "launching clean — if it stops crashing, the executor was the cause.");
                }
            }

            // Offer the same one-click log export the crash dialog has. FluentMessageBox renders
            // markdown, so the email / Discord links below are clickable.
            info.Append("\n\nWant us to take a look? Export your logs and send them over — "
                + $"email [{App.ProjectSupportEmail}](mailto:{App.ProjectSupportEmail}) "
                + $"or join our [Discord]({App.ProjectDiscordLink}).\n\nExport your logs now?");

            var result = ShowMessageBox(info.ToString(), MessageBoxImage.Error, MessageBoxButton.YesNo, MessageBoxResult.Yes);

            if (result == MessageBoxResult.Yes)
            {
                // We're on a background thread here, so blocking until the export finishes is safe
                // (and the export shows its own dispatcher-marshalled result dialog). The process
                // may exit right after this returns, so we wait.
                SupportActions.ExportLogsAsync().GetAwaiter().GetResult();
            }
        }

        public static void ShowExceptionDialog(Exception exception)
        {
            if (App.LaunchSettings.QuietFlag.Active)
                return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                new ExceptionDialog(exception).ShowDialog();
            });
        }

        public static void ShowConnectivityDialog(string title, string description, MessageBoxImage image, Exception exception)
        {
            if (App.LaunchSettings.QuietFlag.Active)
                return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                new ConnectivityDialog(title, description, image, exception).ShowDialog();
            });
        }

        private static IBootstrapperDialog GetCustomBootstrapper()
        {
            const string LOG_IDENT = "Frontend::GetCustomBootstrapper";

            Directory.CreateDirectory(Paths.CustomThemes);

            try
            {
                if (App.Settings.Prop.SelectedCustomTheme == null)
                    throw new CustomThemeException("CustomTheme.Errors.NoThemeSelected");

                CustomDialog dialog = new CustomDialog();
                dialog.ApplyCustomTheme(App.Settings.Prop.SelectedCustomTheme);
                return dialog;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);

                if (!App.LaunchSettings.QuietFlag.Active)
                    ShowMessageBox(string.Format(Strings.CustomTheme_Errors_SetupFailed, ex.Message, "BeastStrap"), MessageBoxImage.Error); // NOTE: BeastStrap is the theme name

                return GetBootstrapperDialog(BootstrapperStyle.FluentDialog);
            }
        }

        public static IBootstrapperDialog GetBootstrapperDialog(BootstrapperStyle style)
        {
            return style switch
            {
                BootstrapperStyle.VistaDialog => new VistaDialog(),
                BootstrapperStyle.LegacyDialog2008 => new LegacyDialog2008(),
                BootstrapperStyle.LegacyDialog2011 => new LegacyDialog2011(),
                BootstrapperStyle.ProgressDialog => new ProgressDialog(),
                BootstrapperStyle.ClassicFluentDialog => new ClassicFluentDialog(),
                BootstrapperStyle.ByfronDialog => new ByfronDialog(),
                BootstrapperStyle.FluentDialog => new FluentDialog(false),
                BootstrapperStyle.FluentAeroDialog => new FluentDialog(true),
                BootstrapperStyle.CustomDialog => GetCustomBootstrapper(),
                _ => new FluentDialog(false)
            };
        }

        private static MessageBoxResult ShowFluentMessageBox(string message, MessageBoxImage icon, MessageBoxButton buttons)
        {
            return Application.Current.Dispatcher.Invoke(new Func<MessageBoxResult>(() =>
            {
                var messagebox = new FluentMessageBox(message, icon, buttons);
                messagebox.ShowDialog();
                return messagebox.Result;
            }));
        }

        /// <summary>
        /// Shows a tray balloon. Delegates to <see cref="Utility.LiveChannelToast"/>, which owns
        /// the dispatch and the transient-NotifyIcon bookkeeping.
        /// </summary>
        /// <remarks>
        /// This used to build its own NotifyIcon with Visible = true and never dispose it. The
        /// bootstrapper fires this on extraction/modification failure and then calls Terminate,
        /// so the shell never got the delete message and a dead icon sat in the notification area
        /// until the user moused over it. It also passed its `timeout` straight to
        /// NotifyIcon.ShowBalloonTip, which takes milliseconds, not the seconds implied here — so
        /// the two callers were asking for a 5 millisecond balloon.
        /// </remarks>
        public static void ShowBalloonTip(string title, string message, System.Windows.Forms.ToolTipIcon icon = System.Windows.Forms.ToolTipIcon.None)
            => BeastStrap.Utility.LiveChannelToast.ShowToast(title, message, icon);
    }
}
