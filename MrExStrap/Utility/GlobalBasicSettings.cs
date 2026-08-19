using System.Xml.Linq;
using System.Xml.XPath;

namespace BeastStrap.Utility
{
    // Reads and writes Roblox's in-game settings file, %LocalAppData%\Roblox\GlobalBasicSettings_13.xml.
    // These are the options behind Roblox's own Esc -> Settings menu: graphics quality, framerate cap,
    // volume, UI transparency and so on. Roblox rewrites the whole file when the client exits, which is
    // why edits normally don't stick — see Lock() for how that's handled.
    //
    // Two deliberate differences from how other forks do this:
    //   1. SetValue creates a missing element instead of silently doing nothing. Roblox only writes a
    //      key once the user has touched that setting at least once, so on a fresh profile half of
    //      these simply aren't in the file yet, and a set-only-if-present editor appears to work while
    //      changing nothing.
    //   2. The first write takes a backup, so "reset to Roblox defaults" is a real restore rather than
    //      deleting the file and hoping.
    public static class GlobalBasicSettings
    {
        private const string LOG_IDENT = "GlobalBasicSettings";

        // The settings container inside the XML. Everything we touch is a child of this.
        private const string PropertiesXPath = "//Item[@class='UserGameSettings']/Properties";

        // Roblox types each setting by element name, so writing a new key means knowing which to
        // create. Names verified against a live GlobalBasicSettings_13.xml rather than guessed.
        public enum GbsType { Bool, Int, Float, Token, String }

        public sealed record Setting(string Key, GbsType Type);

        public static readonly Setting UiTransparency   = new("PreferredTransparency", GbsType.Float);
        public static readonly Setting TextSize         = new("PreferredTextSize", GbsType.Token);
        public static readonly Setting ReducedMotion    = new("ReducedMotion", GbsType.Bool);
        public static readonly Setting ChatVisible      = new("ChatVisible", GbsType.Bool);
        public static readonly Setting PlayerNames      = new("PlayerNamesEnabled", GbsType.Bool);
        public static readonly Setting PlayerList       = new("PlayerListVisible", GbsType.Bool);
        public static readonly Setting BadgeVisible     = new("BadgeVisible", GbsType.Bool);
        public static readonly Setting PerformanceStats = new("PerformanceStatsVisible", GbsType.Bool);

        public static readonly Setting FramerateCap     = new("FramerateCap", GbsType.Int);
        public static readonly Setting QualityLevel     = new("SavedQualityLevel", GbsType.Token);
        public static readonly Setting GraphicsQuality  = new("GraphicsQualityLevel", GbsType.Int);
        public static readonly Setting Fullscreen       = new("Fullscreen", GbsType.Bool);
        public static readonly Setting StartMaximized   = new("StartMaximized", GbsType.Bool);

        public static readonly Setting MasterVolume     = new("MasterVolume", GbsType.Float);
        public static readonly Setting MouseSensitivity = new("MouseSensitivity", GbsType.Float);

        public static string FileLocation => Path.Combine(Paths.LocalAppData, "Roblox", "GlobalBasicSettings_13.xml");

        private static string BackupLocation => FileLocation + ".beaststrap-backup";

        private static XDocument? _document;

        public static bool Loaded => _document is not null;

        public static bool Exists => File.Exists(FileLocation);

        /// <summary>
        /// True while a Roblox client is running. Edits made now are pointless — the client holds its
        /// settings in memory and writes the whole file back out on exit, wiping anything we changed.
        /// Callers should surface this rather than writing and letting the user wonder why nothing took.
        /// </summary>
        public static bool RobloxRunning =>
            Utilities.GetProcessesSafe().Any(p => p.ProcessName == "RobloxPlayerBeta");

        public static bool Load()
        {
            if (!File.Exists(FileLocation))
            {
                App.Logger.WriteLine(LOG_IDENT, $"No settings file at {FileLocation} — Roblox hasn't written one yet.");
                _document = null;
                return false;
            }

            try
            {
                _document = XDocument.Load(FileLocation);
                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Load", ex);
                _document = null;
                return false;
            }
        }

        public static bool Save()
        {
            if (_document is null)
                return false;

            try
            {
                // Keep one pristine copy from before we ever touched the file, so Reset() can put the
                // user back exactly where Roblox had them.
                if (!File.Exists(BackupLocation) && File.Exists(FileLocation))
                {
                    File.Copy(FileLocation, BackupLocation);
                    App.Logger.WriteLine(LOG_IDENT, $"Backed up original settings to {BackupLocation}");
                }

                // The lock is just the read-only attribute, so it has to come off to write and go
                // straight back on afterwards — otherwise saving silently disarms the user's lock.
                //
                // The re-lock is in a finally because the save between them genuinely does throw:
                // the UI offers to save while Roblox is running, and Roblox holds this file open.
                // Without it, a failed save left the file unlocked and Roblox overwrote every
                // value the user believed was pinned the next time it exited.
                bool wasLocked = IsLocked;

                if (wasLocked)
                    SetLocked(false);

                try
                {
                    _document.Save(FileLocation);
                }
                finally
                {
                    if (wasLocked)
                        SetLocked(true);
                }

                App.Logger.WriteLine(LOG_IDENT, $"Saved settings to {FileLocation} (locked={wasLocked})");
                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Save", ex);
                return false;
            }
        }

        public static string? GetValue(Setting setting)
        {
            return _document?.XPathSelectElement($"{PropertiesXPath}/*[@name='{setting.Key}']")?.Value;
        }

        public static void SetValue(Setting setting, object? value)
        {
            if (_document is null)
                return;

            string text = value switch
            {
                bool b => b ? "true" : "false",
                float f => f.ToString(System.Globalization.CultureInfo.InvariantCulture),
                double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
                null => string.Empty,
                _ => value.ToString() ?? string.Empty
            };

            var element = _document.XPathSelectElement($"{PropertiesXPath}/*[@name='{setting.Key}']");

            if (element is not null)
            {
                element.Value = text;
                return;
            }

            // Not present yet — Roblox only writes a key once the user has changed that setting at
            // least once. Create it with the right element name so the client parses it.
            var properties = _document.XPathSelectElement(PropertiesXPath);

            if (properties is null)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Can't set {setting.Key}: no UserGameSettings block in the file.");
                return;
            }

            string elementName = setting.Type switch
            {
                GbsType.Bool => "bool",
                GbsType.Int => "int",
                GbsType.Float => "float",
                GbsType.Token => "token",
                _ => "string"
            };

            properties.Add(new XElement(elementName, new XAttribute("name", setting.Key), text));
            App.Logger.WriteLine(LOG_IDENT, $"Created missing setting {setting.Key} ({elementName}) = {text}");
        }

        public static bool GetBool(Setting setting, bool fallback = false) =>
            bool.TryParse(GetValue(setting), out bool v) ? v : fallback;

        public static int GetInt(Setting setting, int fallback = 0) =>
            int.TryParse(GetValue(setting), out int v) ? v : fallback;

        public static float GetFloat(Setting setting, float fallback = 0f) =>
            float.TryParse(GetValue(setting), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;

        /// <summary>
        /// Roblox rewrites this file wholesale when the client exits, so the only way to make an edit
        /// survive is to take away its write access. Locking sets the read-only attribute; the client
        /// then fails its write and leaves our values alone.
        /// </summary>
        public static bool IsLocked
        {
            get
            {
                try
                {
                    return File.Exists(FileLocation) && File.GetAttributes(FileLocation).HasFlag(FileAttributes.ReadOnly);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::IsLocked", ex);
                    return false;
                }
            }
        }

        public static void SetLocked(bool locked)
        {
            if (!File.Exists(FileLocation))
                return;

            try
            {
                var attributes = File.GetAttributes(FileLocation);

                if (locked)
                    attributes |= FileAttributes.ReadOnly;
                else
                    attributes &= ~FileAttributes.ReadOnly;

                File.SetAttributes(FileLocation, attributes);
                App.Logger.WriteLine(LOG_IDENT, $"{(locked ? "Locked" : "Unlocked")} {FileLocation}");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::SetLocked", ex);
            }
        }

        /// <summary>
        /// Restores the copy taken before our first write. Returns false when there's no backup,
        /// which means we've never modified the file and there's nothing to undo.
        /// </summary>
        public static bool Reset()
        {
            if (!File.Exists(BackupLocation))
                return false;

            try
            {
                SetLocked(false);
                File.Copy(BackupLocation, FileLocation, overwrite: true);

                // Win32 CopyFile propagates attributes, so a backup taken while the lock was on is
                // itself read-only — and the copy above just stamped that back onto FileLocation.
                // Clear both before deleting, or File.Delete throws UnauthorizedAccessException,
                // the catch swallows it, Load() never runs and a restore that actually succeeded
                // is reported to the user as a failure.
                Filesystem.AssertReadOnly(FileLocation);
                Filesystem.AssertReadOnly(BackupLocation);

                File.Delete(BackupLocation);
                App.Logger.WriteLine(LOG_IDENT, "Restored the original Roblox settings and cleared the backup.");
                return Load();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Reset", ex);
                return false;
            }
        }

        public static bool HasBackup => File.Exists(BackupLocation);
    }
}
