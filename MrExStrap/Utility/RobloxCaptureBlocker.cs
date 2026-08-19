namespace BeastStrap.Utility
{
    // Disables Roblox's built-in screenshot and video capture without touching the Roblox process
    // or any FastFlag. Roblox saves screenshots to %MyPictures%\Roblox and recordings to
    // %MyVideos%\Roblox; if we sit a read-only placeholder FILE where each of those folders would
    // go, Roblox can't create the folder or write a capture into it, so the in-game capture just
    // fails. Toggling off removes the placeholder and restores any folder we moved aside.
    //
    // Independent implementation: the save locations are Roblox's own behaviour and a read-only
    // block is the obvious approach, so no third-party code is used here.
    public static class RobloxCaptureBlocker
    {
        private const string LOG_IDENT = "RobloxCaptureBlocker";
        private const string BackupSuffix = " (captures backup)";

        private static IEnumerable<string> CapturePaths()
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Roblox");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Roblox");
        }

        // True only when every capture path is currently blocked (read-only placeholder in place).
        public static bool IsBlocked => CapturePaths().All(IsPathBlocked);

        /// <summary>
        /// Applies the block to every capture path, all-or-nothing. Returns null on success or a
        /// user-facing reason on failure.
        /// </summary>
        /// <remarks>
        /// This used to swallow each path's exception and return void, with the ViewModel deciding
        /// what happened by re-reading <see cref="IsBlocked"/> — which requires ALL paths to be
        /// blocked. So if Pictures succeeded and Videos failed, the toggle snapped back to off, the
        /// setter could never push false, and the user had no route to undo the half that applied.
        /// </remarks>
        public static string? SetBlocked(bool blocked)
        {
            var applied = new List<string>();

            foreach (string path in CapturePaths())
            {
                try
                {
                    if (blocked)
                        Block(path);
                    else
                        Unblock(path);

                    applied.Add(path);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT, ex);

                    // Roll back whatever already took effect so the pair stays consistent.
                    if (blocked)
                    {
                        foreach (string done in applied)
                        {
                            try { Unblock(done); }
                            catch (Exception rollbackEx) { App.Logger.WriteException(LOG_IDENT + "::Rollback", rollbackEx); }
                        }
                    }

                    return $"Couldn't {(blocked ? "block" : "unblock")} {path}: {ex.Message}";
                }
            }

            return null;
        }

        private static bool IsPathBlocked(string path)
        {
            try
            {
                return File.Exists(path) && !Directory.Exists(path)
                    && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);
            }
            catch { return false; }
        }

        private static void Block(string path)
        {
            if (IsPathBlocked(path))
                return;

            // Keep any captures the user already has: move a non-empty folder aside, drop an empty
            // one. Then leave a read-only placeholder file so Roblox can't write captures here.
            string backup = path + BackupSuffix;
            bool movedToBackup = false;

            if (Directory.Exists(path))
            {
                if (Directory.EnumerateFileSystemEntries(path).Any())
                {
                    // Never delete the user's captures. This used to run
                    // Directory.Delete(path, recursive: true) whenever a backup folder already
                    // existed, on the false premise that the existing backup held these files —
                    // Directory.Exists(backup) only proves A backup exists, not that it holds
                    // what's in `path` right now. Unblock() only restores when `path` is absent,
                    // so once Roblox recreated the folder the old backup was orphaned forever and
                    // the next Block() permanently destroyed every screenshot taken since.
                    if (Directory.Exists(backup))
                        backup = NextFreeBackupPath(backup);

                    Directory.Move(path, backup);
                    movedToBackup = true;
                }
                else
                {
                    Directory.Delete(path);
                }
            }

            try
            {
                File.WriteAllBytes(path, Array.Empty<byte>());
                File.SetAttributes(path, FileAttributes.ReadOnly);
            }
            catch
            {
                // Roblox may have recreated the folder in the gap, or the write was denied.
                // Don't strand the user's captures in the backup folder — put them back.
                if (movedToBackup && Directory.Exists(backup) && !Directory.Exists(path))
                {
                    try { Directory.Move(backup, path); } catch { }
                }
                throw;
            }

            App.Logger.WriteLine(LOG_IDENT, $"Blocked Roblox captures at {path}");
        }

        // "<path> (captures backup)", "<path> (captures backup) (2)", ... — first name not taken.
        private static string NextFreeBackupPath(string basePath)
        {
            for (int i = 2; i < 1000; i++)
            {
                string candidate = $"{basePath} ({i})";
                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                    return candidate;
            }

            throw new IOException($"Couldn't find a free backup name next to {basePath}");
        }

        private static void Unblock(string path)
        {
            if (File.Exists(path) && !Directory.Exists(path))
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                File.Delete(path);
            }

            string backup = path + BackupSuffix;

            if (!Directory.Exists(backup))
            {
                App.Logger.WriteLine(LOG_IDENT, $"Unblocked Roblox captures at {path}");
                return;
            }

            if (!Directory.Exists(path))
            {
                Directory.Move(backup, path);
                App.Logger.WriteLine(LOG_IDENT, $"Unblocked Roblox captures at {path} and restored the backup");
                return;
            }

            // Roblox recreated the folder while the block was on, so a straight Move won't work.
            // Merge the backup back in rather than abandoning it — the old code silently did
            // nothing here and still logged success, leaving the user's captures parked in a
            // sibling folder that nothing would ever look at again.
            int moved = MergeInto(backup, path);
            App.Logger.WriteLine(LOG_IDENT, $"Unblocked Roblox captures at {path}, merged {moved} item(s) back from the backup");
        }

        // Moves every entry of `source` into `destination`, renaming on collision, then removes
        // `source` if it ends up empty. Returns how many entries were moved.
        private static int MergeInto(string source, string destination)
        {
            int moved = 0;

            foreach (string entry in Directory.EnumerateFileSystemEntries(source))
            {
                string name = Path.GetFileName(entry);
                string target = Path.Combine(destination, name);

                try
                {
                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        string stem = Path.GetFileNameWithoutExtension(name);
                        string ext = Path.GetExtension(name);
                        target = Path.Combine(destination, $"{stem} (restored){ext}");

                        for (int i = 2; File.Exists(target) || Directory.Exists(target); i++)
                            target = Path.Combine(destination, $"{stem} (restored {i}){ext}");
                    }

                    if (Directory.Exists(entry))
                        Directory.Move(entry, target);
                    else
                        File.Move(entry, target);

                    moved++;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::MergeInto", ex);
                }
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(source).Any())
                    Directory.Delete(source);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::MergeInto::Cleanup", ex);
            }

            return moved;
        }
    }
}
