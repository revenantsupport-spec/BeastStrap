using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace BeastStrap.UI.Elements.Controls
{
    /// <summary>
    /// User animated (GIF) wallpaper backdrop for hero surfaces (settings window, launch menu).
    /// The SourcePath DP is fed by ThemeManager via a DynamicResource so it re-evaluates live when
    /// the user picks / clears a GIF.
    ///
    /// Playback is delegated to XamlAnimatedGif, which decodes the GIF with its own decoder and
    /// composites every frame over the previous one (honouring the palette's transparency index and
    /// each frame's disposal method) into a single WriteableBitmap. WPF's own GifBitmapDecoder
    /// doesn't composite — swapping its raw delta frames flashes the unchanged regions through as
    /// black flicker, which is what all the hand-rolled players here kept hitting. The library also
    /// downloads remote URLs, falls back to a static frame for invalid files, and tears its animator
    /// down on unload. Playback is paused while the control is hidden so a collapsed or minimised
    /// backdrop costs nothing.
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

        private string _loadedPath = "";
        private Uri? _sourceUri;

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

            _loadedPath = path;
            _sourceUri = null;

            // Tear down the current animation before switching sources.
            SetAnimatedSource(null);

            if (string.IsNullOrWhiteSpace(path))
                return;

            // Local files must exist; remote URLs are fetched by the library.
            if (!IsRemote(path) && !File.Exists(path))
                return;

            try
            {
                if (!Uri.TryCreate(ResolveSource(path), UriKind.Absolute, out Uri? uri))
                    return;

                _sourceUri = uri;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("GifBackground::Reload", ex);
                return;
            }

            ApplySource();
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

        // Pause while hidden (collapsed, or the host window is minimised/covered) so an off-screen
        // backdrop doesn't keep ticking. Resuming just replays the loaded animator — no re-download.
        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            try
            {
                var animator = XamlAnimatedGif.AnimationBehavior.GetAnimator(GifImage);

                if (IsVisible)
                {
                    if (_sourceUri is null)
                        return;

                    if (animator is null)
                        ApplySource();
                    else
                        animator.Play();
                }
                else
                {
                    animator?.Pause();
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("GifBackground::OnIsVisibleChanged", ex);
            }
        }
    }
}