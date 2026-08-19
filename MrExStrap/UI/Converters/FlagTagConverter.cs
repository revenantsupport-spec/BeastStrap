using System;
using System.Globalization;
using System.Windows.Data;

namespace BeastStrap.UI.Converters
{
    // Classifies a FastFlag by its name into a short human tag, so the editor grid can show what a
    // flag roughly does at a glance. Pure heuristic substring matching — best-effort, never authoritative.
    public class FlagTagConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string name = value as string ?? "";

            if (name.Length == 0)
                return "";

            if (Contains(name, "Telemetry", "Analytics", "Ares", "Tencent"))
                return "Telemetry";

            if (Contains(name, "MSAA", "Texture", "Grass", "Sky", "Shadow", "Lighting", "Vulkan", "D3D",
                         "OpenGL", "Graphics", "Render", "Quality", "FRM", "Voxel", "Grid", "Reflection"))
                return "Graphics";

            if (Contains(name, "TargetFps", "Framerate", "TaskScheduler", "Fps", "Lod", "LevelOfDetail",
                         "Distance", "Cull", "Preload", "Cache", "Memory", "Physics"))
                return "Performance";

            if (Contains(name, "Verify", "Whitelist", "Menu", "Gui", "InGame", "Notification", "Toast", "Chat"))
                return "UI";

            if (name.Contains("Debug", StringComparison.OrdinalIgnoreCase))
                return "Experimental";

            return "Other";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static bool Contains(string haystack, params string[] needles)
        {
            foreach (string n in needles)
                if (haystack.Contains(n, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }
    }
}
