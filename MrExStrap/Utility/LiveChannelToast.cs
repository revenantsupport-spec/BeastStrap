using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace BeastStrap.Utility
{
    public static class LiveChannelToast
    {
        private const string LOG_IDENT = "LiveChannelToast";

        // Callable from any thread. Dispatches to the WPF UI thread, then shows a transient
        // balloon tip / toast via a short-lived NotifyIcon. If notifications are disabled
        // system-wide, ShowBalloonTip silently no-ops.
        public static void Show()
        {
            if (App.Settings?.Prop?.ShowLiveChannelToast == false)
                return;

            ShowToast(
                title: "Channel: LIVE",
                message: $"Roblox launched on the LIVE channel. Enforced by {App.ProjectName}. You can disable this notification in settings.",
                icon: WinForms.ToolTipIcon.Info);
        }

        // Failure path: the registry write or read-back didn't agree on LIVE. Always shown,
        // even if the success toast is disabled, because the user needs to know the lock
        // promise wasn't kept this launch. The optional reason argument carries through the
        // last exception/mismatch detail so the user has something to act on instead of a
        // blanket "check the log".
        public static void ShowChannelLockFailed(string? reason = null)
        {
            string baseMessage = "Roblox may have launched on a non-LIVE channel. Antivirus, a Roblox manager app, or another tool may be overwriting the channel registry key.";
            string fullMessage = string.IsNullOrEmpty(reason)
                ? baseMessage + " Check the log for details."
                : baseMessage + $" Reason: {reason}";

            ShowToast(
                title: "Channel lock could not be verified",
                message: fullMessage,
                icon: WinForms.ToolTipIcon.Warning);
        }

        // v420.28: promoted to public so the UpdateMonitor toasts (and any
        // future callers) can reuse the dispatch + transient-NotifyIcon
        // bookkeeping. Existing Show / ShowChannelLockFailed callers are
        // unchanged.
        public static void ShowToast(string title, string message, WinForms.ToolTipIcon icon)
        {
            var dispatcher = App.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            try
            {
                dispatcher.InvokeAsync(() => ShowOnUiThread(title, message, icon), DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::ShowToast", ex);
            }
        }

        // Every NotifyIcon currently showing a balloon, so App.Terminate can take them down
        // deterministically. See DisposeAll for why the timer alone is not enough.
        private static readonly List<WinForms.NotifyIcon> _live = new();

        /// <summary>
        /// Removes every toast icon this process still owns. Call before terminating.
        /// </summary>
        /// <remarks>
        /// The disposal timer below is a best-effort tidy-up for long-lived processes, not a
        /// guarantee. The bootstrapper shows a toast and then reaches Environment.Exit two to four
        /// seconds later — well before a ten second timer fires — and Environment.Exit runs no
        /// finalizers. NotifyIcon only sends the shell NIM_DELETE from Dispose(true), so the icon
        /// was left registered to a dead process: one stale BeastStrap icon in the notification
        /// area per Roblox launch, lingering until the user happened to mouse over the tray and the
        /// shell pruned it.
        /// </remarks>
        public static void DisposeAll()
        {
            lock (_live)
            {
                foreach (var icon in _live)
                {
                    try
                    {
                        icon.Visible = false;
                        icon.Dispose();
                    }
                    catch { /* best-effort: we are on the way out */ }
                }

                _live.Clear();
            }
        }

        private static void ShowOnUiThread(string title, string message, WinForms.ToolTipIcon icon)
        {
            WinForms.NotifyIcon? notifyIcon = null;
            try
            {
                notifyIcon = new WinForms.NotifyIcon
                {
                    Icon = BootstrapperIconEx.GetBrandIcon(),
                    Visible = true,
                    BalloonTipTitle = title,
                    BalloonTipText = message,
                    BalloonTipIcon = icon
                };

                lock (_live)
                    _live.Add(notifyIcon);

                notifyIcon.ShowBalloonTip(5000);

                // Keep the NotifyIcon alive long enough for the balloon to naturally dismiss,
                // then dispose so we don't leak a tray slot. 10s is generous — most balloons
                // auto-dismiss in 5s on Win10+. Short-lived processes exit long before this
                // fires, which is what DisposeAll above is for.
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                WinForms.NotifyIcon captured = notifyIcon;
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    try
                    {
                        lock (_live)
                            _live.Remove(captured);

                        captured.Visible = false;
                        captured.Dispose();
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteException(LOG_IDENT + "::Dispose", ex);
                    }
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::ShowOnUiThread", ex);

                if (notifyIcon is not null)
                {
                    lock (_live)
                        _live.Remove(notifyIcon);
                }

                try { notifyIcon?.Dispose(); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
