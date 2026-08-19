namespace BeastStrap.Models
{
    // A named collection of fast flags users can apply in one click. Bundled presets
    // are defined in Utility/FastFlagLibrary.cs; this class is the display/apply model
    // shared by the library dialog and any future import source.
    public class FastFlagPreset
    {
        public string Name { get; set; } = null!;
        public string Category { get; set; } = "General";
        public string Description { get; set; } = "";
        public Dictionary<string, object> Flags { get; set; } = new();
    }
}