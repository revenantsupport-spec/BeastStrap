using System.Drawing;
using System.Windows.Forms;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace BeastStrap.Integrations
{
    // Applies custom icon / title / fake borderless to the running Roblox
    // window. Roblox re-asserts its own window state, so instead of one-shot
    // P/Invoke calls this re-applies everything on a 2s polling loop (same
    // pattern as StreamMode.RewriteWindowTitleLoopAsync), re-resolving the
    // live window handle by PID each pass so a recreated window is picked up.
    public class WindowManipulation : IDisposable
    {
        private const string LOG_IDENT = "WindowManipulation";

        private readonly long _robloxPID;
        private readonly CancellationTokenSource _cancelSource = new();

        private Icon? _icon;
        private IntPtr _iconCopy;
        private IntPtr _borderlessHwnd;

        public WindowManipulation(long windowHandle, long robloxProcessId)
        {
            App.Logger.WriteLine(LOG_IDENT, $"Got window handle as {windowHandle}");
            _robloxPID = robloxProcessId;
        }

        public void Start()
        {
            const string startIdent = LOG_IDENT + "::Start";

            // hold a session-long copy of the icon so re-applies stay cheap
            RobloxIcon robloxIcon = App.Settings.Prop.RobloxIcon;
            if (robloxIcon != RobloxIcon.IconDefault)
            {
                _icon = robloxIcon.GetIcon();
                _iconCopy = PInvoke.CopyIcon((HICON)_icon.Handle);
                App.Logger.WriteLine(startIdent, "Setting Roblox icon");
            }

            if (App.Settings.Prop.FakeBorderlessFullscreen)
                App.Logger.WriteLine(startIdent, "Fake borderless fullscreen enabled");

            Task.Run(() => RunLoopAsync(_cancelSource.Token));
        }

        private async Task RunLoopAsync(CancellationToken token)
        {
            const string loopIdent = LOG_IDENT + "::RunLoop";

            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var process = Process.GetProcessById((int)_robloxPID);
                        process.Refresh();
                        if (process.HasExited)
                            return;

                        IntPtr hwnd = process.MainWindowHandle;
                        if (hwnd != IntPtr.Zero)
                        {
                            if (App.Settings.Prop.FakeBorderlessFullscreen && _borderlessHwnd != hwnd)
                            {
                                FakeBorderless((HWND)hwnd);
                                _borderlessHwnd = hwnd;
                            }

                            if (_iconCopy != IntPtr.Zero)
                                SetIcon((HWND)hwnd);

                            ApplyTitle((HWND)hwnd);
                        }
                    }
                    catch (ArgumentException)
                    {
                        // PID gone — Roblox exited. Bail.
                        return;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteException(loopIdent, ex);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                }
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
        }

        private void FakeBorderless(HWND hWnd)
        {
            const string borderlessIdent = LOG_IDENT + "::BorderlessFullscreen";
            App.Logger.WriteLine(borderlessIdent, "Setting Roblox to borderless fullscreen");

            const int GWLSTYLE = -16;

            int style = PInvoke.GetWindowLong(hWnd, (WINDOW_LONG_PTR_INDEX)GWLSTYLE);

            const int WS_CAPTION = 0x00C00000;
            const int WS_THICKFRAME = 0x00040000;
            const int WS_MINIMIZEBOX = 0x00020000;
            const int WS_MAXIMIZEBOX = 0x00010000;
            const int WS_SYSMENU = 0x00080000;

            style &= ~WS_CAPTION;
            style &= ~WS_THICKFRAME;
            style &= ~WS_MINIMIZEBOX;
            style &= ~WS_MAXIMIZEBOX;
            style &= ~WS_SYSMENU;

            Rectangle resolution = Screen.PrimaryScreen.Bounds;

            PInvoke.SetWindowLong(hWnd, (WINDOW_LONG_PTR_INDEX)GWLSTYLE, style);

            // hack or else it'll still be exclusive
            PInvoke.SetWindowPos(hWnd, (HWND)IntPtr.Zero, 0, 0, resolution.Width, resolution.Height + 1, SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        }

        private void SetIcon(HWND hWnd)
        {
            const int WM_SETICON = 0x0080;
            const int ICON_SMALL = 0; // title bar
            const int ICON_BIG = 1;   // taskbar / alt-tab

            PInvoke.SendMessage(hWnd, WM_SETICON, ICON_SMALL, _iconCopy);
            PInvoke.SendMessage(hWnd, WM_SETICON, ICON_BIG, _iconCopy);
        }

        private void ApplyTitle(HWND hWnd)
        {
            string robloxTitle = App.Settings.Prop.RobloxTitle;
            if (robloxTitle == "Roblox")
                return;

            Span<char> titleBuffer = new char[256];
            PInvoke.GetWindowText(hWnd, titleBuffer);

            string current = titleBuffer.TrimEnd('\0').ToString();
            if (current != robloxTitle)
                PInvoke.SetWindowText(hWnd, robloxTitle);
        }

        public void Dispose()
        {
            const string disposeIdent = LOG_IDENT + "::Dispose";

            _cancelSource.Cancel();

            if (_iconCopy != IntPtr.Zero)
            {
                PInvoke.DestroyIcon((HICON)_iconCopy);
                _iconCopy = IntPtr.Zero;
            }
            _icon?.Dispose();
            _icon = null;

            _cancelSource.Dispose();

            App.Logger.WriteLine(disposeIdent, "Disposed");

            GC.SuppressFinalize(this);
        }
    }
}