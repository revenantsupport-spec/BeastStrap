using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

using Wpf.Ui.Appearance;

using BeastStrap.Enums;
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

        // Apply the saved palette + effect toggles.
        public static void ApplyFromSettings()
        {
            var palette = App.Settings?.Prop?.Palette ?? new ThemePalette();
            if (Signature(palette) == _lastSignature)
                return;
            Apply(palette);
        }

        public static void Apply(ThemePalette p)
        {
            var res = Application.Current.Resources;

            Color cyan = Parse(p.Accent, Color.FromRgb(0x38, 0xBD, 0xF8));
            Color gStart = Parse(p.GradientStart, cyan);
            Color gEnd = Parse(p.GradientEnd, Color.FromRgb(0x7D, 0xD3, 0xFC));
            Color purple = Parse(p.Purple, Color.FromRgb(0xA7, 0x8B, 0xFA));
            Color ink = Parse(p.Background, Color.FromRgb(0x07, 0x0C, 0x16));
            Color surface = Parse(p.Surface, Color.FromRgb(0x0E, 0x16, 0x24));
            Color hairline = Parse(p.Hairline, Color.FromRgb(0x1B, 0x29, 0x40));
            Color glow = Parse(p.Glow, cyan);

            bool glass = App.Settings?.Prop?.EnableGlass ?? true;
            bool glowOn = App.Settings?.Prop?.EnableGlow ?? true;
            bool aurora = App.Settings?.Prop?.EnableAurora ?? true;

            // Direction sweeps the gradient differently; intensity scales the glow strength.
            GradientDirection direction = p.GradientDirection;
            GlowIntensity intensity = p.GlowIntensity;

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

            // Gradients
            res["BrandGradientBrush"] = Gradient(gStart, gEnd, direction);
            res["BrandGradientBrightBrush"] = Gradient(Lighten(gStart, 0.28), Lighten(gEnd, 0.28), direction);
            res["BrandGradientTriBrush"] = TriGradient(purple, cyan, gEnd, direction);

            // Glows (null when the glow is off via toggle or intensity => no effect). Intensity
            // scales both blur and opacity so Soft is tighter/subtler and Strong is big and neon.
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

            // Wallpaper — a user image behind the settings pages / launch menu. When there's no
            // file the background layer just stays on the deep ink (aurora still applies on top).
            ApplyWallpaper(res);

            // Animated (GIF) wallpaper — a looping GIF layered above the static wallpaper. The
            // GifBackground control consumes these resources and animates the frames itself.
            ApplyGifWallpaper(res);

            // Accent — DynamicResource-backed across every WPF-UI control, so this is fully live.
            Accent.Apply(cyan, ThemeType.Dark);

            _lastSignature = Signature(p);
        }

        private static string Signature(ThemePalette p)
        {
            var s = App.Settings?.Prop;
            return string.Join("|", p.Accent, p.GradientStart, p.GradientEnd, p.Purple, p.Background,
                p.Surface, p.Hairline, p.Glow, p.GradientDirection, p.GlowIntensity,
                s?.EnableAurora, s?.EnableGlass, s?.EnableGlow,
                s?.EnableWallpaper, s?.WallpaperLocation, s?.WallpaperOpacity,
                s?.EnableGifWallpaper, s?.GifWallpaperLocation, s?.GifWallpaperOpacity, s?.GifWallpaperStretch);
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
                res["GifBackgroundStretch"] = (s.GifWallpaperStretch) switch
                {
                    BackgroundFit.Fill => Stretch.UniformToFill,
                    BackgroundFit.Stretch => Stretch.Fill,
                    _ => Stretch.Uniform
                };
                return;
            }

            res["GifBackgroundPath"] = "";
            res["GifBackgroundVisibility"] = Visibility.Collapsed;
            res["GifBackgroundOpacity"] = 1.0;
            res["GifBackgroundStretch"] = Stretch.Uniform;
        }

        private static bool IsRemote(string path)
            => path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        private static SolidColorBrush Brush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
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

        private static LinearGradientBrush Gradient(Color a, Color b, GradientDirection direction)
        {
            (Point start, Point end) = GradientPoints(direction);
            var g = new LinearGradientBrush
            {
                StartPoint = start,
                EndPoint = end
            };
            g.GradientStops.Add(new GradientStop(a, 0));
            g.GradientStops.Add(new GradientStop(b, 1));
            g.Freeze();
            return g;
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

        private static Color Lighten(Color c, double amount)
        {
            byte L(byte v) => (byte)Math.Clamp(v + (255 - v) * amount, 0, 255);
            return Color.FromArgb(c.A, L(c.R), L(c.G), L(c.B));
        }
    }
}
