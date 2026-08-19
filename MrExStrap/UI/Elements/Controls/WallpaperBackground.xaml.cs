using System.Windows.Controls;

namespace BeastStrap.UI.Elements.Controls
{
    /// <summary>
    /// User wallpaper backdrop for hero surfaces (settings window, launch menu). Loaded by
    /// Utility.ThemeManager.ApplyWallpaper into app resources; this control just renders it.
    /// </summary>
    public partial class WallpaperBackground : UserControl
    {
        public WallpaperBackground()
        {
            InitializeComponent();
        }
    }
}