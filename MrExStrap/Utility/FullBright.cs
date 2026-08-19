namespace BeastStrap.Utility
{
    // "FullBright" without FastFlags: Roblox's physically-based lighting leans on a BRDF lookup
    // texture at PlatformContent\pc\textures\...\brdfLUT.*, and with that texture missing the client
    // falls back to flat, evenly-lit surfaces. Cosmetic and local-only, same class of change as
    // anything else in the Modifications overlay — it just can't go through the overlay itself,
    // because the overlay copies files in and this effect needs one taken out.
    //
    // Runs on every launch against the version directory we're actually starting, so a Roblox update
    // (which restores the texture in a fresh version folder) is handled without the user doing
    // anything. The original is kept so turning the toggle off puts it straight back rather than
    // forcing a redownload.
    public static class FullBright
    {
        private const string LOG_IDENT = "FullBright";

        private static string BackupDirectory => Path.Combine(Paths.Base, "FullBrightBackup");

        /// <summary>
        /// Applies or reverts the effect inside <paramref name="versionDirectory"/> — the folder the
        /// client is about to launch from, junction or otherwise. Best-effort: a failure here must
        /// never stop a launch, so everything is caught and logged.
        /// </summary>
        public static void Apply(string versionDirectory, bool enabled)
        {
            try
            {
                string texturesRoot = Path.Combine(versionDirectory, "PlatformContent", "pc", "textures");

                if (!Directory.Exists(texturesRoot))
                    return;

                if (enabled)
                    Enable(texturesRoot);
                else
                    Restore(texturesRoot);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Apply", ex);
            }
        }

        private static void Enable(string texturesRoot)
        {
            // Roblox has moved this file between subfolders across versions, so search rather than
            // hardcoding a path.
            var textures = Directory.EnumerateFiles(texturesRoot, "brdfLUT.*", SearchOption.AllDirectories).ToList();

            if (textures.Count == 0)
                return; // already removed, or this build doesn't ship one

            Directory.CreateDirectory(BackupDirectory);

            foreach (string texture in textures)
            {
                string backup = Path.Combine(BackupDirectory, Path.GetFileName(texture));

                if (!File.Exists(backup))
                {
                    File.Copy(texture, backup);
                    App.Logger.WriteLine(LOG_IDENT, $"Kept a copy of {Path.GetFileName(texture)}");
                }

                File.Delete(texture);
                App.Logger.WriteLine(LOG_IDENT, $"Removed {Path.GetFileName(texture)} — FullBright on");
            }
        }

        private static void Restore(string texturesRoot)
        {
            if (!Directory.Exists(BackupDirectory))
                return;

            var backups = Directory.EnumerateFiles(BackupDirectory, "brdfLUT.*").ToList();

            if (backups.Count == 0)
                return;

            // Restore into the version folder we're launching rather than wherever the file lived
            // when it was backed up — after a Roblox update that path is stale, and writing to it
            // would put the texture somewhere the client never reads.
            string target = ResolveTextureFolder(texturesRoot);

            foreach (string backup in backups)
            {
                string destination = Path.Combine(target, Path.GetFileName(backup));

                if (File.Exists(destination))
                    continue; // nothing to undo in this version folder

                Directory.CreateDirectory(target);
                File.Copy(backup, destination);
                App.Logger.WriteLine(LOG_IDENT, $"Restored {Path.GetFileName(backup)} — FullBright off");
            }
        }

        /// <summary>
        /// Where brdfLUT belongs in this build. Roblox keeps it beside the other PBR textures, so
        /// follow one of those if we can find it; otherwise fall back to the textures root.
        /// </summary>
        private static string ResolveTextureFolder(string texturesRoot)
        {
            try
            {
                string? sibling = Directory
                    .EnumerateFiles(texturesRoot, "*.dds", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName)
                    .FirstOrDefault(d => !string.IsNullOrEmpty(d));

                return sibling ?? texturesRoot;
            }
            catch
            {
                return texturesRoot;
            }
        }
    }
}
