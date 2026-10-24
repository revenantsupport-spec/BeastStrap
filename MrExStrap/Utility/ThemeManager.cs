using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

using Wpf.Ui.Appearance;

using BeastStrap.Models;

namespace BeastStrap.Utility
{
    // Turns a ThemePalette into the app's live brand resources. Overwrites the keyed brushes /
    // gradients / effects in Application.Current.Resources (which sit above the merged dictionaries),
    // so anything binding them via DynamicResource updates instantly, and StaticResource consumers
    // pick up the palette on the next window that loads. Also drives the WPF-UI accent (which is
    // DynamicResource-backed everywhere) and honours the aurora / glass / glow on-off toggles.
    public static class ThemeManager
    {
        // Built-in presets. "Default" is the electric ice-blue look.
        public static readonly Dictionary<string, ThemePalette> Presets = new()
        {
            ["Default"] = new ThemePalette(),
            ["Purple Haze"] = new ThemePalette { Accent = "#A855F7", GradientStart = "#A855F7", GradientEnd = "#22D3EE", Purple = "#EC4899", Glow = "#A855F7" },
            ["Blood Red"] = new ThemePalette { Accent = "#FF4D4D", GradientStart = "#FF4D4D", GradientEnd = "#FF9838", Purple = "#FF4D4D", Glow = "#FF4D4D" },
            ["Ocean"] = new ThemePalette { Accent = "#38BDF8", GradientStart = "#38BDF8", GradientEnd = "#818CF8", Purple = "#818CF8", Glow = "#38BDF8" },
            ["Emerald"] = new ThemePalette { Accent = "#34D399", GradientStart = "#34D399", GradientEnd = "#A3E635", Purple = "#22D3EE", Glow = "#34D399" },
            ["Sunset"] = new ThemePalette { Accent = "#FB923C", GradientStart = "#FB923C", GradientEnd = "#F472B6", Purple = "#F472B6", Glow = "#FB923C" },
            ["Mono"] = new ThemePalette { Accent = "#E5E7EB", GradientStart = "#E5E7EB", GradientEnd = "#9CA3AF", Purple = "#9CA3AF", Glow = "#E5E7EB" },
        };

        // Accent-wash resource keys and their alpha, weakest to strongest.
        private static readonly (string Key, byte Alpha)[] AccentWashes =
        {
            ("BrandAccentHoverBrush", 0x1A),
            ("ChipCheckedBg", 0x28),
            ("BrandAccentSelectedBrush", 0x2E),
            ("BrandAccentEdgeBrush", 0x55),
            ("ChipCheckedBorder", 0x66),
            ("BrandAccentStrongBrush", 0x88),
        };

        public static Color Parse(string? hex, Color fallback)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex))
                    return (Color)ColorConverter.ConvertFromString(hex.Trim())!;
            }
            catch { }

            return fallback;
        }

        // ApplyTheme runs in every window's ctor, but the brand resources are app-level and shared, so
        // there's no need to rebuild them for each new window — skip when nothing actually changed.
        private static string _lastSignature = "";

        // Set while Apply is holding the root dictionary's change notices (see Apply).
        private static bool _batching;

        // Apply the saved palette + effect toggles.
        public static void ApplyFromSettings()
        {
            var palette = App.Settings?.Prop?.Palette ?? new ThemePalette();
            if (Signature(palette, ActiveSeasonal) == _lastSignature)
                return;
            Apply(palette);
        }

        // Raised by Apply when what the Appearance page describes may have changed: the theme being SHOWN (a seasonal
        // run starting or ending, another creator theme picked), or the seasonal run its notice covers, including one
        // that was turned off and so never showed. Not on a plain colour edit. Always on the UI thread. The page follows
        // it because settings pages are cached for the window's lifetime, so it would otherwise keep describing yesterday.
        public static event Action? ThemeStateChanged;
        private static string? _shownTheme, _themeState;

        // A palette's colours the way Apply reads them, anything unparseable falling back to the standard look's.
        public static (Color Accent, Color GradientStart, Color GradientEnd, Color Purple, Color Background, Color Surface, Color Hairline, Color Glow) Resolve(ThemePalette p)
        {
            Color accent = Parse(p.Accent, Color.FromRgb(0x38, 0xBD, 0xF8));
            return (accent,
                Parse(p.GradientStart, accent),
                Parse(p.GradientEnd, Color.FromRgb(0x7D, 0xD3, 0xFC)),
                Parse(p.Purple, Color.FromRgb(0xA7, 0x8B, 0xFA)),
                Parse(p.Background, Color.FromRgb(0x07, 0x0C, 0x16)),
                Parse(p.Surface, Color.FromRgb(0x0E, 0x16, 0x24)),
                Parse(p.Hairline, Color.FromRgb(0x1B, 0x29, 0x40)),
                Parse(p.Glow, accent));
        }

        public static void Apply(ThemePalette p)
        {
            var res = Application.Current.Resources;

            // A seasonal theme on its days stands in for whatever was saved (see ActiveSeasonal). Looked up once, so
            // the colours and the art can't disagree across midnight. The signature is taken BEFORE the swap, from the
            // palette as passed in, because that's what ApplyFromSettings compares it with. Taken after, it never
            // matched on a seasonal day, and every window that opened rebuilt every brush.
            var seasonal = ActiveSeasonal;
            string signature = Signature(p, seasonal);
            if (seasonal is not null)
                p = seasonal.Palette;

            var (cyan, gStart, gEnd, purple, ink, surface, hairline, glow) = Resolve(p);

            // Direction sweeps the gradient differently; intensity scales the glow strength.
            GradientDirection direction = p.GradientDirection;
            GlowIntensity intensity = p.GlowIntensity;

            bool glass = App.Settings?.Prop?.EnableGlass ?? true;
            bool glowOn = App.Settings?.Prop?.EnableGlow ?? true;
            bool aurora = App.Settings?.Prop?.EnableAurora ?? true;

            // One invalidation for the lot. Each write to the root dictionary otherwise walks every open window on
            // its own, about 75 walks per Apply, which froze the Appearance page for half a second per keystroke in
            // a colour box. Between BeginInit and EndInit the dictionary holds its change notices, and EndInit sends
            // a single one. Nesting BeginInit throws, hence the guard, though nothing in here calls back into Apply.
            bool batch = !_batching;
            if (batch)
            {
                _batching = true;
                res.BeginInit();
            }

            try
            {
                // Colours
                res["BrandCyanColor"] = cyan;
                res["BrandGreenColor"] = gEnd;
                res["BrandPurpleColor"] = purple;
                res["BrandInkColor"] = ink;
                res["BrandSurfaceColor"] = surface;
                res["BrandHairlineColor"] = hairline;
                res["ApplicationBackgroundColor"] = ink;

                // Solid brushes
                res["BrandCyanBrush"] = Brush(cyan);
                res["BrandGreenBrush"] = Brush(gEnd);
                res["BrandPurpleBrush"] = Brush(purple);
                res["BrandInkBrush"] = Brush(ink);
                res["BrandSurfaceBrush"] = Brush(surface);
                res["BrandHairlineBrush"] = Brush(hairline);
                res["ApplicationBackgroundBrush"] = Brush(ink);

                // Glass surfaces. When glass is off, cards become the solid surface colour.
                res["GlassFillBrush"] = glass ? Brush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)) : Brush(surface);
                res["GlassFillHoverBrush"] = glass ? Brush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)) : Brush(Lighten(surface, 0.08));
                res["GlassBorderBrush"] = Brush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));

                // Make WPF-UI's own controls — text boxes, combo boxes, expanders, toggles — the same
                // material as our cards, so "glass" applies to the whole surface rather than just the
                // cards sitting on it.
                //
                // They were never fully opaque: at rest WPF-UI already paints ControlFillColorDefault
                // (#0FFFFFFF), about 2% dimmer than our card fill, which is why inputs read as flatter
                // rather than obviously different. The visible break was the FOCUSED input state,
                // ControlFillColorInputActive = #B31E1E1E — a 70%-opaque grey — so clicking into a text
                // box turned it into a solid slab in the middle of a glass page. That one token is shared
                // by TextBox, ComboBox, PasswordBox, NumberBox, AutoSuggestBox, RichTextBox and DatePicker,
                // so retinting it fixes all of them at once.
                //
                // ⚠️ Each token has to be set in BOTH forms. WPF-UI's control templates are inconsistent
                // about how they consume them — counted across Styles/Controls: ControlFillColorDefault is
                // referenced 61 times as a Color and twice as a Brush, ControlStrokeColorDefault 4 times as
                // a Color and 42 as a Brush, and ControlFillColorInputActive 9 times as a Color and never
                // as a Brush. Setting only one form leaves most of the controls untouched and the change
                // looks like it silently did nothing.
                //
                // Assigning into Application.Current.Resources is what makes this work at all: the root
                // dictionary shadows every merged one, and the templates resolve these via DynamicResource.
                //
                // ⚠️ Deliberately NOT touched: SystemFillColorSolidNeutralBackground, which backs the
                // ComboBox drop-down. A popup floats over arbitrary content — other controls, other
                // windows — so a translucent one shows whatever happens to be underneath and stops being
                // readable. Drop-downs stay solid on purpose.
                SetToken(res, "ControlFillColorDefault", glass ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : surface);
                SetToken(res, "ControlFillColorSecondary", glass ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Lighten(surface, 0.08));
                SetToken(res, "ControlFillColorTertiary", glass ? Color.FromArgb(0x0B, 0xFF, 0xFF, 0xFF) : Lighten(surface, 0.04));
                SetToken(res, "ControlFillColorInputActive", glass ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Lighten(surface, 0.12));
                SetToken(res, "ControlStrokeColorDefault", Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));

                // Translucent washes of the accent for chips, badges, the brand drop-down and callouts. These used to
                // be cyan literals scattered through the XAML, which is why the Scripts/News/Changelog chips and the
                // Deobfuscator picker stayed cyan under every theme. Brand.xaml holds the same keys in cyan.
                foreach (var (key, alpha) in AccentWashes)
                    res[key] = Brush(Color.FromArgb(alpha, cyan.R, cyan.G, cyan.B));

                // Gradients
                res["BrandGradientBrush"] = Gradient(gStart, gEnd, purple, direction);
                res["BrandGradientBrightBrush"] = Gradient(Lighten(gStart, 0.28), Lighten(gEnd, 0.28), Lighten(purple, 0.28), direction);
                res["BrandGradientTriBrush"] = TriGradient(purple, cyan, gEnd, direction);

                // Primary buttons: the same gradient with each end lifted until the buttons' dark label reads on it
                // (vaehz's violet end gave 3.8:1). A no-op for bright palettes, including the default.
                res["BrandButtonGradientBrush"] = ButtonGradient(gStart, gEnd, purple, direction);
                res["BrandButtonGradientBrightBrush"] = Gradient(Lighten(LiftForLabel(gStart), 0.28), Lighten(LiftForLabel(gEnd), 0.28), Lighten(purple, 0.28), direction);

                // Glows (null when the glow is off via toggle or intensity => no effect). Intensity
                // scales both blur and opacity so Soft is tighter/subtler and Strong is big and neon.
                //
                // These keys exist ONLY here — Brand.xaml deliberately does not declare them. WPF treats
                // a null resource value as "not found" and continues searching the merged dictionaries,
                // so a fallback definition anywhere else silently defeats the off-switch. Keep it that
                // way: if you add BrandGlow* back to a dictionary, "glow off" stops working.
                double glowScale = intensity switch
                {
                    GlowIntensity.Off => 0,
                    GlowIntensity.Soft => 0.55,
                    GlowIntensity.Normal => 1.0,
                    GlowIntensity.Strong => 1.6,
                    _ => 1.0
                };
                bool glowsEnabled = glowOn && glowScale > 0;
                res["BrandGlowEffect"] = glowsEnabled ? Glow(glow, 18 * glowScale, 0.5) : null;
                res["BrandGlowSoft"] = glowsEnabled ? Glow(glow, 14 * glowScale, 0.4) : null;
                res["BrandGlowStrong"] = glowsEnabled ? Glow(glow, 30 * glowScale, 0.6) : null;

                // Aurora visibility (the AmbientBackground binds this)
                res["AuroraVisibility"] = aurora ? Visibility.Visible : Visibility.Collapsed;

                // The aurora's three drifting blobs, in the palette's own colours. They were hard-coded to the
                // default blue / purple / green, so every other preset and creator theme had off-brand light
                // behind the launcher.
                res["AuroraAccentBrush"] = Radial(cyan, 0x40);
                res["AuroraPurpleBrush"] = Radial(purple, 0x40);
                res["AuroraSecondaryBrush"] = Radial(gEnd, 0x30);

                // Accent — DynamicResource-backed across every WPF-UI control, so this is fully live.
                Accent.Apply(cyan, ThemeType.Dark);

                ApplyBrandArt(res, seasonal ?? CreatorTheme.Find(App.Settings?.Prop?.ActiveCreatorTheme));

                // Wallpaper — a user image behind the settings pages / launch menu. When there's no
                // file the background layer just stays on the deep ink (aurora still applies on top).
                ApplyWallpaper(res);

                // Animated (GIF) wallpaper — a looping GIF layered above the static wallpaper. The
                // GifBackground control consumes these resources and animates the frames itself.
                ApplyGifWallpaper(res);
            }
            finally
            {
                if (batch)
                {
                    res.EndInit();
                    _batching = false;
                }
            }

            _lastSignature = signature;

            // A different theme is showing now. Whichever window noticed (a dialog opening, the settings window coming
            // back to the front after midnight, the Appearance page itself), every open window's taskbar icon follows.
            // Doing this only in the window that noticed left the others on the old icon.
            string shown = (seasonal?.Id ?? "") + "|" + (App.Settings?.Prop?.ActiveCreatorTheme ?? "");
            if (shown != _shownTheme)
            {
                _shownTheme = shown;
                ApplyWindowIcons();
            }

            // The page also cares which run is on when nothing stands in for the saved theme (it was turned off for this
            // year, or it IS the saved theme): that run ending still has to take the page's notice away.
            string state = shown + "|" + RunningSeasonal?.Id;
            if (state != _themeState)
            {
                _themeState = state;
                ThemeStateChanged?.Invoke();
            }
        }

        // Includes the run in progress as well as what stands in, so the first recheck after a turned-off run ends
        // still re-applies (and so tells the Appearance page), although nothing on screen changes colour.
        private static string Signature(ThemePalette p, CreatorTheme? seasonal)
        {
            var s = App.Settings?.Prop;
            return string.Join("|", p.Accent, p.GradientStart, p.GradientEnd, p.Purple, p.Background,
                p.Surface, p.Hairline, p.Glow, p.GradientDirection, p.GlowIntensity, s?.EnableAurora, s?.EnableGlass, s?.EnableGlow,
                s?.ActiveCreatorTheme, seasonal?.Id, RunningSeasonal?.Id,
                s?.EnableWallpaper, s?.WallpaperLocation, s?.WallpaperOpacity,
                s?.EnableGifWallpaper, s?.GifWallpaperLocation, s?.GifWallpaperOpacity);
        }

        // ---- seasonal themes -------------------------------------------------------------------------

        // The seasonal theme standing in for the saved one today, or null. Each one takes over on its own days
        // (CreatorTheme.Season: 420 on April 20th, Halloween Oct 1 to Nov 1, Christmas Dec 24 to 26) in every
        // BeastStrap window, the launcher, the tray and the bootstrapper, without being written into the saved
        // theme, so the user's own theme is back by itself afterwards. Skipped when its yearly switch is off, once
        // it's been turned off for this year's run (from the Appearance notice, or by a theme choice during it: their
        // pick wins), and when it already is the saved theme. By the local date, like the days themselves. The rules
        // live in CreatorTheme.SeasonalOn, where seasoncheck can test them.
        public static CreatorTheme? ActiveSeasonal =>
            App.Settings?.Prop is Models.Persistable.Settings s ? CreatorTheme.SeasonalOn(Today, s) : null;

        // The seasonal run in progress today with its yearly switch on, standing in or not (CreatorTheme.Running).
        public static CreatorTheme? RunningSeasonal =>
            App.Settings?.Prop is Models.Persistable.Settings s ? CreatorTheme.Running(Today, s) : null;

        // The one clock the seasonal themes go by, so "is it on" and "turn it off for this year" always agree.
        public static DateTime Today => DateTime.Today;

        // ---- creator themes: the art ---------------------------------------------------------------

        // The creator theme actually showing: a seasonal one on its days (see above), else the saved one.
        public static CreatorTheme? ActiveCreator =>
            ActiveSeasonal ?? CreatorTheme.Find(App.Settings?.Prop?.ActiveCreatorTheme);

        // Brand art keys. Brand.xaml holds BeastStrap's own art under the image keys, so switching back
        // REMOVES the overrides from the root dictionary rather than writing defaults: a lookup then falls
        // through to Brand.xaml. (Writing null wouldn't work — WPF treats a null resource as "not found"
        // and keeps searching, which is the same trap the glow keys above are built around.)
        private static readonly string[] CreatorImageKeys = { "BrandAppIconImage", "BrandTitleBarIcon", "BrandSmallIconImage", "CreatorAvatarImage", "CreatorBannerImage" };

        private static void ApplyBrandArt(ResourceDictionary res, CreatorTheme? creator)
        {
            res["BrandDefaultArtVisibility"] = creator is null ? Visibility.Visible : Visibility.Collapsed;
            res["BrandCreatorArtVisibility"] = creator is null ? Visibility.Collapsed : Visibility.Visible;
            res["CreatorName"] = creator?.Name ?? "";
            res["CreatorHandle"] = creator?.Handle ?? "";
            res["CreatorTagline"] = creator?.Tagline ?? "";
            res["CreatorUrl"] = creator?.Url ?? "";
            res["CreatorLinkVisibility"] = creator?.HasLink == true ? Visibility.Visible : Visibility.Collapsed;
            res["CreatorLinkText"] = creator?.LinkText ?? "";
            res["CreatorLinkIcon"] = creator?.LinkIcon ?? Wpf.Ui.Common.SymbolRegular.Globe24;
            res["BrandEditionText"] = creator is null ? "" : $"{creator.Name} edition";

            if (creator is null)
            {
                foreach (string key in CreatorImageKeys)
                    res.Remove(key);
                return;
            }

            if (LoadImage(creator.AvatarUri) is ImageSource avatar)
            {
                res["BrandAppIconImage"] = avatar;
                res["CreatorAvatarImage"] = avatar;

                // The title bar and the tray menu draw their icon at 16-20 px. The .ico's small frames are made
                // for that size (Roblox Scripts and Sir Meme Hub get a simplified mark there), whereas the 256 px
                // avatar shrunk that far is a colour smear for a wordmark badge.
                var small = LoadIconFrame(creator.IconUri, 32) ?? avatar;
                res["BrandTitleBarIcon"] = small;
                res["BrandSmallIconImage"] = small;
            }

            // Only the settings window's Home hero draws the banner (see UseBanner), so the bootstrapper, the tray and
            // every watcher don't decode a 1400x322 image they never show. Removed when it can't be had, rather than
            // leaving the previous theme's banner up.
            if (_useBanner && LoadImage(creator.BannerUri) is ImageSource banner)
                res["CreatorBannerImage"] = banner;
            else
                res.Remove("CreatorBannerImage");
        }

        private static bool _useBanner;

        // Called by the settings window, the only place the creator banner shows (the Home page hero).
        public static void UseBanner()
        {
            if (_useBanner)
                return;
            _useBanner = true;

            if (ActiveCreator is CreatorTheme creator && LoadImage(creator.BannerUri) is ImageSource banner)
                Application.Current.Resources["CreatorBannerImage"] = banner;
        }

        // The taskbar / alt-tab icon for a window: the user's own custom icon wins, then the active creator
        // theme's, else the standard BeastStrap icon. WpfUiWindow applies this to every window as it
        // opens, which is also what makes a custom icon stick across launches (it used to be applied only
        // while the Appearance page was open, despite the page promising "everywhere on the next launch").
        public static ImageSource? ResolveWindowIcon()
        {
            string path = App.Settings?.Prop?.CustomAppIconLocation ?? "";
            var creator = ActiveCreator;

            // Every window asks as it opens, so decode once per choice rather than once per window. Keyed on
            // both inputs, and a failed decode is cached as its fallback too, so a broken custom icon is
            // logged once instead of once per window.
            string key = path + "|" + (creator?.Id ?? "");
            if (key == _iconKey)
                return _icon;

            ImageSource? icon = null;

            // This runs in every window's constructor, including the crash dialog, so nothing about a bad
            // custom path (a malformed one throws in Path/Uri, not in the loader) may escape.
            if (path.Length > 0)
            {
                try
                {
                    // Capped at 256 px, the most any window icon needs, because this is decoded in every process
                    // that opens a window and a photo picked as the icon would otherwise sit in each at full size.
                    // An .ico keeps all its frames instead, so WPF picks the right one for each size.
                    if (File.Exists(path))
                    {
                        string uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
                        icon = path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ? LoadIcon(uri) : LoadImage(uri, 256);
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("ThemeManager::ResolveWindowIcon", ex);
                }
            }

            if (icon is null && creator is not null)
                icon = LoadIcon(creator.IconUri);
            icon ??= LoadIcon("pack://application:,,,/BeastStrap.ico");

            _icon = icon;
            _iconKey = key;
            return _icon;
        }

        private static string? _iconKey;
        private static ImageSource? _icon;

        public static void ApplyWindowIcons()
        {
            // Not a launch dialog: that shows the icon picked under Appearance > Bootstrapper > Icon, and Apply now runs
            // this in every process, so the bootstrapper's own dialog could otherwise get the theme's icon at midnight.
            // Nor a window that never shows an icon (the watcher's hidden tray-menu host). With none left, which is also
            // the case before the first window opens, nothing is decoded: each window resolves its own as it opens.
            var windows = Application.Current.Windows.OfType<Window>()
                .Where(w => w is not IBootstrapperDialog && (w is not BeastStrap.UI.Elements.Base.WpfUiWindow ui || ui.UsesThemeIcon))
                .ToList();
            if (windows.Count == 0)
                return;

            var icon = ResolveWindowIcon();
            if (icon is null)
                return;

            foreach (var window in windows)
                window.Icon = icon;
        }

        private static ImageSource? LoadImage(string uri, int decodeWidth = 0)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(uri, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // no file lock on a user's custom icon
                if (decodeWidth > 0)
                    bmp.DecodePixelWidth = decodeWidth;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ThemeManager::LoadImage", ex);
                return null;
            }
        }

        // The single .ico frame closest to `size` px, for places that draw a fixed small icon themselves.
        private static ImageSource? LoadIconFrame(string uri, int size)
        {
            try
            {
                // Default, not OnLoad: OnLoad decodes all seven frames up front, and only one of them is wanted.
                var decoder = BitmapDecoder.Create(new Uri(uri, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.Default);
                BitmapFrame? best = null;
                foreach (var frame in decoder.Frames)
                    if (best is null || Math.Abs(frame.PixelWidth - size) < Math.Abs(best.PixelWidth - size))
                        best = frame;
                best?.Freeze();
                return best;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ThemeManager::LoadIconFrame", ex);
                return null;
            }
        }

        // An .ico through BitmapFrame keeps its IconBitmapDecoder, which lets WPF pick the right size for
        // the taskbar and the title bar instead of shrinking one big frame.
        private static ImageSource? LoadIcon(string uri)
        {
            try
            {
                var frame = BitmapFrame.Create(new Uri(uri, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                frame.Freeze();
                return frame;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ThemeManager::LoadIcon", ex);
                return null;
            }
        }

        /// <summary>Publishes a WPF-UI theme token as both its Color and its matching *Brush resource.</summary>
        /// <remarks>
        /// Both are needed — see the note at the call site. WPF-UI's templates reference some tokens as
        /// <c>{DynamicResource Foo}</c> and others as <c>{DynamicResource FooBrush}</c>, sometimes both
        /// within one control, so setting a single form leaves half the UI on the old value.
        /// </remarks>
        private static void SetToken(ResourceDictionary res, string key, Color value)
        {
            res[key] = value;
            res[key + "Brush"] = Brush(value);
        }

        private static SolidColorBrush Brush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // Brand gradient. Public so the Appearance cards can draw each theme's own ring with it.
        // `third` is the palette's third colour, preferred as the middle stop when one is needed (see below).
        public static LinearGradientBrush Gradient(Color a, Color b, Color? third = null, GradientDirection direction = GradientDirection.Horizontal)
        {
            (Point start, Point end) = GradientPoints(direction);
            var g = new LinearGradientBrush
            {
                StartPoint = start,
                EndPoint = end
            };
            g.GradientStops.Add(new GradientStop(a, 0));

            // Near-complementary ends (Sir Meme Hub's pink to green) blend through grey in a straight RGB
            // mix, which made every button, name and ring in that theme muddy in the middle. Past 90 degrees
            // of hue, add a middle stop: the palette's own third colour when it lies on the short way round the
            // colour wheel between the ends (Sir Meme Hub's violet), otherwise the hue midpoint. Close hues
            // (every other shipped theme and preset) keep their plain two-stop gradient.
            if (HueDistance(a, b) > 90)
                g.GradientStops.Add(new GradientStop(third is Color t && OnShortArc(a, b, t) ? LiftTo(t, a, b) : HueMidpoint(a, b), 0.5));

            g.GradientStops.Add(new GradientStop(b, 1));
            g.Freeze();
            return g;
        }

        // Gradient for surfaces that carry the dark #07131A primary-button label.
        public static LinearGradientBrush ButtonGradient(Color a, Color b, Color? third = null, GradientDirection direction = GradientDirection.Horizontal) =>
            Gradient(LiftForLabel(a), LiftForLabel(b), third is Color t ? LiftForLabel(t) : null, direction);

        // Lighten until the dark button label (#07131A) is at least 4.5:1 on it.
        public static Color LiftForLabel(Color c)
        {
            const double labelLum = 0.0063;   // #07131A
            Color x = c;
            for (int i = 1; i <= 20 && (Luminance(x) + 0.05) / (labelLum + 0.05) < 4.5; i++)
                x = Lighten(c, i * 0.04);
            return x;
        }

        private static bool OnShortArc(Color a, Color b, Color t)
        {
            ToHsv(a, out double ha, out _, out _);
            ToHsv(b, out double hb, out _, out _);
            ToHsv(t, out double ht, out double st, out _);
            if (st < 0.3)
                return false;
            double span = ((hb - ha) % 360 + 540) % 360 - 180;   // signed short way a -> b
            double off = ((ht - ha) % 360 + 540) % 360 - 180;    // signed a -> t
            return Math.Sign(off) == Math.Sign(span) && Math.Abs(off) > 10 && Math.Abs(off) < Math.Abs(span) - 10;
        }

        // Lift a middle colour to at least the darker end's brightness (see HueMidpoint).
        private static Color LiftTo(Color mid, Color a, Color b)
        {
            double floor = Math.Min(Luminance(a), Luminance(b));
            Color x = mid;
            for (int i = 1; i <= 20 && Luminance(x) < floor; i++)
                x = Lighten(mid, i * 0.05);
            return x;
        }

        private static void ToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
            s = max == 0 ? 0 : d / max;
            v = max;
        }

        private static Color FromHsv(double h, double s, double v)
        {
            double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
            (double r, double g, double b) = h < 60 ? (c, x, 0d) : h < 120 ? (x, c, 0d) : h < 180 ? (0d, c, x)
                                           : h < 240 ? (0d, x, c) : h < 300 ? (x, 0d, c) : (c, 0d, x);
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        private static double HueDistance(Color a, Color b)
        {
            ToHsv(a, out double ha, out double sa, out _);
            ToHsv(b, out double hb, out double sb, out _);
            if (sa < 0.15 || sb < 0.15)
                return 0;   // a grey end has no meaningful hue
            double d = Math.Abs(ha - hb) % 360;
            return d > 180 ? 360 - d : d;
        }

        private static Color HueMidpoint(Color a, Color b)
        {
            ToHsv(a, out double ha, out double sa, out double va);
            ToHsv(b, out double hb, out double sb, out double vb);
            double diff = ((hb - ha) % 360 + 540) % 360 - 180;   // signed short way, -180..180
            double h = (ha + diff / 2 + 360) % 360;
            Color mid = FromHsv(h, (sa + sb) / 2, (va + vb) / 2);

            // Primary buttons put a dark label on this gradient, and the short-way midpoint can be a deep blue
            // (pink to green passes 231 deg) that the label disappears into. Lift it toward white until it's at
            // least as bright as the darker end, so the middle is never harder to read than the ends.
            double floor = Math.Min(Luminance(a), Luminance(b));
            for (int i = 1; i <= 20 && Luminance(mid) < floor; i++)
                mid = Lighten(FromHsv(h, (sa + sb) / 2, (va + vb) / 2), i * 0.05);
            return mid;
        }

        // WCAG relative luminance.
        private static double Luminance(Color c)
        {
            static double Lin(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }

        // A soft blob: the colour at `alpha` in the middle, fading to fully transparent at the edge.
        private static RadialGradientBrush Radial(Color c, byte alpha)
        {
            var g = new RadialGradientBrush(Color.FromArgb(alpha, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
            g.Freeze();
            return g;
        }

        private static (Point Start, Point End) GradientPoints(GradientDirection direction)
        {
            return direction switch
            {
                GradientDirection.Vertical => (new Point(0, 0), new Point(0, 1)),
                GradientDirection.Diagonal => (new Point(0, 0), new Point(1, 1)),
                _ => (new Point(0, 0), new Point(1, 0))
            };
        }

        private static LinearGradientBrush TriGradient(Color a, Color b, Color c, GradientDirection direction)
        {
            (Point start, Point end) = GradientPoints(direction);
            var g = new LinearGradientBrush
            {
                StartPoint = start,
                EndPoint = end
            };
            g.GradientStops.Add(new GradientStop(a, 0));
            g.GradientStops.Add(new GradientStop(b, 0.55));
            g.GradientStops.Add(new GradientStop(c, 1));
            g.Freeze();
            return g;
        }

        private static DropShadowEffect Glow(Color c, double blur, double opacity)
        {
            var e = new DropShadowEffect
            {
                Color = c,
                BlurRadius = blur,
                ShadowDepth = 0,
                Opacity = opacity
            };
            e.Freeze();
            return e;
        }

        public static Color Lighten(Color c, double amount)
        {
            byte L(byte v) => (byte)Math.Clamp(v + (255 - v) * amount, 0, 255);
            return Color.FromArgb(c.A, L(c.R), L(c.G), L(c.B));
        }

        private static void ApplyWallpaper(ResourceDictionary res)
        {
            var s = App.Settings?.Prop;
            bool enabled = s?.EnableWallpaper == true && !string.IsNullOrWhiteSpace(s.WallpaperLocation);
            string? path = enabled ? s!.WallpaperLocation.Trim() : null;

            if (path != null && File.Exists(path))
            {
                try
                {
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.UriSource = new Uri(path);
                    img.EndInit();
                    img.Freeze();

                    res["WallpaperImageSource"] = img;
                    res["WallpaperVisibility"] = Visibility.Visible;
                    res["WallpaperOpacity"] = Math.Clamp(s!.WallpaperOpacity, 0.1, 1.0);
                    return;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("ThemeManager::ApplyWallpaper", ex);
                }
            }

            res["WallpaperImageSource"] = null;
            res["WallpaperVisibility"] = Visibility.Collapsed;
            res["WallpaperOpacity"] = 1.0;
        }

        private static void ApplyGifWallpaper(ResourceDictionary res)
        {
            var s = App.Settings?.Prop;
            bool enabled = s?.EnableGifWallpaper == true && !string.IsNullOrWhiteSpace(s.GifWallpaperLocation);
            string? path = enabled ? s!.GifWallpaperLocation.Trim() : null;

            // A URL (e.g. a Giphy link) is valid without a local file; only local paths need
            // to exist on disk.
            if (path != null && (IsRemote(path) || File.Exists(path)))
            {
                res["GifBackgroundPath"] = path;
                res["GifBackgroundVisibility"] = Visibility.Visible;
                res["GifBackgroundOpacity"] = Math.Clamp(s!.GifWallpaperOpacity, 0.1, 1.0);
                return;
            }

            res["GifBackgroundPath"] = "";
            res["GifBackgroundVisibility"] = Visibility.Collapsed;
            res["GifBackgroundOpacity"] = 1.0;
        }

        private static bool IsRemote(string path)
            => path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
}
