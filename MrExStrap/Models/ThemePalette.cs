using BeastStrap.Enums;

namespace BeastStrap.Models
{
    // A user-editable colour palette for the app. Stored as hex strings (System.Text.Json friendly,
    // no custom Color converter needed). ThemeManager turns these into the live brand brushes.
    public class ThemePalette
    {
        public string Accent { get; set; } = "#38BDF8";
        public string GradientStart { get; set; } = "#3B82F6";
        public string GradientEnd { get; set; } = "#7DD3FC";
        public string Purple { get; set; } = "#A78BFA";
        public string Background { get; set; } = "#070C16";
        public string Surface { get; set; } = "#0E1624";
        public string Hairline { get; set; } = "#1B2940";
        public string Glow { get; set; } = "#38BDF8";
        public GradientDirection GradientDirection { get; set; } = GradientDirection.Horizontal;
        public GlowIntensity GlowIntensity { get; set; } = GlowIntensity.Normal;

        public ThemePalette Clone() => (ThemePalette)MemberwiseClone();
    }
}
