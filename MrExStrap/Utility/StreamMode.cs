using System.Runtime.InteropServices;

namespace BeastStrap.Utility
{
    // v420.28: hides Roblox-account info from places where it's user-visible
    // (Discord Rich Presence, bootstrapper dialog place readout, Roblox window
    // title) so users who stream / record their screen don't accidentally leak
    // account-identifying info to viewers. Single opt-in toggle —
    // App.Settings.Prop.EnableStreamMode — and no auto-detection of streaming
    // software in v1 (explicit user intent keeps behaviour predictable).
    public static class StreamMode
    {
        private const string LOG_IDENT = "StreamMode";

        public static bool IsActive => App.Settings.Prop.EnableStreamMode;

        // Replacement string for the bootstrapper dialog's "Joining Roblox
        // place #<id>" readout. Generic enough not to identify the user, but
        // still informative enough that the user knows the launch is in
        // progress.
        public static string MaskedPlaceInfo => "Joining a Roblox experience";

        // Background loop that watches the given Roblox PID and rewrites its
        // main window title to a generic "Roblox" whenever the game sets a
        // descriptive one (e.g. "Roblox — <game name> — <username>"). Runs
        // for the lifetime of the process. Polls every 2 seconds — Roblox
        // changes its title at most a few times during a session, so this is
        // cheap and stays under any meaningful CPU radar.
        //
        // Spawned from Watcher.cs when Stream Mode is on.
        public static async Task RewriteWindowTitleLoopAsync(int pid, CancellationToken token)
        {
            const string targetTitle = "Roblox";

            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        process.Refresh();
                        if (process.HasExited)
                            return;

                        IntPtr hwnd = process.MainWindowHandle;
                        if (hwnd != IntPtr.Zero)
                        {
                            string current = process.MainWindowTitle ?? "";
                            if (!string.Equals(current, targetTitle, StringComparison.Ordinal))
                            {
                                SetWindowText(hwnd, targetTitle);
                            }
                        }
                    }
                    catch (ArgumentException)
                    {
                        // PID gone — Roblox exited. Bail.
                        return;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteException(LOG_IDENT + "::RewriteLoop", ex);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                }
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetWindowText(IntPtr hWnd, string lpString);
    }
}
