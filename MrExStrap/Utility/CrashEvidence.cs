using System.IO;

namespace BeastStrap.Utility
{
    /// <summary>
    /// Copies the logs belonging to a crash somewhere rotation cannot reach them.
    /// </summary>
    /// <remarks>
    /// Without this the rest of the crash-notice feature is pointless. Logger keeps 15 FILES, and
    /// every launch writes at least two of them (bootstrapper + watcher), so ordinary retention is
    /// roughly five to seven launches. A user who sees the notice three days later and exports would
    /// otherwise get a bundle with none of the relevant logs left in it.
    ///
    /// Everything here is local. Nothing is transmitted — the copies only exist so that a
    /// user-initiated export days later still contains something worth reading.
    /// </remarks>
    public static class CrashEvidence
    {
        private const string LOG_IDENT = "CrashEvidence";

        // Keep the last few crashes' worth. Each folder is a handful of text files, but a user who
        // crashes repeatedly should not accumulate them forever.
        private const int MaxPreservedCrashes = 5;

        public static string PreservedRoot => Path.Combine(Paths.Logs, "Preserved");

        /// <summary>
        /// Copies this session's BeastStrap logs and the dead client's Roblox log into a new
        /// folder under <see cref="PreservedRoot"/>.
        /// </summary>
        /// <returns>The folder name (not full path), or "" if nothing could be preserved.</returns>
        public static string Preserve(string? clientLogPath)
        {
            try
            {
                if (!Paths.Initialized || string.IsNullOrEmpty(Paths.Logs) || !Directory.Exists(Paths.Logs))
                    return "";

                string folderName = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
                string target = Path.Combine(PreservedRoot, folderName);
                Directory.CreateDirectory(target);

                // Our own logs. Copy the whole current rotation window rather than just this
                // process's file: the process that causes a crash is very often NOT the process that
                // notices it (the bootstrapper deletes, the watcher sees the client die), which is
                // the same reason CrashAnalyzer reads every sibling log.
                int copied = 0;
                foreach (var file in new DirectoryInfo(Paths.Logs).GetFiles("*.log"))
                {
                    if (TryCopy(file.FullName, Path.Combine(target, file.Name)))
                        copied++;
                }

                // The client that actually died.
                if (!string.IsNullOrEmpty(clientLogPath) && File.Exists(clientLogPath))
                {
                    if (TryCopy(clientLogPath, Path.Combine(target, "roblox-" + Path.GetFileName(clientLogPath))))
                        copied++;
                }

                if (copied == 0)
                {
                    try { Directory.Delete(target, true); } catch { }
                    return "";
                }

                App.Logger.WriteLine(LOG_IDENT, $"Preserved {copied} log file(s) for this crash under Logs\\Preserved\\{folderName}");

                Prune();

                return folderName;
            }
            catch (Exception ex)
            {
                // Never let bookkeeping break the crash path.
                App.Logger.WriteException(LOG_IDENT, ex);
                return "";
            }
        }

        /// <summary>Drops the oldest preserved folders beyond <see cref="MaxPreservedCrashes"/>.</summary>
        private static void Prune()
        {
            try
            {
                if (!Directory.Exists(PreservedRoot))
                    return;

                var folders = new DirectoryInfo(PreservedRoot).GetDirectories()
                    .OrderByDescending(d => d.Name, StringComparer.Ordinal)
                    .Skip(MaxPreservedCrashes)
                    .ToArray();

                foreach (var folder in folders)
                {
                    try
                    {
                        folder.Delete(true);
                        App.Logger.WriteLine(LOG_IDENT, $"Pruned old preserved crash folder '{folder.Name}'");
                    }
                    catch (Exception ex) { App.Logger.WriteException(LOG_IDENT + "::Prune", ex); }
                }
            }
            catch (Exception ex) { App.Logger.WriteException(LOG_IDENT + "::Prune", ex); }
        }

        // Share everything: the live log is held open by this very process, and the Roblox log may
        // still be held by a dying client.
        private static bool TryCopy(string source, string destination)
        {
            try
            {
                using var src = new FileStream(source, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                src.CopyTo(dst);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
