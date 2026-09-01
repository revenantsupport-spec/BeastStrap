// Keeps the "★ MAIN — <game>" window title on the marked instance. Roblox re-asserts
// its own window title on scene load, so a single SetWindowText is not enough — one
// 2s re-apply loop per marked pid, the same pattern WindowManipulation and StreamMode
// use. The loop ends itself when the client exits; Unmark strips the marker again.

using System.Collections.Concurrent;

namespace BeastStrap.Utility
{
    public static class MainInstanceMarker
    {
        private const string LOG_IDENT = "MainInstanceMarker";
        private const string Prefix = "★ ";

        private sealed class MarkerState
        {
            public CancellationTokenSource Cancel = new();
        }

        private static readonly ConcurrentDictionary<int, MarkerState> _active = new();

        public static bool IsMarked(int pid) => _active.ContainsKey(pid);

        public static void Mark(int pid)
        {
            if (_active.TryGetValue(pid, out _))
                return;

            var state = new MarkerState();
            if (!_active.TryAdd(pid, state))
                return;

            // Land on it now — that is the whole point of the tick.
            InstanceWindow.BringToFront(pid);
            App.Logger.WriteLine(LOG_IDENT, $"Marked pid={pid} as the main instance.");

            Task.Run(() => LoopAsync(pid, state));
        }

        public static void Unmark(int pid)
        {
            if (!_active.TryRemove(pid, out var state))
                return;

            state.Cancel.Cancel();
            state.Cancel.Dispose();

            // Strip the marker so the window goes back to its plain title.
            IntPtr hwnd = InstanceWindow.FindMainWindow(pid);
            if (hwnd != IntPtr.Zero)
            {
                string current = InstanceWindow.GetWindowTitle(hwnd);
                if (current.StartsWith(Prefix, StringComparison.Ordinal))
                    InstanceWindow.SetWindowTitle(hwnd, current.Substring(Prefix.Length));
            }

            App.Logger.WriteLine(LOG_IDENT, $"Unmarked pid={pid}.");
        }

        private static async Task LoopAsync(int pid, MarkerState state)
        {
            try
            {
                while (!state.Cancel.IsCancellationRequested)
                {
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        IntPtr hwnd = InstanceWindow.FindMainWindow(pid);
                        if (hwnd != IntPtr.Zero)
                        {
                            string current = InstanceWindow.GetWindowTitle(hwnd);
                            if (current.Length > 0 && !current.StartsWith(Prefix, StringComparison.Ordinal))
                                InstanceWindow.SetWindowTitle(hwnd, Prefix + current);
                        }
                    }
                    catch (ArgumentException)
                    {
                        // PID gone — Roblox exited. End the loop.
                        Unmark(pid);
                        return;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteException(LOG_IDENT + "::LoopAsync", ex);
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), state.Cancel.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected on unmark / shutdown
            }
        }
    }
}