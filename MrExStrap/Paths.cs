namespace BeastStrap
{
    static class Paths
    {
        // note that these are directories that aren't tethered to the basedirectory
        // so these can safely be called before initialization
        public static string Temp => Path.Combine(Path.GetTempPath(), App.ProjectName);
        public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        public static string WindowsStartMenu => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        public static string System => Environment.GetFolderPath(Environment.SpecialFolder.System);

        public static string Process => Environment.ProcessPath!;

        public static string TempUpdates => Path.Combine(Temp, "Updates");
        public static string TempLogs => Path.Combine(Temp, "Logs");

        public static string Base { get; private set; } = "";
        public static string Downloads { get; private set; } = "";
        public static string Logs { get; private set; } = "";
        public static string Integrations { get; private set; } = "";
        public static string Versions { get; private set; } = "";

        // Where inactive Versions Manager profiles' installs wait.
        //
        // Deliberately a SIBLING of Versions rather than a child. Executors and FastFlag injectors
        // work out which Roblox build you are on by scanning the Versions folder, and a parked
        // install is a complete Roblox tree with its own client in it — so while these lived at
        // Versions\profile-<id>\ those tools kept attaching to whichever profile the user had NOT
        // selected. "profile-" even sorts before "version-", so it was usually the first one found.
        // Versions now holds exactly one directory, named version-<hash>, which is the layout every
        // one of those tools already expects.
        public static string ParkedVersions { get; private set; } = "";

        public static string Modifications { get; private set; } = "";
        public static string CustomThemes { get; private set; } = "";
        public static string DebugOutput { get; private set; } = "";

        // Per-Versions-Manager-profile fast flag sets. Each profile's flags live in
        // FastFlagProfiles\<profileId>.json, kept OUTSIDE Modifications\ so the launch
        // overlay copy never ships them into the Roblox install.
        public static string FastFlagProfiles { get; private set; } = "";
        public static string FastFlagBackups { get; private set; } = "";

        public static string Application { get; private set; } = "";

        public static string CustomFont => Path.Combine(Modifications, "content\\fonts\\CustomFont.ttf");

        public static bool Initialized => !String.IsNullOrEmpty(Base);

        // When non-null, Versions + Downloads are stored under this directory instead of Base.
        // Used for fast-portable mode: heavy Roblox binaries cache locally on the host machine
        // while config (Settings/State/Logs/Modifications/CustomThemes) still travels with the
        // portable folder.
        public static string? CacheBase { get; private set; }

        public static void Initialize(string baseDirectory, string? cacheDirectory = null)
        {
            Base = baseDirectory;
            CacheBase = cacheDirectory;

            string heavyRoot = cacheDirectory ?? baseDirectory;

            Downloads = Path.Combine(heavyRoot, "Downloads");
            Versions = Path.Combine(heavyRoot, "Versions");

            // MUST be heavyRoot, never Base. Parking and unparking a profile is a Directory.Move,
            // which throws across volumes — and in fast-portable mode Base is the portable folder
            // (possibly a USB stick) while heavyRoot is the local cache on C:. Hanging this off
            // Base would make every profile switch fail for exactly the users that mode exists for,
            // and the failure surfaces as "it's in use, close Roblox and relaunch" forever.
            ParkedVersions = Path.Combine(heavyRoot, "ParkedVersions");

            Logs = Path.Combine(Base, "Logs");
            Integrations = Path.Combine(Base, "Integrations");
            Modifications = Path.Combine(Base, "Modifications");
            CustomThemes = Path.Combine(Base, "CustomThemes");

            // Debug-mode artifacts: diagnostic snapshots, captured stack dumps, anything the
            // user generates from the "Save diagnostic snapshot" button in Settings → Debug mode.
            // Distinct from Logs/ so users can hand the maintainer a single folder without
            // accidentally including every routine log file.
            DebugOutput = Path.Combine(Base, "Debug");

            FastFlagProfiles = Path.Combine(Base, "FastFlagProfiles");

            // Community-library backups of per-profile flag sets. Distinct from FastFlagProfiles
            // (the live files) so restoring a backup can never clobber the source it came from.
            FastFlagBackups = Path.Combine(Base, "FastFlagBackups");

            Application = Path.Combine(Base, $"{App.ProjectName}.exe");
        }
    }
}
