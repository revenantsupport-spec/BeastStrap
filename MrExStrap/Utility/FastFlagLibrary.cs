using BeastStrap.Models;

namespace BeastStrap.Utility
{
    // Community fast-flag library + per-profile backup/restore.
    //
    // Bundled presets are curated, allowlist-friendly flag sets grouped by category, surfaced
    // in the FastFlags page via FastFlagLibraryDialog. Applying one merges its flags into the
    // currently-edited profile (App.FastFlags, which the page repoints via EditingProfileId).
    //
    // Backups snapshot the edited profile's entire flag set as a flat JSON dictionary under
    // Paths.FastFlagBackups. They're independent of the live per-profile files, so restoring
    // a backup is a deliberate action rather than something that happens automatically.
    public static class FastFlagLibrary
    {
        private const string LOG_IDENT = "FastFlagLibrary";

        public static string BackupsDirectory => Paths.FastFlagBackups;

        public static IReadOnlyList<FastFlagPreset> BundledPresets { get; } = BuildPresets();

        // ---- backup management ----

        public static List<FastFlagBackupEntry> ListBackups()
        {
            var entries = new List<FastFlagBackupEntry>();

            try
            {
                if (!Directory.Exists(BackupsDirectory))
                    return entries;

                foreach (string file in Directory.EnumerateFiles(BackupsDirectory, "*.json"))
                {
                    try
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        int count = CountFlags(file);
                        entries.Add(new FastFlagBackupEntry(name, File.GetLastWriteTime(file), count));
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteException(LOG_IDENT + "::ListBackups", ex);
                    }
                }

                entries.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::ListBackups", ex);
            }

            return entries;
        }

        // Copies the currently-edited profile's flag set into a named backup. Returns the
        // backup name actually written (sanitized for the filesystem), or null on failure.
        public static string? SaveBackup(string name)
        {
            string safeName = SanitizeName(name);
            if (string.IsNullOrEmpty(safeName))
                return null;

            try
            {
                Directory.CreateDirectory(BackupsDirectory);

                string path = Path.Combine(BackupsDirectory, safeName + ".json");
                string json = JsonSerializer.Serialize(
                    new Dictionary<string, object>(App.FastFlags.Prop),
                    new JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(path, json);
                App.Logger.WriteLine(LOG_IDENT, $"Saved backup '{safeName}' ({App.FastFlags.Prop.Count} flags)");

                return safeName;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::SaveBackup", ex);
                return null;
            }
        }

        // Loads a backup's flags as a fresh dictionary; null when missing or unreadable.
        public static Dictionary<string, object>? LoadBackup(string name)
        {
            string safeName = SanitizeName(name);
            if (string.IsNullOrEmpty(safeName))
                return null;

            string path = Path.Combine(BackupsDirectory, safeName + ".json");
            if (!File.Exists(path))
                return null;

            try
            {
                var flags = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                return flags ?? new Dictionary<string, object>();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::LoadBackup", ex);
                return null;
            }
        }

        public static void DeleteBackup(string name)
        {
            string safeName = SanitizeName(name);
            if (string.IsNullOrEmpty(safeName))
                return;

            string path = Path.Combine(BackupsDirectory, safeName + ".json");

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    App.Logger.WriteLine(LOG_IDENT, $"Deleted backup '{safeName}'");
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::DeleteBackup", ex);
            }
        }

        private static int CountFlags(string path)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    ? doc.RootElement.EnumerateObject().Count()
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static string SanitizeName(string name)
        {
            name = name.Trim();
            if (string.IsNullOrEmpty(name))
                return "";

            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            return name.Trim().Length > 0 ? name : "";
        }

        // ---- bundled community presets ----

        private static List<FastFlagPreset> BuildPresets()
        {
            return new List<FastFlagPreset>
            {
                // ---------- Performance ----------
                new()
                {
                    Name = "Uncap FPS",
                    Category = "Performance",
                    Description = "Raises the engine's frame target so the client can render past Roblox's 60 FPS default, and lifts the newer 240 FPS hard cap.",
                    Flags =
                    {
                        ["DFIntTaskSchedulerTargetFps"] = "2147483647",
                        ["FFlagTaskSchedulerLimitTargetFpsTo2402"] = "False"
                    }
                },
                new()
                {
                    Name = "Low-end / performance",
                    Category = "Performance",
                    Description = "A bundle for weaker machines: uncapped FPS, low textures, no MSAA, Vulkan preferred, no grass.",
                    Flags =
                    {
                        ["DFIntTaskSchedulerTargetFps"] = "2147483647",
                        ["FFlagTaskSchedulerLimitTargetFpsTo2402"] = "False",
                        ["DFFlagTextureQualityOverrideEnabled"] = "True",
                        ["DFIntTextureQualityOverride"] = "0",
                        ["FIntDebugForceMSAASamples"] = "1",
                        ["FFlagDebugGraphicsPreferVulkan"] = "True",
                        ["FIntFRMMaxGrassDistance"] = "0",
                        ["FIntFRMMinGrassDistance"] = "0"
                    }
                },
                new()
                {
                    Name = "Remove grass",
                    Category = "Performance",
                    Description = "Sets the grass render distance to zero so decorative grass stops drawing.",
                    Flags =
                    {
                        ["FIntFRMMaxGrassDistance"] = "0",
                        ["FIntFRMMinGrassDistance"] = "0"
                    }
                },
                new()
                {
                    Name = "Pause voxelizer",
                    Category = "Performance",
                    Description = "Pauses the voxelizer (used for terrain shadow generation), a mild CPU saving.",
                    Flags = { ["DFFlagDebugPauseVoxelizer"] = "True" }
                },

                // ---------- Rendering ----------
                new()
                {
                    Name = "Force D3D11",
                    Category = "Rendering",
                    Description = "Force the D3D11 graphics backend. Often the most compatible option.",
                    Flags = { ["FFlagDebugGraphicsPreferD3D11"] = "True" }
                },
                new()
                {
                    Name = "Force Vulkan",
                    Category = "Rendering",
                    Description = "Force the Vulkan backend. Can run better on some GPUs.",
                    Flags = { ["FFlagDebugGraphicsPreferVulkan"] = "True" }
                },
                new()
                {
                    Name = "Force OpenGL",
                    Category = "Rendering",
                    Description = "Force the OpenGL backend. Useful for compatibility testing.",
                    Flags = { ["FFlagDebugGraphicsPreferOpenGL"] = "True" }
                },
                new()
                {
                    Name = "High MSAA (x8)",
                    Category = "Rendering",
                    Description = "Force 8x MSAA for smoother edges. Costs GPU performance.",
                    Flags = { ["FIntDebugForceMSAASamples"] = "8" }
                },
                new()
                {
                    Name = "Low textures",
                    Category = "Rendering",
                    Description = "Override texture quality to the lowest level. Big VRAM / bandwidth saving.",
                    Flags =
                    {
                        ["DFFlagTextureQualityOverrideEnabled"] = "True",
                        ["DFIntTextureQualityOverride"] = "0"
                    }
                },
                new()
                {
                    Name = "High textures",
                    Category = "Rendering",
                    Description = "Override texture quality to the highest level. Sharper textures at a VRAM cost.",
                    Flags =
                    {
                        ["DFFlagTextureQualityOverrideEnabled"] = "True",
                        ["DFIntTextureQualityOverride"] = "3"
                    }
                },
                new()
                {
                    Name = "Gray sky",
                    Category = "Rendering",
                    Description = "Replaces the sky with a flat gray — a small visibility and performance tweak.",
                    Flags = { ["FFlagDebugSkyGray"] = "True" }
                },

                // ---------- UI / misc ----------
                new()
                {
                    Name = "Fix display scaling",
                    Category = "UI / Misc",
                    Description = "Disables DPI scaling so the renderer stays at its native resolution.",
                    Flags = { ["DFFlagDisableDPIScale"] = "True" }
                },
                new()
                {
                    Name = "Manual fullscreen (Alt+Enter)",
                    Category = "UI / Misc",
                    Description = "Handle Alt+Enter fullscreen switching in-process instead of relying on the OS.",
                    Flags = { ["FFlagHandleAltEnterFullscreenManually"] = "True" }
                },
                new()
                {
                    Name = "Reduced motion (grass)",
                    Category = "UI / Misc",
                    Description = "Reduces the swaying motion of decorative grass.",
                    Flags = { ["FIntGrassMovementReducedMotionFactor"] = "1" }
                }
            };
        }
    }

    // Lightweight display model for one saved backup (name + timestamp + flag count).
    public class FastFlagBackupEntry
    {
        public string Name { get; }
        public DateTime CreatedAt { get; }
        public int FlagCount { get; }

        public FastFlagBackupEntry(string name, DateTime createdAt, int flagCount)
        {
            Name = name;
            CreatedAt = createdAt;
            FlagCount = flagCount;
        }

        public string Display =>
            $"{Name}  ·  {CreatedAt:yyyy-MM-dd HH:mm}  ·  {FlagCount} flags";
    }
}