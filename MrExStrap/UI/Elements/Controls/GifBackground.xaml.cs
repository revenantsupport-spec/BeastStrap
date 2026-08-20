using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BeastStrap.UI.Elements.Controls
{
    /// <summary>
    /// User animated (GIF) wallpaper backdrop for hero surfaces (settings window, launch menu).
    /// The SourcePath DP is fed by ThemeManager via a DynamicResource so it re-evaluates live when
    /// the user picks / clears a GIF.
    ///
    /// Animation is driven off <see cref="CompositionTarget.Rendering"/> — it fires once per render
    /// frame, synchronised with the display's vsync (typically 60 Hz), which is far smoother than a
    /// DispatcherTimer. Elapsed render time is accumulated against each frame's own delay, so the
    /// GIF plays at its native speed but is only ever advanced at the display refresh rate (max one
    /// source swap per render tick). Frames are composited over each other and frozen at load
    /// (delta frames otherwise flash the background through as black flicker) and the hook only runs
    /// while the control is visible, so it costs nothing in the background.
    /// </summary>
    public partial class GifBackground : UserControl
    {
        public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
            nameof(SourcePath),
            typeof(string),
            typeof(GifBackground),
            new PropertyMetadata("", OnSourcePathChanged));

        /// <summary>Path to the GIF to animate. Empty / missing = nothing is rendered.</summary>
        public string SourcePath
        {
            get => (string)GetValue(SourcePathProperty);
            set => SetValue(SourcePathProperty, value);
        }

        private readonly List<BitmapSource> _frames = new();
        private readonly List<int> _delaysMs = new();
        private bool _subscribedToRendering;
        private int _frameIndex;
        private TimeSpan _lastRenderTime;
        private double _accumulatorMs;
        private string _loadedPath = "";

        public GifBackground()
        {
            InitializeComponent();
            IsVisibleChanged += OnIsVisibleChanged;
            Unloaded += OnUnloaded;
        }

        private static void OnSourcePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((GifBackground)d).Reload(e.NewValue as string ?? "");

        private static bool IsRemote(string path)
            => path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        // Accepts a local file, a direct .gif URL, or a Giphy share page
        // (https://giphy.com/gifs/...), which is resolved to its direct media .gif.
        private static string ResolveSource(string source)
        {
            if (!IsRemote(source))
                return source;

            Uri uri;
            try
            {
                uri = new Uri(source);
            }
            catch (UriFormatException)
            {
                return source;
            }

            // Direct media URL (media.giphy.com / media1..3.giphy.com, giphy.com/media/...):
            // normalise to https + path and use it as-is.
            if (uri.Host.Contains("giphy.com") && uri.AbsolutePath.Contains("/media/"))
                return $"https://{uri.Host}{uri.AbsolutePath}";

            // Giphy share page: the media id is the trailing hyphen-delimited token of the
            // slug path, e.g. https://giphy.com/gifs/rick-astley-ico88wGV3d7RkaMhny.
            if (uri.Host.Equals("giphy.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith("/gifs/", StringComparison.OrdinalIgnoreCase))
            {
                string segment = uri.AbsolutePath.TrimEnd('/');
                int lastSlash = segment.LastIndexOf('/');
                string token = lastSlash >= 0 ? segment[(lastSlash + 1)..] : segment;
                int dash = token.LastIndexOf('-');
                if (dash >= 0)
                    token = token[(dash + 1)..];

                if (!string.IsNullOrWhiteSpace(token))
                    return $"https://media.giphy.com/media/{token}/giphy.gif";
            }

            // Some other URL — try it directly.
            return $"https://{uri.Host}{uri.AbsolutePath}";
        }

        private void Reload(string path)
        {
            if (string.Equals(path, _loadedPath, StringComparison.OrdinalIgnoreCase))
                return;

            Stop();
            _frames.Clear();
            _delaysMs.Clear();
            _frameIndex = 0;
            _accumulatorMs = 0;
            GifImage.Source = null;
            _loadedPath = path;

            if (string.IsNullOrWhiteSpace(path))
                return;

            // Local files must exist; remote URLs are fetched by the decoder.
            if (!IsRemote(path) && !File.Exists(path))
                return;

            try
            {
                var decoder = new GifBitmapDecoder(
                    new Uri(ResolveSource(path)),
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                var rawFrames = new List<BitmapFrame>();
                foreach (BitmapFrame frame in decoder.Frames)
                {
                    rawFrames.Add(frame);
                    _delaysMs.Add(GetFrameDelayMs(frame));
                }

                // Composite the raw frames into full-canvas, GPU-friendly images. Raw GIF
                // frames are usually delta frames - each only carries the pixels that changed
                // since the previous frame and relies on the player drawing it over what came
                // before. Swapping the raw frames directly shows the "unchanged" regions as
                // the dark ink scrim flashing through (the black flicker). Bake the
                // accumulation in once here, so playback is just swaps of frozen images.
                _frames.AddRange(CompositeFrames(rawFrames));
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("GifBackground::Reload", ex);
                _frames.Clear();
                _delaysMs.Clear();
                GifImage.Source = null;
                return;
            }

            if (_frames.Count == 0)
                return;

            _frameIndex = 0;
            GifImage.Source = _frames[0];
            Start();
        }

        // GIF delay is stored in centiseconds (1/100 s) in the frame's graphics-control
        // extension. WPF surfaces it as "/grctlex/GIFDelayTime" on the frame metadata.
        private static int GetFrameDelayMs(BitmapFrame frame)
        {
            const string delayQuery = "/grctlex/GIFDelayTime";
            const int defaultDelayMs = 80;

            try
            {
                if (frame.Metadata is BitmapMetadata md && md.ContainsQuery(delayQuery))
                {
                    object? value = md.GetQuery(delayQuery);
                    if (value is not null)
                    {
                        double centiseconds = value switch
                        {
                            byte b => b,
                            ushort u => u,
                            short s => s,
                            int i => i,
                            _ => double.Parse(value.ToString() ?? "", CultureInfo.InvariantCulture)
                        };
                        return Math.Max(10, (int)Math.Round(centiseconds * 10));
                    }
                }
            }
            catch
            {
                // fall through to the default delay
            }

            return defaultDelayMs;
        }

        // Render each frame over the accumulated result of everything before it, producing a
        // full-canvas opaque image per frame (Pbgra32 - palette resolved, no per-frame decode).
        // Playback then just swaps frozen sources, which the render thread can consume directly.
        private static List<BitmapSource> CompositeFrames(IReadOnlyList<BitmapFrame> frames)
        {
            var composited = new List<BitmapSource>(frames.Count);
            if (frames.Count == 0)
                return composited;

            int canvasWidth = 1, canvasHeight = 1;
            foreach (BitmapFrame f in frames)
            {
                if ((int)f.Width > canvasWidth) canvasWidth = (int)f.Width;
                if ((int)f.Height > canvasHeight) canvasHeight = (int)f.Height;
            }

            double dpiX = frames[0].DpiX > 0 ? frames[0].DpiX : 96.0;
            double dpiY = frames[0].DpiY > 0 ? frames[0].DpiY : 96.0;

            BitmapSource? previous = null;

            foreach (BitmapFrame frame in frames)
            {
                int disposal = GetDisposalMethod(frame);

                var visual = new DrawingVisual();
                using (DrawingContext dc = visual.RenderOpen())
                {
                    // Disposal 2 (restore to background) clears the canvas, so don't carry the
                    // previous frame over. Everything else accumulates - what most web GIFs expect.
                    if (previous is not null && disposal != 2)
                        dc.DrawImage(previous, new Rect(0, 0, canvasWidth, canvasHeight));

                    dc.DrawImage(frame, new Rect(0, 0, canvasWidth, canvasHeight));
                }

                var rtb = new RenderTargetBitmap(canvasWidth, canvasHeight, dpiX, dpiY, PixelFormats.Pbgra32);
                rtb.Render(visual);
                rtb.Freeze();
                composited.Add(rtb);
                previous = rtb;
            }

            return composited;
        }

        // GIF disposal method from the graphics-control extension:
        // 0 = unspecified (treat as 1), 1 = leave in place, 2 = restore to background, 3 = restore to previous.
        private static int GetDisposalMethod(BitmapFrame frame)
        {
            const string disposalQuery = "/grctlex/GIFDisposalMethod";

            try
            {
                if (frame.Metadata is BitmapMetadata md && md.ContainsQuery(disposalQuery))
                {
                    int method = md.GetQuery(disposalQuery) switch
                    {
                        byte b => b,
                        ushort u => u,
                        short s => s,
                        int i => i,
                        _ => -1
                    };
                    if (method >= 0 && method <= 3)
                        return method;
                }
            }
            catch
            {
                // best effort - accumulate by default
            }

            return 0;
        }

        private void Start()
        {
            if (!IsVisible || _frames.Count == 0)
                return;

            // Reset the timing state on every start/resume so a pause (window minimised /
            // hidden) never produces a catch-up burst of frames.
            _accumulatorMs = 0;
            _lastRenderTime = default;

            if (_subscribedToRendering)
                return;

            CompositionTarget.Rendering += OnRendering;
            _subscribedToRendering = true;
        }

        private void Stop()
        {
            if (!_subscribedToRendering)
                return;

            CompositionTarget.Rendering -= OnRendering;
            _subscribedToRendering = false;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (_frames.Count == 0)
                return;

            var renderingTime = ((RenderingEventArgs)e).RenderingTime;

            if (_lastRenderTime == default)
            {
                _lastRenderTime = renderingTime;
                return;
            }

            double elapsedMs = (renderingTime - _lastRenderTime).TotalMilliseconds;
            _lastRenderTime = renderingTime;

            // Clamp so a long stall (UI thread was busy) doesn't fast-forward the animation.
            elapsedMs = Math.Min(elapsedMs, 250);

            _accumulatorMs += elapsedMs;

            // Advance as many frames as the elapsed time covers, subtracting each frame's own
            // delay, but commit the source swap at most once per render tick — the animation is
            // bounded by the display's refresh rate.
            bool advanced = false;
            while (_accumulatorMs >= _delaysMs[_frameIndex])
            {
                _accumulatorMs -= _delaysMs[_frameIndex];
                _frameIndex = (_frameIndex + 1) % _frames.Count;
                advanced = true;
            }

            if (advanced)
                GifImage.Source = _frames[_frameIndex];
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
                Start();
            else
                Stop();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => Stop();
    }
}