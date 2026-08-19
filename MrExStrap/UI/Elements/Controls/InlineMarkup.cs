using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace BeastStrap.UI.Elements.Controls
{
    // Attached property that renders a tiny subset of Markdown into a TextBlock's Inlines: inline `code`
    // spans and \n line breaks. Used by the News page for Roblox release notes, whose text carries `code`
    // fences around API tokens (Class.X, Enum.X, table.freeze, …). A code span written in Discourse's
    // `link|display` form is shown as just the display half.
    //
    // Set it with:  <TextBlock controls:InlineMarkup.Markup="{Binding Text}" TextWrapping="Wrap" />
    public static class InlineMarkup
    {
        // Subtle inline-code chip: faint cyan wash + cyan text, matching the neon brand.
        private static readonly Brush CodeBackground = Freeze(new SolidColorBrush(Color.FromArgb(38, 34, 211, 238)));
        private static readonly Brush CodeForeground = Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0xE9, 0xF5)));
        private static readonly System.Windows.Media.FontFamily CodeFont = new("Consolas");

        public static readonly DependencyProperty MarkupProperty =
            DependencyProperty.RegisterAttached(
                "Markup", typeof(string), typeof(InlineMarkup),
                new PropertyMetadata(null, OnMarkupChanged));

        public static string GetMarkup(DependencyObject obj) => (string)obj.GetValue(MarkupProperty);
        public static void SetMarkup(DependencyObject obj, string value) => obj.SetValue(MarkupProperty, value);

        private static void OnMarkupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock tb)
                return;

            tb.Inlines.Clear();
            string text = e.NewValue as string ?? "";
            if (text.Length == 0)
                return;

            // Split on backticks: odd-indexed segments are code, even-indexed are plain text.
            string[] parts = text.Split('`');
            for (int i = 0; i < parts.Length; i++)
            {
                string seg = parts[i];
                if (seg.Length == 0)
                    continue;

                bool isCode = (i % 2) == 1;
                if (isCode)
                {
                    // `link|display` → keep just the display half.
                    int bar = seg.LastIndexOf('|');
                    if (bar >= 0 && bar < seg.Length - 1)
                        seg = seg.Substring(bar + 1);

                    tb.Inlines.Add(new Run(seg)
                    {
                        FontFamily = CodeFont,
                        Background = CodeBackground,
                        Foreground = CodeForeground,
                    });
                }
                else
                {
                    AddPlainWithBreaks(tb, seg);
                }
            }
        }

        private static void AddPlainWithBreaks(TextBlock tb, string seg)
        {
            string[] lines = seg.Split('\n');
            for (int j = 0; j < lines.Length; j++)
            {
                if (j > 0)
                    tb.Inlines.Add(new LineBreak());
                if (lines[j].Length > 0)
                    tb.Inlines.Add(new Run(lines[j]));
            }
        }

        private static Brush Freeze(Brush b)
        {
            if (b.CanFreeze)
                b.Freeze();
            return b;
        }
    }
}
