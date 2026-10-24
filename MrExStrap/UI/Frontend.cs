using System.Windows;

using BeastStrap.UI.Elements.Bootstrapper;
using BeastStrap.UI.Elements.Dialogs;
using BeastStrap.Utility;

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
        public static void ShowPlayerErrorDialog(bool crash = false, DateTime? clientStartUtc = null, DateTime? clientEndUtc = null, string? clientLogPath = null, CrashVerdict? verdict = null)
        {
            const string LOG_IDENT = "Frontend::ShowPlayerErrorDialog";

            if (App.LaunchSettings.QuietFlag.Active)
                return;

            // The watcher already runs the analyzer so it can record the crash locally, so it hands
            // the verdict in rather than making us analyze the same logs twice.
            //
            // clientLogPath scopes the third-party rules to the log of the client that actually
            // died, instead of whichever Roblox logs happen to be recent.
            verdict ??= CrashAnalyzer.Analyze(clientStartUtc, clientEndUtc, clientLogPath);

            // No verdict on a crash means we genuinely do not know why the client died. Say nothing.
            // The failed-launch path is different: "Roblox never started" is itself a known, useful
            // fact, so that one still gets a dialog.
            if (crash && verdict.Fault == CrashFault.Unknown)
            {
                App.Logger.WriteLine(LOG_IDENT,
                    "Roblox exited abnormally but no rule identified a cause. Staying quiet rather than guessing.");
                return;
            }

            var info = new StringBuilder();

            if (verdict.Fault == CrashFault.Ours)
            {
                // Only claim we closed it when the rule actually establishes that. Some ours/* rules
                // prove we were doing something risky near this client, not that we killed it, and a
                // flat "BeastStrap closed Roblox" over that evidence is the same overreach in the
                // opposite direction to the third-party guesses this rewrite removed.
                if (verdict.Certain)
                    info.Append(crash
                        ? "Roblox was closed by BeastStrap."
                        : "Roblox failed to launch because of BeastStrap.");
                else
                    info.Append(crash
                        ? "Roblox closed unexpectedly, and BeastStrap may have caused it."
                        : "Roblox failed to launch, and BeastStrap may have caused it.");

                info.Append("\n\n" + verdict.Message);
            }
            else if (crash)
            {
                info.Append("Roblox closed unexpectedly.");
                info.Append("\n\n" + verdict.Message);
            }
            else
            {
                info.Append(String.Format(Strings.Dialog_PlayerError_FailedLaunch, App.ProjectSupportLink));

                if (verdict.Fault == CrashFault.Theirs)
                    info.Append("\n\n" + verdict.Message);
            }

            // Shared wording, so the support address lives in exactly one place. This dialog asks a
            // Yes/No question, so it uses the prompt variant rather than the one that tells the user
            // to click an Export button that only exists on the other error dialogs.
            info.Append("\n\n" + SupportActions.ExportPromptMarkdown);

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
                BootstrapperStyle.TerminalDialog => new TerminalDialog(),
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
