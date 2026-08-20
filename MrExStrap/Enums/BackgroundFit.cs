namespace BeastStrap.Enums
{
    // How the user's animated (GIF) background is sized inside its surface. "Fit" shows the
    // whole image with no cropping (the default — avoids the zoomed-in look), "Fill" covers
    // the whole surface and may crop the edges, "Stretch" reshapes it to the surface exactly.
    public enum BackgroundFit
    {
        [EnumName(StaticName = "Fit (whole image)")]
        Fit,
        [EnumName(StaticName = "Fill (cover window)")]
        Fill,
        [EnumName(StaticName = "Stretch (reshape)")]
        Stretch
    }
}