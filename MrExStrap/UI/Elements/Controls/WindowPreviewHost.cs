// Live preview of another window's content — the little "box of the game they're in"
// under each running instance. Bitmap-captured (GDI+/PrintWindow) on a ~1.5s tick
// instead of a DWM thumbnail: DWM thumbnails come back blank for hardware-accelerated
// (D3D/Vulkan) windows and need a sized destination host — PrintWindow with
// PW_RENDERFULLCONTENT redirects the DX surface instead and works for games. Auto-pauses
// when the row/tab is hidden, keeps the last good frame on a failed capture, and stops
// its timer when removed from the tree so it can never leak.

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BeastStrap.UI.Elements.Controls
{
    public class WindowPreviewHost : System.Windows.Controls.Image
    {
        private const uint PW_RENDERFULLCONTENT = 0x00000002;

        private static readonly TimeSpan CaptureInterval = TimeSpan.FromSeconds(1.5);

        private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = CaptureInterval };

        public WindowPreviewHost()
        {
            _timer.Tick += (_, _) => Capture();
            IsVisibleChanged += OnVisibleChanged;

            // The tab rebuilds the list on every Refresh; a timer that stays alive keeps its
            // closure (this control) alive forever via the Dispatcher's reference. Stop it.
            Unloaded += (_, _) => _timer.Stop();
        }

        public static readonly DependencyProperty SourceWindowHandleProperty =
            DependencyProperty.Register(nameof(SourceWindowHandle), typeof(IntPtr), typeof(WindowPreviewHost),
                new FrameworkPropertyMetadata(IntPtr.Zero, OnSourceChanged));

        public IntPtr SourceWindowHandle
        {
            get => (IntPtr)GetValue(SourceWindowHandleProperty);
            set => SetValue(SourceWindowHandleProperty, value);
        }

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((WindowPreviewHost)d).OnSourceChanged();

        private void OnSourceChanged()
        {
            UpdateTimer();
            Capture(); // grab immediately when a refresh runs
        }

        private void OnVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e)
            => UpdateTimer();

        private void UpdateTimer()
        {
            bool shouldRun = IsVisible && SourceWindowHandle != IntPtr.Zero;
            if (shouldRun && !_timer.IsEnabled)
                _timer.Start();
            else if (!shouldRun && _timer.IsEnabled)
                _timer.Stop();
        }

        private void Capture()
        {
            if (!IsVisible || SourceWindowHandle == IntPtr.Zero)
                return;

            IntPtr hwnd = SourceWindowHandle;
            if (hwnd == IntPtr.Zero || IsIconic(hwnd))
                return;

            if (!GetWindowRect(hwnd, out RECT winRect))
                return;

            int winW = winRect.Right - winRect.Left;
            int winH = winRect.Bottom - winRect.Top;
            if (winW <= 0 || winH <= 0)
                return;

            // Crop to the client area so the border/title bar doesn't eat the thumbnail.
            int offsetX = 0, offsetY = 0, clientW = winW, clientH = winH;
            if (GetClientRect(hwnd, out RECT clientRect) && clientRect.Right > 0 && clientRect.Bottom > 0)
            {
                clientW = clientRect.Right - clientRect.Left;
                clientH = clientRect.Bottom - clientRect.Top;

                var origin = new POINT();
                ClientToScreen(hwnd, ref origin);

                offsetX = Math.Clamp(origin.X - winRect.Left, 0, Math.Max(0, winW - 1));
                offsetY = Math.Clamp(origin.Y - winRect.Top, 0, Math.Max(0, winH - 1));
            }

            try
            {
                using var frame = new Bitmap(winW, winH, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(frame))
                {
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        // PW_RENDERFULLCONTENT redirects the GPU surface; fall back to the
                        // plain WM_PRINT capture if the full-content flag isn't supported.
                        if (!PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT))
                            PrintWindow(hwnd, hdc, 0);
                    }
                    finally
                    {
                        g.ReleaseHdc(hdc);
                    }
                }

                using var cropped = (offsetX != 0 || offsetY != 0 || clientW != winW || clientH != winH)
                    ? Crop(frame, offsetX, offsetY, clientW, clientH)
                    : null;

                Bitmap final = cropped ?? frame;

                IntPtr hbitmap = IntPtr.Zero;
                try
                {
                    hbitmap = final.GetHbitmap();
                    var source = Imaging.CreateBitmapSourceFromHBitmap(
                        hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    Source = source;
                }
                finally
                {
                    if (hbitmap != IntPtr.Zero)
                        DeleteObject(hbitmap);
                }
            }
            catch
            {
                // Keep the last good frame; the next tick retries.
            }
        }

        private static Bitmap Crop(Bitmap source, int x, int y, int width, int height)
        {
            var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(result))
                g.DrawImage(source, new Rectangle(0, 0, width, height), new Rectangle(x, y, width, height), GraphicsUnit.Pixel);
            return result;
        }

        #region P/Invoke

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        #endregion
    }
}