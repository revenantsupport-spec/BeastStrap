// Adapted approach from robloxmanager by sasha / centerepic (MIT)
// https://gitlab.com/centerepic/robloxmanager
// Original behavior: after Roblox launches, find all Roblox windows and arrange
// them in a grid on the primary monitor. This C# port uses standard Win32
// (EnumWindows + SetWindowPos) with a short retry loop so windows picked up by
// the tile pass include ones that appear after the initial delay.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace BeastStrap.Utility
{
    public static class WindowTiler
    {
        private const string LOG_IDENT = "WindowTiler";
        private const string RobloxProcessName = "RobloxPlayerBeta";

        // Fire a single tile pass ~5s after launch. Runs on background; never throws up.
        public static void ScheduleTilePass(WindowTilingLayout layout)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    // Give Roblox time to actually create its window.
                    Thread.Sleep(TimeSpan.FromSeconds(5));
                    TileNow(layout);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::Schedule", ex);
                }
            });
        }

        public static void TileNow(WindowTilingLayout layout)
        {
            var windows = FindRobloxWindows();
            App.Logger.WriteLine(LOG_IDENT, $"Found {windows.Count} Roblox window(s) to tile.");

            if (windows.Count == 0)
                return;

            var (rows, cols) = ResolveGrid(layout, windows.Count);

            // Add rows until every window has its own cell. A fixed layout is a request for a
            // column count, not permission to hide clients: Grid2x2 with six windows used to
            // clamp the overflow onto the last row, putting windows 5 and 6 at exactly the same
            // rects as 3 and 4. The user saw four windows and assumed two had failed to launch.
            if (rows * cols < windows.Count)
            {
                int needed = (int)Math.Ceiling((double)windows.Count / cols);
                App.Logger.WriteLine(LOG_IDENT, $"{windows.Count} window(s) won't fit in {rows}x{cols}, growing to {needed}x{cols}.");
                rows = needed;
            }

            App.Logger.WriteLine(LOG_IDENT, $"Layout: {rows}x{cols} for {windows.Count} window(s).");

            var area = Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
            int cellW = area.Width / cols;
            int cellH = area.Height / rows;

            for (int i = 0; i < windows.Count; i++)
            {
                int row = i / cols;
                int col = i % cols;

                int x = area.X + col * cellW;
                int y = area.Y + row * cellH;

                // Restore first so maximized windows respond to the move.
                ShowWindow(windows[i], SW_RESTORE);

                if (!SetWindowPos(windows[i], IntPtr.Zero, x, y, cellW, cellH,
                    SWP_NOZORDER | SWP_NOACTIVATE))
                {
                    int err = Marshal.GetLastWin32Error();
                    App.Logger.WriteLine(LOG_IDENT, $"SetWindowPos failed for hWnd {windows[i]}: {err}");
                }
            }
        }

        private static (int rows, int cols) ResolveGrid(WindowTilingLayout layout, int count)
        {
            switch (layout)
            {
                case WindowTilingLayout.Grid2x2: return (2, 2);
                case WindowTilingLayout.Grid3x3: return (3, 3);
                case WindowTilingLayout.Grid1x2: return (1, 2);
                case WindowTilingLayout.Grid2x1: return (2, 1);
                case WindowTilingLayout.Grid1x3: return (1, 3);
                case WindowTilingLayout.Grid3x1: return (3, 1);
                case WindowTilingLayout.Auto:
                default:
                    int cols = (int)Math.Ceiling(Math.Sqrt(count));
                    int rows = (int)Math.Ceiling((double)count / cols);
                    return (Math.Max(rows, 1), Math.Max(cols, 1));
            }
        }

        private static List<IntPtr> FindRobloxWindows()
        {
            var robloxPids = GetRobloxPids();
            var result = new List<IntPtr>();

            if (robloxPids.Count == 0)
                return result;

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (!robloxPids.Contains((int)pid)) return true;

                // Must have a title — filters out invisible helper windows.
                int len = GetWindowTextLength(hWnd);
                if (len == 0) return true;

                var sb = new StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();

                // Roblox's main window title usually starts with "Roblox". Be lenient —
                // on join it's the game name, so fall back on "has a non-empty title" alone
                // if the class check passes.
                var classBuf = new StringBuilder(128);
                GetClassName(hWnd, classBuf, classBuf.Capacity);
                string className = classBuf.ToString();

                if (className.StartsWith("WINDOWSCLIENT", StringComparison.OrdinalIgnoreCase) ||
                    className.Equals("ROBLOX", StringComparison.OrdinalIgnoreCase) ||
                    title.StartsWith("Roblox", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(hWnd);
                }

                return true;
            }, IntPtr.Zero);

            return result;
        }

        private static HashSet<int> GetRobloxPids()
        {
            var pids = new HashSet<int>();
            try
            {
                foreach (var p in Process.GetProcessesByName(RobloxProcessName))
                {
                    pids.Add(p.Id);
                    p.Dispose();
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::GetPids", ex);
            }
            return pids;
        }

        #region P/Invoke

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int SW_RESTORE = 9;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        #endregion
    }
}
