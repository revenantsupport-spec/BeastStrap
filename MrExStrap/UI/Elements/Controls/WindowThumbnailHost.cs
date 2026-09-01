// Live DWM preview of another window's content — the little "box of the game they're
// in" under each running instance. Rendered by Desktop Window Manager via
// DwmRegisterThumbnail, so it is a true live view of the Roblox client, not a static
// icon. Every running-instance row hosts one. Blank and cheap when the source can't be
// previewed (DWM off, window gone, handle zero).

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BeastStrap.UI.Elements.Controls
{
    public class WindowThumbnailHost : HwndHost
    {
        public static readonly DependencyProperty SourceWindowHandleProperty =
            DependencyProperty.Register(nameof(SourceWindowHandle), typeof(IntPtr), typeof(WindowThumbnailHost),
                new FrameworkPropertyMetadata(IntPtr.Zero, FrameworkPropertyMetadataOptions.AffectsRender, OnSourceChanged));

        public IntPtr SourceWindowHandle
        {
            get => (IntPtr)GetValue(SourceWindowHandleProperty);
            set => SetValue(SourceWindowHandleProperty, value);
        }

        private IntPtr _hostHwnd;
        private IntPtr _thumbnail;
        private bool _registeredForCurrentSource;

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((WindowThumbnailHost)d).RegisterOrUpdate();

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            _hostHwnd = CreateWindowEx(0, "STATIC", "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
                0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight),
                hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            _registeredForCurrentSource = false;
            RegisterOrUpdate();

            return new HandleRef(this, _hostHwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            UnregisterThumbnail();
            if (_hostHwnd != IntPtr.Zero)
            {
                DestroyWindow(_hostHwnd);
                _hostHwnd = IntPtr.Zero;
            }
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            UpdateThumbnail();
        }

        private void RegisterOrUpdate()
        {
            if (_hostHwnd == IntPtr.Zero)
                return;

            if (_thumbnail != IntPtr.Zero && _registeredForCurrentSource && SourceWindowHandle != IntPtr.Zero)
            {
                UpdateThumbnail();
                return;
            }

            // Source changed (or first registration) — rebuild the DWM link.
            UnregisterThumbnail();

            if (SourceWindowHandle != IntPtr.Zero)
            {
                _thumbnail = DwmRegisterThumbnail(_hostHwnd, SourceWindowHandle);
                _registeredForCurrentSource = _thumbnail != IntPtr.Zero;
            }

            UpdateThumbnail();
        }

        private void UpdateThumbnail()
        {
            if (_thumbnail == IntPtr.Zero || _hostHwnd == IntPtr.Zero)
                return;

            GetClientRect(_hostHwnd, out RECT dest);
            if (dest.Right <= dest.Left || dest.Bottom <= dest.Top)
                return;

            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = dest,
                fVisible = true,
                fSourceClientAreaOnly = true
            };

            DwmUpdateThumbnailProperties(_thumbnail, ref props);
        }

        private void UnregisterThumbnail()
        {
            if (_thumbnail != IntPtr.Zero)
            {
                DwmUnregisterThumbnail(_thumbnail);
                _thumbnail = IntPtr.Zero;
            }
            _registeredForCurrentSource = false;
        }

        #region P/Invoke

        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;

        private const uint DWM_TNP_RECTDESTINATION = 0x00000001;
        private const uint DWM_TNP_VISIBLE = 0x00000008;
        private const uint DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_THUMBNAIL_PROPERTIES
        {
            public uint dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            public bool fVisible;
            public bool fSourceClientAreaOnly;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
            int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
            IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("dwmapi.dll")]
        private static extern IntPtr DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUnregisterThumbnail(IntPtr hThumbnail);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnail, ref DWM_THUMBNAIL_PROPERTIES ptnProperties);

        #endregion
    }
}