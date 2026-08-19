using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace BeastStrap.UI.Elements.Controls
{
    // Attached property that turns a Border into a colour-coded "flag chip". It renders the fast-flag name
    // with its type prefix (FFlag / DFInt / FString / FLog and their D*/S* variants) highlighted in a colour
    // keyed to the flag's value type, and tints the chip's fill + border to match. Purely presentational —
    // used by the FastFlags allowlist display to make a flat monospace list read as lively, typed tags.
    //
    // Usage:  <Border Style="{StaticResource FlagChipBorder}" controls:FlagChip.Flag="FIntFRMMaxGrassDistance" />
    public static class FlagChip
    {
        private static readonly System.Windows.Media.FontFamily Mono = new("Consolas");
        private static readonly Brush RestText = Frozen(new SolidColorBrush(Color.FromRgb(0xCE, 0xD3, 0xDA)));

        // Checked in order; the D*/S* forms differ in their first letter from the bare forms, so there's no
        // shadowing, but the compound prefixes are listed first for clarity.
        private static readonly string[] Prefixes =
        {
            "DFString", "SFString", "FString",
            "DFFlag", "SFFlag", "FFlag",
            "DFInt", "SFInt", "FInt",
            "DFLog", "SFLog", "FLog",
        };

        public static readonly DependencyProperty FlagProperty =
            DependencyProperty.RegisterAttached(
                "Flag", typeof(string), typeof(FlagChip),
                new PropertyMetadata(null, OnFlagChanged));

        public static string GetFlag(DependencyObject o) => (string)o.GetValue(FlagProperty);
        public static void SetFlag(DependencyObject o, string v) => o.SetValue(FlagProperty, v);

        private static void OnFlagChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Border border)
                return;

            string flag = e.NewValue as string ?? "";
            string prefix = MatchPrefix(flag);
            Color accent = AccentFor(prefix);

            var text = new TextBlock { FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.NoWrap };
            if (prefix.Length > 0)
            {
                text.Inlines.Add(new Run(prefix) { Foreground = Frozen(new SolidColorBrush(accent)), FontWeight = FontWeights.Bold });
                text.Inlines.Add(new Run(flag.Substring(prefix.Length)) { Foreground = RestText });
            }
            else
            {
                text.Inlines.Add(new Run(flag) { Foreground = RestText });
            }

            border.Child = text;
            border.Background = Frozen(new SolidColorBrush(Color.FromArgb(0x22, accent.R, accent.G, accent.B)));
            border.BorderBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B)));
        }

        private static string MatchPrefix(string flag)
        {
            foreach (var p in Prefixes)
                if (flag.StartsWith(p, StringComparison.Ordinal))
                    return p;
            return "";
        }

        // bool = green, int = cyan, string = purple, log = amber, unknown = neutral.
        private static Color AccentFor(string prefix)
        {
            if (prefix.EndsWith("Flag", StringComparison.Ordinal)) return Color.FromRgb(0x7E, 0xDB, 0xA6);
            if (prefix.EndsWith("Int", StringComparison.Ordinal)) return Color.FromRgb(0x6F, 0xD3, 0xE8);
            if (prefix.EndsWith("String", StringComparison.Ordinal)) return Color.FromRgb(0xC2, 0xA9, 0xEC);
            if (prefix.EndsWith("Log", StringComparison.Ordinal)) return Color.FromRgb(0xE7, 0xC4, 0x82);
            return Color.FromRgb(0x9A, 0xA0, 0xA6);
        }

        private static Brush Frozen(Brush b)
        {
            if (b.CanFreeze) b.Freeze();
            return b;
        }
    }
}
