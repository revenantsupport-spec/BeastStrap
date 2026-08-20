using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BeastStrap.UI.Elements.Controls
{
    /// <summary>
    /// User animated wallpaper backdrop for hero surfaces (settings window, launch menu).
    /// The SourcePath DP is fed by ThemeManager via a DynamicResource so it re-evaluates live when
    /// the user picks / clears a source.
    ///
    /// GIFs are delegated to XamlAnimatedGif, which decodes the GIF with its own decoder and
    /// composites every frame over the previous one (honouring the palette's transparency index and
    /// each frame's disposal method) into a single WriteableBitmap. WPF's own GifBitmapDecoder
    /// doesn't composite — swapping its raw delta frames flashes the unchanged regions through as
    /// black flicker, which is what all the hand-rolled players here kept hitting. The library also
    /// downloads remote URLs, falls back to a static frame for invalid files, and tears its animator
    /// down on unload.
    ///
    /// MP4/WebM videos are played on the Rectangle layer with a MediaPlayer + VideoDrawing. A
    /// motionbgs.com detail page (https://motionbgs.com/&lt;slug&gt;) is fetched once and resolved to the
    /// direct .mp4 the page embeds. Videos loop on MediaEnded and are muted (they're wallpapers).
    ///
    /// Playback (GIF or video) is paused while the control is hidden so a collapsed or minimised
    /// backdrop costs nothing.
    /// </summary>
    public partial class GifBackground : UserControl
    {
        public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
            nameof(SourcePath),
            typeof(string),
            typeof(GifBackground),
            new PropertyMetadata("", OnSourcePathChanged));

        /// <summary>Path to the animated source (GIF / image / video / page URL). Empty / missing = nothing is rendered.</summary>
        public string SourcePath
        {
            get => (string)GetValue(SourcePathProperty);
            set => SetValue(SourcePathProperty, value);
        }

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".webm", ".mov", ".m4v", ".avi", ".mkv", ".mpg", ".mpeg",
        };

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".gif", ".png", ".jpg", ".jpeg", ".bmp", ".webp",
        };

        private static readonly Regex MotionBgsMediaPattern = new(
            @"/media/\d+/[^""'\s<>]+\.(?:mp4|webm)(?![a-zA-Z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private string _loadedPath = "";
        private Uri? _sourceUri;
        private bool _isVideo;
        private MediaPlayer? _videoPlayer;

        public GifBackground()
        {
            InitializeComponent();
            IsVisibleChanged += OnIsVisibleChanged;
        }

        private static void OnSourcePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((GifBackground)d).Reload(e.NewValue as string ?? "");

        private static bool IsRemote(string path)
            => path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        private static bool IsVideoUri(Uri uri)
            => VideoExtensions.Contains(Path.GetExtension(uri.AbsolutePath));

        // A motionbgs.com detail page (or its /mobile/ twin) has no media extension; everything
        // else under motionbgs.com (e.g. /media/&lt;id&gt;/&lt;slug&gt;.mp4) is used directly.
        private static bool IsMotionBgsPage(Uri uri)
        {
            if (!(uri.Host.Equals("motionbgs.com", StringComparison.OrdinalIgnoreCase)
                  || uri.Host.Equals("www.motionbgs.com", StringComparison.OrdinalIgnoreCase)))
                return false;

            string ext = Path.GetExtension(uri.AbsolutePath);
            return !string.IsNullOrEmpty(ext)
                ? !(VideoExtensions.Contains(ext) || ImageExtensions.Contains(ext))
                : true;
        }

        // Accepts a local file, a direct .gif/.mp4 URL, a Giphy share page
        // (https://giphy.com/gifs/...), resolved to its direct media .gif, or a motionbgs.com
        // detail page (https://motionbgs.com/&lt;slug&gt;), resolved later to its direct .mp4.
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

            _loadedPath = path;
            _sourceUri = null;

            // Tear down the current source (GIF animator + any video player) before switching.
            Teardown();

            if (string.IsNullOrWhiteSpace(path))
                return;

            // Local files must exist; remote URLs are fetched / streamed by the player.
            if (!IsRemote(path) && !File.Exists(path))
                return;

            try
            {
                string resolved = ResolveSource(path);
                if (string.IsNullOrEmpty(resolved))
                    return;

                if (!Uri.TryCreate(resolved, UriKind.Absolute, out Uri? uri))
                    return;

                if (IsMotionBgsPage(uri))
                {
                    LoadFromPageAsync(path, resolved);
                    return;
                }

                _sourceUri = uri;
                if (IsVideoUri(uri))
                    LoadVideo(uri);
                else
                    ApplySource();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("GifBackground::Reload", ex);
            }
        }

        // Fetches a motionbgs.com detail page and resolves it to the direct .mp4 it embeds.
        private async void LoadFromPageAsync(string expectedPath, string pageUrl)
        {
            try
            {
                string html;
                using (var response = await App.HttpClient.GetAsync(pageUrl))
                {
                    response.EnsureSuccessStatusCode();
                    html = await response.Content.ReadAsStringAsync();
                }

                // The user switched sources while we were fetching — drop the stale result.
                if (!string.Equals(_loadedPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                    return;

                string? media = ExtractMotionBgsMedia(html);
                if (string.IsNullOrWhiteSpace(media) || !Uri.TryCreate(media, UriKind.Absolute, out Uri? uri))
                    return;

                _sourceUri = uri;
                LoadVideo(uri);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("GifBackground::LoadFromPageAsync", ex);
            }
        }

        private static string? ExtractMotionBgsMedia(string html)
        {
            Match m = MotionBgsMediaPattern.Match(html);
            if (!m.Success)
                return null;
            return "https://motionbgs.com" + m.Value;
        }

        // Applies the source only if the control is actually visible — otherwise playback (and the
        // remote download) starts the first time it becomes visible.
        private void ApplySource()
        {
            if (!IsVisible || _sourceUri is null)
                return;

            try
            {
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(GifImage, _sourceUri);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("GifBackground::ApplySource", ex);
                SetAnimatedSource(null);
            }
        }

        private void SetAnimatedSource(Uri? uri)
        {
            try
            {
                XamlAnimatedGif.AnimationBehavior.SetSourceUri(GifImage, uri);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("GifBackground::SetAnimatedSource", ex);
            }
        }

        private void LoadVideo(Uri uri)
        {
            _isVideo = true;
            SetAnimatedSource(null);
            GifImage.Visibility = Visibility.Collapsed;
            VideoSurface.Visibility = Visibility.Visible;

            if (IsVisible)
                OpenVideo(uri);
        }

        private void OpenVideo(Uri uri)
        {
            try
            {
                var player = _videoPlayer ??= CreatePlayer();
                VideoDraw.Player = player;
                player.Open(uri);
                player.Play();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("GifBackground::OpenVideo", ex);
            }
        }

        private MediaPlayer CreatePlayer()
        {
            var player = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };
            player.MediaOpened += OnMediaOpened;
            player.MediaEnded += OnMediaEnded;
            return player;
        }

        private void OnMediaOpened(object? sender, EventArgs e)
        {
            try
            {
                var player = _videoPlayer;
                if (player is null)
                    return;

                // Size the VideoDrawing to the media's natural dimensions; the DrawingBrush
                // stretch handles fitting it into the control.
                if (player.NaturalVideoWidth > 0 && player.NaturalVideoHeight > 0)
                    VideoDraw.Rect = new Rect(0, 0, player.NaturalVideoWidth, player.NaturalVideoHeight);

                player.Play();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("GifBackground::OnMediaOpened", ex);
            }
        }

        private void OnMediaEnded(object? sender, EventArgs e)
        {
            try
            {
                if (_videoPlayer is { } player)
                {
                    player.Position = TimeSpan.Zero;
                    player.Play();
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("GifBackground::OnMediaEnded", ex);
            }
        }

        private void Teardown()
        {
            SetAnimatedSource(null);

            if (_videoPlayer is not null)
            {
                _videoPlayer.Close();
                _videoPlayer = null;
            }
            VideoDraw.Player = null;
            VideoDraw.Rect = new Rect(0, 0, 1, 1);

            _isVideo = false;
            GifImage.Visibility = Visibility.Visible;
            VideoSurface.Visibility = Visibility.Collapsed;
        }

        // Pause while hidden (collapsed, or the host window is minimised/covered) so an off-screen
        // backdrop doesn't keep ticking. Resuming just replays the loaded source — no re-download.
        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            try
            {
                var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(GifImage);

                if (IsVisible)
                {
                    if (_sourceUri is null)
                        return;

                    if (_isVideo)
                    {
                        if (_videoPlayer is null || _videoPlayer.Source is null)
                            OpenVideo(_sourceUri);
                        else
                            _videoPlayer.Play();
                    }
                    else
                    {
                        if (animator is null)
                            ApplySource();
                        else
                            animator.Play();
                    }
                }
                else
                {
                    animator?.Pause();
                    _videoPlayer?.Pause();
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("GifBackground::OnIsVisibleChanged", ex);
            }
        }
    }
}