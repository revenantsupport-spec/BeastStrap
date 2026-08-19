namespace BeastStrap.Enums
{
    // How the brand gradient sweeps across a surface. Stored on the palette so presets can
    // carry their own look; ThemeManager builds the brush from this each Apply.
    public enum GradientDirection
    {
        [EnumName(StaticName = "Side to side")]
        Horizontal,
        [EnumName(StaticName = "Top to bottom")]
        Vertical,
        [EnumName(StaticName = "Diagonal")]
        Diagonal
    }

    // Strength of the neon glow on the wordmark, hero cards and accent edges. "Off" disables
    // the glow entirely; the existing EnableGlow toggle stays as a global master switch.
    public enum GlowIntensity
    {
        [EnumName(StaticName = "Off")]
        Off,
        [EnumName(StaticName = "Soft")]
        Soft,
        [EnumName(StaticName = "Normal")]
        Normal,
        [EnumName(StaticName = "Strong")]
        Strong
    }
}