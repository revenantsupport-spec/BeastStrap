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
    /// source swap per render tick). All frames are frozen at load, which prevents the flicker /
    /// artefacts you get from swapping live-decoded images every tick. The hook only runs while the
    /// control is visible and is detached when it isn't, so it costs nothing in the background.
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

        private readonly List<BitmapFrame> _frames = new();
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

                foreach (BitmapFrame frame in decoder.Frames)
                {
                    _frames.Add(frame);
                    _delaysMs.Add(GetFrameDelayMs(frame));

                    // Freeze every frame up-front so WPF can hand it to the render thread
                    // directly each swap — live (unfrozen) images re-decode and flicker.
                    try
                    {
                        if (!frame.IsFrozen)
                            frame.Freeze();
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteException("GifBackground::FreezeFrame", ex);
                    }
                }
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