using Microsoft.Win32;

namespace BeastStrap.Extensions
{
    public static class ThemeEx
    {
        // BeastStrap is dark-only — every theme resolves to Dark regardless of the stored
        // setting or the OS light/dark preference.
        public static Theme GetFinal(this Theme dialogTheme) => Theme.Dark;
    }
}
