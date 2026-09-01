// Win32 window helpers for the Multi Instance tab's "main instance" marker and the
// live DWM previews. Kept raw-P/Invoke and self-contained so the tab doesn't depend
// on the CsWin32 surface used by WindowManipulation. Same-user windows only.

using System.Runtime.InteropServices;
using System.Text;

namespace BeastStrap.Utility
{
    public static class InstanceWindow
    {
        private const int SW_RESTORE = 9;

        // First visible top-level window owned by pid that carries a title. Mirrors the
        // selection the old MultiInstanceViewModel.GetMainWindowTitle used, but also hands
        // back the handle so the preview thumbnail and the title rewrite target the same
        // window (Roblox can own several windows; the titled, visible one is the game).
        public static IntPtr FindMainWindow(int pid)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out uint winPid);
                if ((int)winPid != pid) return true;
                if (!IsWindowVisible(hWnd)) return true;
                if (GetWindowTextLength(hWnd) == 0) return true;
                found = hWnd;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        public static string GetWindowTitle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return "";

            int len = GetWindowTextLength(hwnd);
            if (len == 0)
                return "";

            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static void SetWindowTitle(IntPtr hwnd, string title)
        {
            if (hwnd != IntPtr.Zero)
                SetWindowText(hwnd, title);
        }

        // Pops the window up if it was minimized, then foregrounds it — "stop tabbing
        // through every alt to find the main account" is solved by landing on it the
        // moment it's ticked.
        public static void BringToFront(int pid)
        {
            IntPtr hwnd = FindMainWindow(pid);
            if (hwnd == IntPtr.Zero)
                return;

            if (IsIconic(hwnd))
                ShowWindow(hwnd, SW_RESTORE);

            SetForegroundWindow(hwnd);
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetWindowText(IntPtr hWnd, string lpString);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}