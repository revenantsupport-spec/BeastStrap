using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BeastStrap.UI.Elements.Controls
{
    /// <summary>
    /// User animated (GIF) wallpaper backdrop for hero surfaces (settings window, launch menu).
    /// The SourcePath DP is fed by ThemeManager via a DynamicResource so it re-evaluates live when
    /// the user picks / clears a GIF. Frames are decoded up-front and cycled by a DispatcherTimer
    /// that honours each frame's own delay (from the GIF's graphics-control block) and only runs
    /// while the control is visible.
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
        private DispatcherTimer? _timer;
        private int _frameIndex;
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
            StartTimer();
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

        private void StartTimer()
        {
            if (!IsVisible || _frames.Count == 0)
                return;

            Stop();
            _timer = new DispatcherTimer(DispatcherPriority.Background);
            _timer.Tick += OnTick;
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(10, _delaysMs[0]));
            _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (_frames.Count == 0)
                return;

            _frameIndex = (_frameIndex + 1) % _frames.Count;
            GifImage.Source = _frames[_frameIndex];

            if (_timer is not null && _delaysMs.Count > 0)
                _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(10, _delaysMs[Math.Min(_frameIndex, _delaysMs.Count - 1)]));
        }

        private void Stop()
        {
            if (_timer is not null)
            {
                _timer.Stop();
                _timer.Tick -= OnTick;
                _timer = null;
            }
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
                StartTimer();
            else
                Stop();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => Stop();
    }
}