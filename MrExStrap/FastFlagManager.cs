using BeastStrap.Enums.FlagPresets;

namespace BeastStrap
{
    public class FastFlagManager : JsonManager<Dictionary<string, object>>
    {
        private Dictionary<string, object> OriginalProp = new();

        public override string ClassName => nameof(FastFlagManager);

        public override string LOG_IDENT_CLASS => ClassName;

        public override string FileName => "ClientAppSettings.json";

        // Which Versions Manager profile's flag set this manager reads/writes. Empty =>
        // the currently active profile. The FastFlags editor repoints this to edit other
        // profiles' flags without changing which profile actually launches.
        public string EditingProfileId { get; set; } = "";

        public override string FileLocation
        {
            get
            {
                string id = EditingProfileId;
                if (string.IsNullOrEmpty(id))
                    id = App.Settings?.Prop?.ActiveVersionProfileId ?? "";

                // No profile yet (fresh install before the profile list is seeded) ->
                // fall back to the legacy single global file.
                if (string.IsNullOrEmpty(id))
                    return Path.Combine(Paths.Modifications, "ClientSettings", FileName);

                return Path.Combine(Paths.FastFlagProfiles, $"{id}.json");
            }
        }

        public bool Changed => !OriginalProp.SequenceEqual(Prop);

        public static IReadOnlyDictionary<string, string> PresetFlags = new Dictionary<string, string>
        {
            { "Rendering.ManualFullscreen", "FFlagHandleAltEnterFullscreenManually" },
            { "Rendering.DisableScaling", "DFFlagDisableDPIScale" },
            { "Rendering.MSAA", "FIntDebugForceMSAASamples" },

            { "Rendering.TextureQuality.OverrideEnabled", "DFFlagTextureQualityOverrideEnabled" },
            { "Rendering.TextureQuality.Level", "DFIntTextureQualityOverride" },

            { "Rendering.Framerate", "DFIntTaskSchedulerTargetFps" },

            // Rendering mode — force the graphics API (all three are on Roblox's allowlist).
            { "Rendering.Mode.D3D11", "FFlagDebugGraphicsPreferD3D11" },
            { "Rendering.Mode.Vulkan", "FFlagDebugGraphicsPreferVulkan" },
            { "Rendering.Mode.OpenGL", "FFlagDebugGraphicsPreferOpenGL" },

            // Small allowlisted visual/performance tweaks.
            { "Rendering.GraySky", "FFlagDebugSkyGray" },
            { "Rendering.Grass.Max", "FIntFRMMaxGrassDistance" },
            { "Rendering.Grass.Min", "FIntFRMMinGrassDistance" },
        };

        public static IReadOnlyDictionary<MSAAMode, string?> MSAAModes => new Dictionary<MSAAMode, string?>
        {
            { MSAAMode.Default, null },
            { MSAAMode.x1, "1" },
            { MSAAMode.x2, "2" },
            { MSAAMode.x4, "4" },
            { MSAAMode.x8, "8" }
        };

        public static IReadOnlyDictionary<RenderingMode, string> RenderingModes => new Dictionary<RenderingMode, string>
        {
            { RenderingMode.Default, "None" },
            { RenderingMode.D3D11, "D3D11" },
            { RenderingMode.Vulkan, "Vulkan" },
            { RenderingMode.OpenGL, "OpenGL" }
        };

        public static IReadOnlyDictionary<TextureQuality, string?> TextureQualityLevels => new Dictionary<TextureQuality, string?>
        {
            { TextureQuality.Default, null },
            { TextureQuality.Level0, "0" },
            { TextureQuality.Level1, "1" },
            { TextureQuality.Level2, "2" },
            { TextureQuality.Level3, "3" },
        };

        // FPS unlocking: DFIntTaskSchedulerTargetFps is the engine's frame target. null leaves
        // Roblox's default 60 cap in place; a number sets the cap; a high number effectively uncaps.
        public static IReadOnlyDictionary<FramerateLimit, string?> FramerateLimits => new Dictionary<FramerateLimit, string?>
        {
            { FramerateLimit.Default, null },
            { FramerateLimit.Fps60, "60" },
            { FramerateLimit.Fps120, "120" },
            { FramerateLimit.Fps144, "144" },
            { FramerateLimit.Fps165, "165" },
            { FramerateLimit.Fps240, "240" },
            { FramerateLimit.Fps360, "360" },
            { FramerateLimit.Unlimited, "9999" }
        };

        // all fflags are stored as strings
        // to delete a flag, set the value as null
        public void SetValue(string key, object? value)
        {
            const string LOG_IDENT = "FastFlagManager::SetValue";

            if (value is null)
            {
                if (Prop.ContainsKey(key))
                    App.Logger.WriteLine(LOG_IDENT, $"Deletion of '{key}' is pending");

                Prop.Remove(key);
            }
            else
            {
                if (Prop.ContainsKey(key))
                {
                    // Compare the incoming VALUE to the stored one. This used to compare the flag
                    // NAME to the stored value, so the "unchanged, skip" short-circuit never fired
                    // — and a flag whose value happened to equal its own name could never be
                    // edited at all, because the set was silently refused.
                    if (value.ToString() == Prop[key].ToString())
                        return;

                    App.Logger.WriteLine(LOG_IDENT, $"Changing of '{key}' from '{Prop[key]}' to '{value}' is pending");
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Setting of '{key}' to '{value}' is pending");
                }

                Prop[key] = value.ToString()!;
            }
        }

        // this returns null if the fflag doesn't exist
        public string? GetValue(string key)
        {
            // check if we have an updated change for it pushed first
            if (Prop.TryGetValue(key, out object? value) && value is not null)
                return value.ToString();

            return null;
        }

        public void SetPreset(string prefix, object? value)
        {
            foreach (var pair in PresetFlags.Where(x => x.Key.StartsWith(prefix)))
                SetValue(pair.Value, value);
        }

        public void SetPresetEnum(string prefix, string target, object? value)
        {
            foreach (var pair in PresetFlags.Where(x => x.Key.StartsWith(prefix)))
            {
                if (pair.Key.StartsWith($"{prefix}.{target}"))
                    SetValue(pair.Value, value);
                else
                    SetValue(pair.Value, null);
            }
        }

        public string? GetPreset(string name)
        {
            if (!PresetFlags.ContainsKey(name))
            {
                App.Logger.WriteLine("FastFlagManager::GetPreset", $"Could not find preset {name}");
                Debug.Assert(false, $"Could not find preset {name}");
                return null;
            }

            return GetValue(PresetFlags[name]);
        }

        public T GetPresetEnum<T>(IReadOnlyDictionary<T, string> mapping, string prefix, string value) where T : Enum
        {
            foreach (var pair in mapping)
            {
                if (pair.Value == "None")
                    continue;

                if (GetPreset($"{prefix}.{pair.Value}") == value)
                    return pair.Key;
            }

            return mapping.First().Key;
        }

        public override void Save()
        {
            // convert all flag values to strings before saving

            foreach (var pair in Prop)
                Prop[pair.Key] = pair.Value.ToString()!;

            base.Save();

            // clone the dictionary
            OriginalProp = new(Prop);
        }

        public override bool Load(bool alertFailure = true)
        {
            bool result = base.Load(alertFailure);

            if (GetPreset("Rendering.ManualFullscreen") != "False")
                SetPreset("Rendering.ManualFullscreen", "False");

            // Snapshot AFTER the ManualFullscreen fixup, not before. Changed is
            // !OriginalProp.SequenceEqual(Prop), so baselining first meant any profile that didn't
            // already carry that flag reported itself as modified the instant it loaded — and the
            // user got an "unsaved changes?" prompt for something they never touched.
            OriginalProp = new(Prop);

            return result;
        }
    }
}
