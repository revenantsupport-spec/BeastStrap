using BeastStrap.Enums.FlagPresets;

namespace BeastStrap.UI.ViewModels.Settings
{
    public class FastFlagsViewModel : NotifyPropertyChangedViewModel
    {
        // STATIC on purpose. This holds the user's entire flag set while the "Reset everything to
        // defaults" toggle is on, and the page's RequestPageReloadEvent handler rebuilds this view
        // model — which used to throw the backup away with the old instance, so toggling the switch
        // back off restored nothing and the flags were gone for good the next time the user hit
        // Save. Living on the type means it survives the swap.
        private static Dictionary<string, object>? _preResetFlags;

        // Raised when a reset swap happens so the page can rebuild this VM (so the preset
        // controls re-read App.FastFlags) and refresh the raw editor grid.
        public event EventHandler? RequestPageReloadEvent;

        public bool UseFastFlagManager
        {
            get => App.Settings.Prop.UseFastFlagManager;
            set => App.Settings.Prop.UseFastFlagManager = value;
        }

        public IReadOnlyDictionary<MSAAMode, string?> MSAALevels => FastFlagManager.MSAAModes;

        public MSAAMode SelectedMSAALevel
        {
            get => MSAALevels.FirstOrDefault(x => x.Value == App.FastFlags.GetPreset("Rendering.MSAA")).Key;
            set => App.FastFlags.SetPreset("Rendering.MSAA", MSAALevels[value]);
        }

        public IReadOnlyDictionary<RenderingMode, string> RenderingModes => FastFlagManager.RenderingModes;

        public RenderingMode SelectedRenderingMode
        {
            get => App.FastFlags.GetPresetEnum(RenderingModes, "Rendering.Mode", "True");
            set => App.FastFlags.SetPresetEnum("Rendering.Mode", value.ToString(), "True");
        }

        public bool GraySky
        {
            get => App.FastFlags.GetPreset("Rendering.GraySky") == "True";
            set => App.FastFlags.SetPreset("Rendering.GraySky", value ? "True" : null);
        }

        public bool DisableGrass
        {
            get => App.FastFlags.GetPreset("Rendering.Grass.Max") == "0";
            set
            {
                App.FastFlags.SetPreset("Rendering.Grass.Max", value ? "0" : null);
                App.FastFlags.SetPreset("Rendering.Grass.Min", value ? "0" : null);
            }
        }

        public bool FixDisplayScaling
        {
            get => App.FastFlags.GetPreset("Rendering.DisableScaling") == "True";
            set => App.FastFlags.SetPreset("Rendering.DisableScaling", value ? "True" : null);
        }

        public IReadOnlyDictionary<TextureQuality, string?> TextureQualities => FastFlagManager.TextureQualityLevels;

        public TextureQuality SelectedTextureQuality
        {
            get => TextureQualities.Where(x => x.Value == App.FastFlags.GetPreset("Rendering.TextureQuality.Level")).FirstOrDefault().Key;
            set
            {
                if (value == TextureQuality.Default)
                {
                    App.FastFlags.SetPreset("Rendering.TextureQuality", null);
                }
                else
                {
                    App.FastFlags.SetPreset("Rendering.TextureQuality.OverrideEnabled", "True");
                    App.FastFlags.SetPreset("Rendering.TextureQuality.Level", TextureQualities[value]);
                }
            }
        }

        public IReadOnlyDictionary<FramerateLimit, string?> FramerateLimits => FastFlagManager.FramerateLimits;

        public FramerateLimit SelectedFramerateLimit
        {
            get => FramerateLimits.FirstOrDefault(x => x.Value == App.FastFlags.GetPreset("Rendering.Framerate")).Key;
            set
            {
                App.FastFlags.SetPreset("Rendering.Framerate", FramerateLimits[value]);

                // A custom framerate above 240 is silently clamped to 240 unless Roblox's hard
                // cap flag is lifted. Turn it off for any non-default choice, clear it otherwise.
                App.FastFlags.SetPreset("Rendering.FpsCapTo240", value == FramerateLimit.Default ? null : "False");
            }
        }

        public bool ResetConfiguration
        {
            get => _preResetFlags is not null;

            set
            {
                if (value)
                {
                    // Don't overwrite an existing backup — a second "on" after the VM was rebuilt
                    // would otherwise snapshot the already-cleared dictionary and lose the real one.
                    _preResetFlags ??= new(App.FastFlags.Prop);
                    App.FastFlags.Prop.Clear();
                }
                else if (_preResetFlags is not null)
                {
                    App.FastFlags.Prop = _preResetFlags;
                    _preResetFlags = null;
                }
                else
                {
                    return; // nothing was ever backed up, so there is nothing to restore
                }

                RequestPageReloadEvent?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
