// Live preview of another window's content — the little "box of the game they're in"
// under each running instance. Every instance runs its own background capture thread
// (so the settings UI never blocks), paced to the InstancePreviewFps setting (default
// 30) via PrintWindow(PW_RENDERFULLCONTENT) — the GPU-surface redirect that works for
// games where DWM thumbnails come back blank. Buffers are reused across frames and the
// full-window capture is downscaled to the box before conversion, so a 30fps preview
// churns tiny bitmap allocations instead of 1080p ones every 33ms. Pauses when hidden,
// skips minimized windows, drops stale frames instead of queuing them, and the thread
// dies with the control.

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace BeastStrap.UI.Elements.Controls
{
    public class WindowPreviewHost : System.Windows.Controls.Image
    {
        private const uint PW_RENDERFULLCONTENT = 0x00000002;
        private const int DefaultFps = 30;

        private readonly CancellationTokenSource _cts = new();
        private readonly object _captureLock = new();
        private Thread? _thread;
        private Bitmap? _buffer;   // full-window capture buffer, reused across frames
        private Bitmap? _scaled;   // box-sized result buffer, reused across frames
        private bool _paused = true;
        private int _targetW = 96;
        private int _targetH = 54;

        public WindowPreviewHost()
        {
            IsVisibleChanged += OnVisibleChanged;
            Unloaded += (_, _) => StopThread();
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
            => ((WindowPreviewHost)d).EnsureThread();

        protected override void OnRenderSizeChanged(System.Windows.SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            _targetW = Math.Max(1, (int)Math.Round(ActualWidth));
            _targetH = Math.Max(1, (int)Math.Round(ActualHeight));
        }

        private void OnVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e)
        {
            _paused = !IsVisible;
            if (IsVisible && SourceWindowHandle != IntPtr.Zero)
                EnsureThread();
        }

        private void EnsureThread()
        {
            if (_thread is { IsAlive: true })
                return;

            _thread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "InstancePreview"
            };
            _thread.Start();
        }

        private void StopThread()
        {
            _cts.Cancel();
            try { _thread?.Join(300); } catch { }
            _thread = null;
        }

        private static int ReadTargetFps()
        {
            int fps = App.Settings?.Prop?.InstancePreviewFps ?? DefaultFps;
            return Math.Clamp(fps, 1, 60);
        }

        private void CaptureLoop()
        {
            var sw = Stopwatch.StartNew();

            while (!_cts.IsCancellationRequested)
            {
                if (_paused || SourceWindowHandle == IntPtr.Zero || IsIconic(SourceWindowHandle))
                {
                    Thread.Sleep(40);
                    continue;
                }

                double intervalMs = 1000.0 / ReadTargetFps();

                try
                {
                    CaptureFrame();
                }
                catch
                {
                    // keep the last good frame; next tick retries
                }

                double elapsedMs = sw.Elapsed.TotalMilliseconds;
                double waitMs = intervalMs - elapsedMs;
                Thread.Sleep(waitMs > 0 ? (int)waitMs : 1);
                sw.Restart();
            }
        }

        private void CaptureFrame()
        {
            IntPtr hwnd = SourceWindowHandle;
            if (hwnd == IntPtr.Zero)
                return;

            if (!GetWindowRect(hwnd, out RECT winRect))
                return;

            int winW = winRect.Right - winRect.Left;
            int winH = winRect.Bottom - winRect.Top;
            if (winW <= 0 || winH <= 0 || winW > 8192 || winH > 8192)
                return;

            lock (_captureLock)
            {
                if (_buffer is null || _buffer.Width != winW || _buffer.Height != winH)
                {
                    _buffer?.Dispose();
                    _buffer = new Bitmap(winW, winH, PixelFormat.Format32bppArgb);
                }

                bool ok;
                using (var g = Graphics.FromImage(_buffer))
                {
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        // PW_RENDERFULLCONTENT redirects the GPU surface; fall back to the
                        // plain WM_PRINT capture if the full-content flag isn't supported.
                        ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                        if (!ok)
                            ok = PrintWindow(hwnd, hdc, 0);
                    }
                    finally
                    {
                        g.ReleaseHdc(hdc);
                    }
                }

                if (!ok)
                    return;

                if (_scaled is null || _scaled.Width != _targetW || _scaled.Height != _targetH)
                {
                    _scaled?.Dispose();
                    _scaled = new Bitmap(_targetW, _targetH, PixelFormat.Format32bppArgb);
                }

                using (var g = Graphics.FromImage(_scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(_buffer, new Rectangle(0, 0, _targetW, _targetH));
                }

                IntPtr hbitmap = _scaled.GetHbitmap();
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHBitmap(
                        hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();

                    // Drop the stale frame if the UI thread is already behind — the next tick
                    // always has a fresher one. Latest-wins, never a queue.
                    App.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!_cts.IsCancellationRequested)
                            Source = source;
                    }));
                }
                finally
                {
                    DeleteObject(hbitmap);
                }
            }
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

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        #endregion
    }
}