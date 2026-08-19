namespace BeastStrap.Utility
{
    // Detects and removes the directory junctions used by the v420.24 layout. It no longer creates
    // them — Utility/VersionProfileLayout.cs replaced that scheme with park-and-rename, and this is
    // all that remains, purely so old installs can be migrated.
    //
    // The reason the junctions had to go, recorded here because this file's previous header made
    // exactly the wrong claim. It used to state:
    //
    //     "Most executors that parse the install-dir name see the junction path verbatim — they get
    //      the version-<hash> name they expect."
    //
    // That holds only for tools reading PEB->ProcessParameters->ImagePathName. Anything using
    // QueryFullProcessImageNameW or GetModuleFileNameExW gets the path *after* NTFS has resolved the
    // reparse point — which was Versions\profile-<id>\, matching no version-<hash> pattern at all.
    // Worse, launching the client through a reparse point is what Hyperion appears to have been
    // killing sessions over: a 2026-07-19 diagnostics bundle held a same-machine control where one
    // Fishstrap session exited cleanly at 92s while three of ours died 41-58s in, still connected,
    // logs truncating mid-gameplay.
    //
    // Nothing in this codebase should create a junction again. That's why CreateJunction and
    // RepointJunction are gone rather than merely unused.
    public static class VersionJunctionManager
    {
        private const string LOG_IDENT = "VersionJunctionManager";

        // True when the directory at `path` is a reparse point (junction or
        // symlink). Either is safe to remove via Directory.Delete(path, false).
        public static bool IsJunction(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                    return false;
                var attrs = File.GetAttributes(path);
                return (attrs & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::IsJunction", ex);
                return false;
            }
        }

        // Remove a junction without affecting its target. Directory.Delete with
        // recursive=false treats reparse points as unlink-only, so the target
        // dir's contents are safe — which is what makes the migration free: the
        // profile-<id> directory a legacy junction pointed at is already in the
        // parked shape the new layout expects, so no data has to move.
        public static bool DeleteJunction(string junctionPath)
        {
            try
            {
                if (!Directory.Exists(junctionPath))
                    return true;
                if (!IsJunction(junctionPath))
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Refusing to DeleteJunction on non-junction {junctionPath} — caller must handle real dirs explicitly.");
                    return false;
                }
                Directory.Delete(junctionPath, recursive: false);
                App.Logger.WriteLine(LOG_IDENT, $"Removed junction {junctionPath}");
                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::DeleteJunction", ex);
                return false;
            }
        }
    }
}
