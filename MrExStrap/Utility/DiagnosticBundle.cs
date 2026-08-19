using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ICSharpCode.SharpZipLib.Zip;

namespace BeastStrap.Utility
{
    // One-click "everything the maintainer needs to debug a problem on the user's machine"
    // bundle. Triggered from the Debug-mode panel in Settings; output lands in Paths.DebugOutput
    // so the user can find it without hunting through three different folders.
    //
    // Contents (each as a separate entry inside the zip):
    //   environment.txt        — OS, runtime, locale, elevation, build commit
    //   settings.json          — full Settings dump
    //   state.json             — full State dump
    //   fastflags.json         — the ACTIVE profile's FastFlags (kept flat for older readers)
    //   fastflags/<id>.json    — EVERY profile's FastFlags, plus _index.txt mapping id -> name
    //   adapters.txt           — physical network adapters as BeastStrap sees them
    //   processes.txt          — running Roblox PIDs + uptime + memory
    //   health.txt             — HealthCheck.RunAllAsync output
    //   update_probe.txt       — fresh HTTP probe of the update server's /releases/latest with status / headers
    //   logs/<filename>.log    — every file in Paths.Logs (the live session is taken from the
    //                            logger's in-memory History, since its on-disk copy is still buffered)
    //   roblox-logs/<file>.log — Roblox client logs covering the same time window as logs/ above,
    //                            which is where a Roblox-side crash (as opposed to a BeastStrap
    //                            fault) is actually diagnosable
    public static class DiagnosticBundle
    {
        private const string LOG_IDENT = "DiagnosticBundle";

        // quick=true is the crash-time path: it skips the two live network probes (health
        // check + GitHub probe) so the export is instant and works offline, and falls back
        // to a temp folder if Paths isn't initialized yet (a crash during early startup).
        public static async Task<string> CreateAsync(bool quick = false)
        {
            string outputDir = string.IsNullOrEmpty(Paths.DebugOutput)
                ? Path.Combine(Path.GetTempPath(), $"{App.ProjectName}-Debug")
                : Paths.DebugOutput;
            Directory.CreateDirectory(outputDir);

            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
            string kind = quick ? "crashlogs" : "debug";
            string zipPath = Path.Combine(outputDir, $"BeastStrap-{kind}-{timestamp}.zip");

            App.Logger.WriteLine(LOG_IDENT, $"Building diagnostic snapshot at {zipPath} (quick={quick})");

            using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
            using var zip = new ZipOutputStream(fileStream);
            zip.SetLevel(6);

            // Everything below is in a try/finally so the zip is always closed out properly. The
            // individual builders already swallow their own failures, but a bundle that dies part
            // way through used to leave a truncated, unopenable zip on disk — which is exactly the
            // wrong failure mode for the one artefact a user only produces when something is
            // already badly wrong. A short bundle beats an unreadable one.
            try
            {
                WriteEntry(zip, "environment.txt", await BuildEnvironmentAsync());
                WriteEntry(zip, "settings.json", RedactSecrets(SafeReadFile(App.Settings.FileLocation)));
                WriteEntry(zip, "state.json", SafeReadFile(App.State.FileLocation));
                AddFastFlagProfiles(zip);
                WriteEntry(zip, "adapters.txt", BuildAdapterReport());
                WriteEntry(zip, "processes.txt", BuildProcessReport());

                if (quick)
                {
                    // Skip the network-bound probes — at crash time we want the logs out
                    // instantly, and the user may have no connection. The logs + settings +
                    // environment below are what actually matter for debugging a crash.
                    WriteEntry(zip, "health.txt", "(skipped for quick crash export)");
                    WriteEntry(zip, "update_probe.txt", "(skipped for quick crash export)");
                }
                else
                {
                    WriteEntry(zip, "health.txt", await BuildHealthReportAsync());
                    WriteEntry(zip, "update_probe.txt", await BuildUpdateProbeAsync());
                }

                AddLogFolder(zip);
                AddRobloxLogs(zip);
            }
            finally
            {
                try
                {
                    zip.CloseEntry();
                    zip.Finish();
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::Finish", ex);
                }
            }

            App.Logger.WriteLine(LOG_IDENT, $"Diagnostic snapshot complete: {zipPath}");
            return zipPath;
        }

        // Every entry goes through here, so this is the one place the username scrub has to
        // happen for the whole zip to be clean.
        private static void WriteEntry(ZipOutputStream zip, string entryName, string contents, DateTime? modified = null)
        {
            try
            {
                var entry = new ZipEntry(entryName) { DateTime = modified ?? DateTime.UtcNow };
                zip.PutNextEntry(entry);
                byte[] bytes = Encoding.UTF8.GetBytes(LogScrubber.ScrubUserName(contents));
                zip.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::WriteEntry::" + entryName, ex);
            }
        }

        // Reads a file that another process may still hold open — our own live log, or a Roblox
        // client log for a session that's still running.
        private static string ReadShared(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }

        // Settings.json is copied into the bundle verbatim, and the bundle is something the user
        // emails us or drops in a Discord channel. The TOOLS tabs store the user's own paid API
        // keys there in plaintext (BloxGenApiKey, LuaObfuscatorApiKey, BypassToolsApiKey), so they
        // have to come out before the file is written. Matches "Key": "value" and keeps the shape
        // intact so the JSON is still readable.
        private static readonly Regex SecretSettings = new(
            @"""([A-Za-z]*(?:ApiKey|Token|Secret|Cookie))""\s*:\s*""[^""]*""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static string RedactSecrets(string json)
        {
            try
            {
                return SecretSettings.Replace(json, m =>
                    string.IsNullOrEmpty(m.Value) ? m.Value : $"\"{m.Groups[1].Value}\": \"<redacted>\"");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::RedactSecrets", ex);
                return "(redaction failed, settings withheld)";
            }
        }

        private static string SafeReadFile(string? path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return $"(no file at {path ?? "<null>"})";
                return File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                return $"(read failed: {ex.GetType().Name}: {ex.Message})";
            }
        }

        private static async Task<string> BuildEnvironmentAsync()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"App version      : v{App.Version}");
            try
            {
                sb.AppendLine($"Build commit     : {App.BuildMetadata.CommitHash} ({App.BuildMetadata.CommitRef})");
                sb.AppendLine($"Build timestamp  : {App.BuildMetadata.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
                sb.AppendLine($"Build machine    : {App.BuildMetadata.Machine}");
            }
            catch { sb.AppendLine("Build metadata   : (unavailable)"); }
            sb.AppendLine($"OS               : {Environment.OSVersion}");
            sb.AppendLine($"OS architecture  : {RuntimeInformation.OSArchitecture}");
            sb.AppendLine($"Process arch     : {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($"Runtime          : {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"Locale           : {CultureInfo.CurrentCulture.Name}");
            sb.AppendLine($"UI culture       : {CultureInfo.CurrentUICulture.Name}");
            sb.AppendLine($"Machine name     : {Environment.MachineName}");
            sb.AppendLine($"User name        : {Environment.UserName}");
            sb.AppendLine($"Process path     : {Paths.Process}");
            sb.AppendLine($"Base path        : {Paths.Base}");
            sb.AppendLine($"Logs path        : {Paths.Logs}");
            sb.AppendLine($"DebugOutput path : {Paths.DebugOutput}");
            sb.AppendLine($"Portable mode    : {App.IsPortableMode}");
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                sb.AppendLine($"Elevated         : {new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator)}");
            }
            catch { sb.AppendLine("Elevated         : (unknown)"); }

            try
            {
                string? drive = Path.GetPathRoot(Path.GetFullPath(Paths.Base));
                if (!string.IsNullOrEmpty(drive))
                {
                    var info = new DriveInfo(drive);
                    sb.AppendLine($"Install drive    : {drive} ({info.AvailableFreeSpace / (1024 * 1024 * 1024)} GB free of {info.TotalSize / (1024 * 1024 * 1024)} GB)");
                }
            }
            catch { /* best-effort */ }

            await Task.CompletedTask;
            return sb.ToString();
        }

        private static string BuildAdapterReport()
        {
            var sb = new StringBuilder();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    sb.AppendLine($"{nic.Name} | {nic.Description}");
                    sb.AppendLine($"  Type: {nic.NetworkInterfaceType}");
                    sb.AppendLine($"  Status: {nic.OperationalStatus}");
                    sb.AppendLine($"  MAC: {NetworkAdapterMacFormat(nic.GetPhysicalAddress().ToString())}");
                    sb.AppendLine($"  Speed: {(nic.Speed > 0 ? nic.Speed.ToString("N0") + " bps" : "unknown")}");
                    sb.AppendLine($"  Id: {nic.Id}");
                    sb.AppendLine();
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(adapter enumeration failed: {ex.GetType().Name}: {ex.Message})");
            }
            return sb.ToString();
        }

        private static string NetworkAdapterMacFormat(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw.Length != 12) return raw ?? "";
            return string.Join("-", Enumerable.Range(0, 6).Select(i => raw.Substring(i * 2, 2)));
        }

        private static string BuildProcessReport()
        {
            var sb = new StringBuilder();
            string[] names = { "RobloxPlayerBeta", "RobloxStudioBeta", "RobloxCrashHandler", App.ProjectName };
            foreach (var name in names)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(name); }
                catch { continue; }

                foreach (var p in procs)
                {
                    try
                    {
                        sb.AppendLine($"{name} pid={p.Id} uptime={DateTime.Now - p.StartTime:hh\\:mm\\:ss} mem={p.WorkingSet64 / 1024 / 1024} MB");
                    }
                    catch { /* process exited mid-enumeration */ }
                    finally { p.Dispose(); }
                }
            }
            if (sb.Length == 0)
                sb.AppendLine("(no Roblox or BeastStrap processes were running at snapshot time)");
            return sb.ToString();
        }

        private static async Task<string> BuildHealthReportAsync()
        {
            try
            {
                var results = await HealthCheck.RunAllAsync();
                var sb = new StringBuilder();
                foreach (var r in results)
                    sb.AppendLine($"[{r.Status}] {r.Category} / {r.Name}: {r.Detail}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"(health check failed: {ex.GetType().Name}: {ex.Message})";
            }
        }

        private static async Task<string> BuildUpdateProbeAsync()
        {
            var sb = new StringBuilder();
            string endpoint = $"{App.ProjectApiBase}/repos/{App.ProjectRepository}/releases/latest";
            sb.AppendLine($"Endpoint        : {endpoint}");

            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, endpoint);
                using var resp = await App.HttpClient.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                stopwatch.Stop();
                sb.AppendLine($"Status          : {(int)resp.StatusCode} {resp.ReasonPhrase}");
                sb.AppendLine($"Elapsed         : {stopwatch.ElapsedMilliseconds} ms");
                if (resp.Headers.TryGetValues("server", out var server))
                    sb.AppendLine($"server          : {string.Join(",", server)}");
                if (resp.Headers.TryGetValues("x-ratelimit-remaining", out var rl))
                    sb.AppendLine($"rate-remaining  : {string.Join(",", rl)}");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                sb.AppendLine($"Status          : (exception after {stopwatch.ElapsedMilliseconds} ms)");
                sb.AppendLine($"Error class     : {ex.GetType().FullName}");
                sb.AppendLine($"Error message   : {ex.Message}");
                if (ex.InnerException != null)
                {
                    sb.AppendLine($"Inner class     : {ex.InnerException.GetType().FullName}");
                    sb.AppendLine($"Inner message   : {ex.InnerException.Message}");
                }
            }
            return sb.ToString();
        }

        private static void AddLogFolder(ZipOutputStream zip)
        {
            if (string.IsNullOrEmpty(Paths.Logs) || !Directory.Exists(Paths.Logs))
                return;

            // The current session's log is the one that matters most at crash time, but it's
            // also the one the logger is still writing to — and writes are flushed
            // fire-and-forget, so its on-disk copy is usually empty/partial right now. Capture
            // it from the logger's in-memory History instead; that's the complete, authoritative
            // record. Past sessions are fully flushed, so those are copied straight off disk.
            string? activeLog = App.Logger.FileLocation;
            bool capturedActive = false;

            string[] files;
            try { files = Directory.GetFiles(Paths.Logs); }
            catch (Exception ex) { App.Logger.WriteException(LOG_IDENT + "::EnumLogs", ex); return; }

            foreach (var file in files)
            {
                if (!capturedActive && !string.IsNullOrEmpty(activeLog) && PathsEqual(file, activeLog))
                {
                    WriteEntry(zip, "logs/" + Path.GetFileName(file), ReadLiveSession() ?? SafeReadFile(file));
                    capturedActive = true;
                    continue;
                }

                try
                {
                    // Read as text rather than stream-copying: the entry has to pass through
                    // WriteEntry for the username scrub to apply.
                    WriteEntry(zip, "logs/" + Path.GetFileName(file), ReadShared(file), File.GetLastWriteTimeUtc(file));
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::AddLog::" + Path.GetFileName(file), ex);
                }
            }

            // If the live log never had a file on disk to match against (NoWriteMode, temp-dir
            // fallback, or a crash before the file was created), still emit what's in memory so
            // the session that crashed isn't lost.
            if (!capturedActive)
            {
                string? live = ReadLiveSession();
                if (!string.IsNullOrEmpty(live))
                    WriteEntry(zip, "logs/current-session.log", live);
            }
        }

        // Snapshot of the logger's in-memory History. Returns null if it can't be read cleanly
        // (e.g. another thread is logging mid-snapshot) so callers can fall back to the disk file.
        private static string? ReadLiveSession()
        {
            try { return App.Logger.AsDocument; }
            catch { return null; }
        }

        private static bool PathsEqual(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        // Roblox writes its own client logs to %LocalAppData%\Roblox\logs, one per launch. That's
        // where a Roblox-side crash actually shows up — our own logs only see that the process
        // died. Best-effort: a missing folder or a file Roblox still holds open never breaks
        // the export.
        //
        // The window has to match logs/ or the bundle is unreadable. Until 2026-07-19 this took
        // a flat newest-5, and skully's crash report landed with our logs going back to the
        // previous day while all five Roblox logs were from after the user believed they'd fixed
        // it — so the sessions that actually crashed had no client-side evidence at all. Anchor
        // on the oldest BeastStrap log instead, keeping newest-5 as a floor and a hard cap so a
        // log dir that's never been cleaned can't bloat the zip.
        private const int RobloxLogFloor = 5;

        private static void AddRobloxLogs(ZipOutputStream zip, int max = 24)
        {
            string logDir;
            try { logDir = Path.Combine(Paths.LocalAppData, "Roblox", "logs"); }
            catch { return; }

            if (!Directory.Exists(logDir))
                return;

            DateTime cutoff = OldestAppLogUtc();

            FileInfo[] files;
            try
            {
                files = new DirectoryInfo(logDir).GetFiles("*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Where((f, i) => i < RobloxLogFloor || f.LastWriteTimeUtc >= cutoff)
                    .Take(max)
                    .ToArray();
            }
            catch (Exception ex) { App.Logger.WriteException(LOG_IDENT + "::EnumRobloxLogs", ex); return; }

            App.Logger.WriteLine(LOG_IDENT, $"Including {files.Length} Roblox client log(s) back to {cutoff:u}");

            foreach (var file in files)
            {
                try
                {
                    // Roblox logs are full of "C:\Users\<name>\..." paths, so these go through
                    // WriteEntry (and its scrub) like everything else.
                    WriteEntry(zip, "roblox-logs/" + file.Name, ReadShared(file.FullName), file.LastWriteTimeUtc);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::AddRobloxLog::" + file.Name, ex);
                }
            }
        }

        // Timestamp of the oldest BeastStrap log we're shipping, used to size the Roblox-log
        // window to match. Falls back to two days on any failure — wide enough to cover a typical
        // report, narrow enough not to sweep in months of stale client logs.
        private static DateTime OldestAppLogUtc()
        {
            try
            {
                if (string.IsNullOrEmpty(Paths.Logs) || !Directory.Exists(Paths.Logs))
                    return DateTime.UtcNow.AddDays(-2);

                var files = new DirectoryInfo(Paths.Logs).GetFiles("*.log");
                return files.Length == 0
                    ? DateTime.UtcNow.AddDays(-2)
                    : files.Min(f => f.LastWriteTimeUtc);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::OldestAppLogUtc", ex);
                return DateTime.UtcNow.AddDays(-2);
            }
        }

        // FastFlags live per Versions Manager profile at Paths.FastFlagProfiles\<profileId>.json.
        // Exporting only the active profile's set (the behaviour until 2026-07-19) silently loses
        // the flags that were live during a crash whenever the user has switched profiles since —
        // exactly what happened in skully's report, where the crashing sessions ran the 'Matcha'
        // profile but the export captured 'live-builtin'. Ship every profile, with an index so the
        // reader can tell which is which and which one was active.
        private static void AddFastFlagProfiles(ZipOutputStream zip)
        {
            // Keep the flat entry: it's the active profile's set, and it's what the older bundle
            // format carried, so anything that reads old exports keeps working.
            WriteEntry(zip, "fastflags.json", SafeReadFile(App.FastFlags.FileLocation));

            if (string.IsNullOrEmpty(Paths.FastFlagProfiles) || !Directory.Exists(Paths.FastFlagProfiles))
                return;

            string[] files;
            try { files = Directory.GetFiles(Paths.FastFlagProfiles, "*.json"); }
            catch (Exception ex) { App.Logger.WriteException(LOG_IDENT + "::EnumFastFlagProfiles", ex); return; }

            string activeId = App.Settings?.Prop?.ActiveVersionProfileId ?? "";

            var index = new StringBuilder();
            index.AppendLine("Per-profile FastFlag sets, one file each at fastflags/<profile-id>.json.");
            index.AppendLine("The active profile's set is also duplicated at the top level as fastflags.json.");
            index.AppendLine();

            foreach (var file in files)
            {
                try
                {
                    string id = Path.GetFileNameWithoutExtension(file);
                    var profile = App.Settings?.Prop?.VersionProfiles?.FirstOrDefault(p => p.Id == id);
                    string name = profile?.Name ?? "(no matching profile — orphaned flag file)";
                    string marker = string.Equals(id, activeId, StringComparison.Ordinal) ? "   <-- ACTIVE" : "";

                    index.AppendLine($"{id,-38}  {name}{marker}");
                    WriteEntry(zip, $"fastflags/{id}.json", SafeReadFile(file), File.GetLastWriteTimeUtc(file));
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::AddFastFlagProfile::" + Path.GetFileName(file), ex);
                }
            }

            WriteEntry(zip, "fastflags/_index.txt", index.ToString());
        }
    }
}
