namespace BeastStrap.Utility
{
    // Works out why a Roblox client just died and, crucially, whether WE did it. All the actual
    // deciding lives in CrashRules (which is kept free of file I/O so it can be run against a
    // user's diagnostics bundle directly) — this class only gathers the evidence and logs the
    // verdict.
    //
    // Two evidence sources, and reading both is the point:
    //
    //   1. Every BeastStrap session log that overlaps the crash window. Not just this process's
    //      own log — a launch happening in ANOTHER BeastStrap process is what deletes, rewrites
    //      or kills the client, and it is never the process that ends up showing the dialog. In
    //      the 2026-07-27 report the delete was logged by session 143038Z while the dialog fired
    //      from session 143342Z. Reading only our own History would have missed it completely.
    //   2. Roblox's own client logs, for third-party and environmental causes.
    //
    // Everything is best-effort: any failure downgrades the verdict to Unknown rather than
    // throwing, and a verdict we can't substantiate never claims BeastStrap is in the clear.
    public static class CrashAnalyzer
    {
        private const string LOG_IDENT = "CrashAnalyzer";

        // A crash-on-launch log is small (the crash fires under 60s), but an older long-session
        // log can be tens of MB. Read only the last MaxReadBytes so a big log can't hang the
        // dialog or spike memory. That still covers a small crashed log in full, and for a large
        // one the tail is where an end-of-session failure shows up.
        private const long MaxReadBytes = 1024 * 1024;

        // Our own logs are rotated at 15 files and are far smaller, but bound them anyway.
        private const long MaxAppLogReadBytes = 2 * 1024 * 1024;

        // Clock skew between processes, plus the gap between us starting a destructive operation
        // and the client noticing its files are gone. In the 2026-07-27 bundle that gap was 0.2s,
        // but the delete can begin well before the client trips over the result.
        private static readonly TimeSpan WindowSlack = TimeSpan.FromSeconds(20);

        // With no client lifetime supplied, look back far enough to cover a launch plus a short
        // session without dragging in unrelated history.
        private static readonly TimeSpan DefaultLookback = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Names the most likely cause of the crash. Pass the dead client's lifetime when it is
        /// known (the watcher has it) so the self-check only considers what BeastStrap was doing
        /// while that client was actually alive.
        /// </summary>
        public static CrashVerdict Analyze(DateTime? clientStartUtc = null, DateTime? clientEndUtc = null, string? clientLogPath = null)
        {
            try
            {
                DateTime end = (clientEndUtc ?? DateTime.UtcNow) + WindowSlack;
                DateTime start = (clientStartUtc ?? (end - DefaultLookback)) - WindowSlack;

                var ourSessions = ReadOurSessions(start, end);
                var robloxLogs = ReadRobloxLogs(start, end, clientLogPath);

                var verdict = CrashRules.Evaluate(ourSessions, robloxLogs);

                App.Logger.WriteLine(LOG_IDENT,
                    $"Verdict: {verdict.Fault} via '{verdict.RuleId}' " +
                    $"(window {start:HH:mm:ss}-{end:HH:mm:ss}Z, {ourSessions.Count} BeastStrap session(s), " +
                    $"{robloxLogs.Count} Roblox log(s){(string.IsNullOrEmpty(clientLogPath) ? "" : ", scoped to the watched client")})");

                return verdict;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);

                // We failed to look, so we have no verdict. The caller shows nothing for Unknown.
                return CrashVerdict.Inconclusive();
            }
        }

        #region BeastStrap's own logs

        // Every BeastStrap log touched during the window, narrowed to the lines inside it.
        private static IReadOnlyList<AppLogSession> ReadOurSessions(DateTime start, DateTime end, int max = 20)
        {
            var sessions = new List<AppLogSession>();

            string? liveLog = null;
            try { liveLog = App.Logger.FileLocation; } catch { /* logger not up yet */ }

            // The live session is taken from the logger's in-memory History rather than off disk.
            // Writes are flushed per line so the file is usually current, but History is the
            // authoritative copy and costs nothing.
            try
            {
                string live = App.Logger.AsDocument;
                if (!string.IsNullOrEmpty(live))
                {
                    var narrowed = CrashRules.NarrowToWindow(live, start, end);
                    if (!string.IsNullOrEmpty(narrowed))
                        sessions.Add(new AppLogSession("current-session", CrashRules.IsBackgroundUpdaterLog(live), narrowed));
                }
            }
            catch (Exception ex) { App.Logger.WriteException(LOG_IDENT + "::ReadLiveSession", ex); }

            FileInfo[] files;
            try
            {
                if (string.IsNullOrEmpty(Paths.Logs) || !Directory.Exists(Paths.Logs))
                    return sessions;

                files = new DirectoryInfo(Paths.Logs).GetFiles("*.log")
                    // A file last written before the window opened can't hold anything inside it.
                    .Where(f => f.LastWriteTimeUtc >= start)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(max)
                    .ToArray();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::EnumAppLogs", ex);
                return sessions;
            }

            foreach (var file in files)
            {
                // Already covered by History above.
                if (!string.IsNullOrEmpty(liveLog) && PathsEqual(file.FullName, liveLog))
                    continue;

                try
                {
                    string content = ReadTail(file.FullName, MaxAppLogReadBytes);
                    string narrowed = CrashRules.NarrowToWindow(content, start, end);

                    if (string.IsNullOrEmpty(narrowed))
                        continue;

                    sessions.Add(new AppLogSession(file.Name, CrashRules.IsBackgroundUpdaterLog(content), narrowed));
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::ReadAppLog::" + file.Name, ex);
                }
            }

            return sessions;
        }

        private static bool PathsEqual(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        #endregion

        #region Roblox's client logs

        // Roblox writes one log per launch to %LocalAppData%\Roblox\logs.
        //
        // Two things matter here and neither used to happen:
        //
        //   1. When we know WHICH client died (the watcher does — it has been tailing that exact
        //      file), read only that one. Scanning "the 3 most recently modified logs" meant a
        //      healthy client running alongside the dead one could hand us the verdict for a crash
        //      it had nothing to do with.
        //   2. Narrow to the crash window, exactly as we do for our own logs. Handing the rules a
        //      whole 1 MB tail let a line from the opening minutes of a long session explain a
        //      death hours later.
        private static IReadOnlyList<string> ReadRobloxLogs(DateTime start, DateTime end, string? clientLogPath, int max = 3)
        {
            var logs = new List<string>();

            // The client we were actually watching. Nothing else can be more relevant.
            if (!string.IsNullOrEmpty(clientLogPath) && File.Exists(clientLogPath))
            {
                try
                {
                    string narrowed = CrashRules.NarrowToWindow(ReadTail(clientLogPath, MaxReadBytes), start, end);
                    if (!string.IsNullOrEmpty(narrowed))
                        logs.Add(narrowed);

                    return logs;
                }
                catch (Exception ex)
                {
                    // Fall through to the scan below rather than losing the evidence entirely.
                    App.Logger.WriteException(LOG_IDENT + "::ReadClientLog", ex);
                }
            }

            string logDir;
            try { logDir = Path.Combine(Paths.LocalAppData, "Roblox", "logs"); }
            catch { return logs; }

            if (!Directory.Exists(logDir))
                return logs;

            FileInfo[] files;
            try
            {
                files = new DirectoryInfo(logDir).GetFiles("*.log")
                    .Where(f => f.LastWriteTimeUtc >= start)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(max)
                    .ToArray();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::EnumRobloxLogs", ex);
                return logs;
            }

            foreach (var file in files)
            {
                try
                {
                    string narrowed = CrashRules.NarrowToWindow(ReadTail(file.FullName, MaxReadBytes), start, end);
                    if (!string.IsNullOrEmpty(narrowed))
                        logs.Add(narrowed);
                }
                catch { continue; }
            }

            return logs;
        }

        #endregion

        private static string ReadTail(string path, long maxBytes)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length > maxBytes)
                fs.Seek(-maxBytes, SeekOrigin.End);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
    }
}
