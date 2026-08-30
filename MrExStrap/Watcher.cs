using System.Runtime.InteropServices;

using BeastStrap.AppData;
using BeastStrap.Integrations;
using BeastStrap.Models;

namespace BeastStrap
{
    public class Watcher : IDisposable
    {
        // Set once the watcher data is deserialised, so it can be scoped to the client PID this
        // watcher is for. A single machine-wide "Watcher" lock meant only the first concurrent
        // session ever got a watcher — see the matching comment at the spawn site in
        // Bootstrapper.StartRoblox.
        private InterProcessLock? _lock;

        private readonly WatcherData? _watcherData;
        
        private readonly NotifyIconWrapper? _notifyIcon;

        public readonly ActivityWatcher? ActivityWatcher;

        public readonly DiscordRichPresence? RichPresence;

        // v420.46: rewrites the Roblox window (custom icon / title / fake borderless).
        // Ported from FishyStrap. Only built when enabled and a window handle was captured.
        public readonly WindowManipulation? WindowManipulation;

        // If Roblox exits sooner than this after launch AND never reached gameplay, Run() treats it as
        // a probable crash. Covers the observed 7-40s crash-on-launch window with margin while staying
        // below where a normal player is reliably in-game. Single knob for tuning.
        private const int ProbableCrashWindowSeconds = 60;

        // Set from ActivityWatcher.OnAppClose ("[FLog::SingleSurfaceApp] leaveUGCGameInternal"), i.e.
        // the user deliberately closed the client. Written on the log-reader thread, read on the
        // watcher thread, hence volatile.
        private volatile bool _userClosedApp;

        // Markers Roblox writes on its way out under its own steam. A client that logged any of these
        // ran its shutdown path — it did not crash, whatever else we think we know.
        private static readonly string[] GracefulShutdownMarkers =
        {
            "[FLog::SessionTransitionFSM] Tearing down",
            "Platform handler was destroyed",
            "AppPlatformQoSEmergencyHandler was destroyed",
            "unregisterMemoryPrioritizationCallback"
        };

        public Watcher()
        {
            const string LOG_IDENT = "Watcher";

            string? watcherDataArg = App.LaunchSettings.WatcherFlag.Data;

            if (String.IsNullOrEmpty(watcherDataArg))
            {
#if DEBUG
                string path = new RobloxPlayerData().ExecutablePath;
                if (!File.Exists(path))
                    throw new ApplicationException("Roblox player is not been installed");

                using var gameClientProcess = Process.Start(path);

                _watcherData = new() { ProcessId = gameClientProcess.Id };
#else
                throw new Exception("Watcher data not specified");
#endif
            }
            else
            {
                _watcherData = JsonSerializer.Deserialize<WatcherData>(Encoding.UTF8.GetString(Convert.FromBase64String(watcherDataArg)));
            }

            if (_watcherData is null)
                throw new Exception("Watcher data is invalid");

            // Now that we know which client this watcher belongs to, claim the per-client lock.
            _lock = new InterProcessLock($"Watcher-{_watcherData.ProcessId}");

            if (!_lock.IsAcquired)
            {
                App.Logger.WriteLine(LOG_IDENT, $"A watcher for PID {_watcherData.ProcessId} already exists");
                return;
            }

            if (App.Settings.Prop.EnableActivityTracking)
            {
                ActivityWatcher = new(_watcherData.LogFile);

                // The client told us the user is leaving of their own accord. Record it ALWAYS, not
                // just under UseDisableAppPatch — this is the only real "the user meant to close it"
                // signal we get, and the crash gate below needs it. Without this, a deliberate quit
                // was indistinguishable from a crash and we accused ourselves of killing the client.
                ActivityWatcher.OnAppClose += delegate
                {
                    _userClosedApp = true;
                };

                if (App.Settings.Prop.UseDisableAppPatch)
                {
                    ActivityWatcher.OnAppClose += delegate
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Received desktop app exit, closing Roblox");

                        // Both calls below throw when the client has already gone: GetProcessById
                        // raises ArgumentException for an unknown pid, and CloseMainWindow raises
                        // InvalidOperationException when there is no main window. That is the
                        // normal ordering when the client exits before the log tail catches up,
                        // and it used to kill the watcher from inside its own log-reader thread.
                        try
                        {
                            using var process = Process.GetProcessById(_watcherData.ProcessId);
                            process.CloseMainWindow();
                        }
                        catch (Exception ex)
                        {
                            App.Logger.WriteLine(LOG_IDENT, "Roblox had already exited, nothing to close.");
                            App.Logger.WriteException(LOG_IDENT + "::OnAppClose", ex);
                        }
                    };
                }

                if (App.Settings.Prop.UseDiscordRichPresence)
                    RichPresence = new(ActivityWatcher);
            }

            // v420.46: window manipulation — custom icon, custom title and fake borderless
            // fullscreen on the running Roblox window. Only when enabled AND the bootstrapper
            // captured a window handle (the handle is zero if the window never showed).
            if (App.Settings.Prop.EnableWindowManipulation && _watcherData.Handle != 0)
                WindowManipulation = new(_watcherData.Handle, _watcherData.ProcessId);

            _notifyIcon = new(this);
        }

        public void KillRobloxProcess() => CloseProcess(_watcherData!.ProcessId, true);

        public void CloseProcess(int pid, bool force = false)
        {
            const string LOG_IDENT = "Watcher::CloseProcess";

            try
            {
                using var process = Process.GetProcessById(pid);

                App.Logger.WriteLine(LOG_IDENT, $"Killing process '{process.ProcessName}' (pid={pid}, force={force})");

                if (process.HasExited)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"PID {pid} has already exited");
                    return;
                }

                if (force)
                    process.Kill();
                else
                    process.CloseMainWindow();
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"PID {pid} could not be closed");
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        public async Task Run()
        {
            const string LOG_IDENT = "Watcher::Run";

            if (_lock is null || !_lock.IsAcquired || _watcherData is null)
                return;

            // Multi-instance: the watcher is the longest-lived BeastStrap process in a play
            // session, so it owns Roblox's single-instance lock while the client runs. While
            // we own it, no client can become the primary instance and close the others.
            // See Utility.MultiInstance for the full picture. The flag covers account launches
            // (Multi Instance tab) that force multi-instance without the global toggle.
            if (App.Settings.Prop.MultiInstanceEnabled || App.LaunchSettings.MultiInstanceFlag.Active)
            {
                Utility.MultiInstance.HoldSingletonMutex();

                // Safety net for the case where the client became the primary instance anyway:
                // close its singleton event so the next launch doesn't kill it. Scheduled here
                // rather than in the bootstrapper because the bootstrapper exits before the
                // sweep's first probe — see the note at its old call site in StartRoblox.
                App.Logger.WriteLine(LOG_IDENT, $"Multi-instance active — scheduling singleton sweep (PID {_watcherData.ProcessId})");
                Utility.MultiInstance.ScheduleSingletonSweep();
            }

            // Window tiling: arrange all Roblox windows into a grid once the client has a window.
            // Also moved off the bootstrapper, which used to exit during the tiler's 5s sleep.
            if (App.Settings.Prop.WindowTilingEnabled)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Window tiling enabled — scheduling tile pass with layout {App.Settings.Prop.WindowTilingLayout}");
                Utility.WindowTiler.ScheduleTilePass(App.Settings.Prop.WindowTilingLayout);
            }

            ActivityWatcher?.Start();

            // v420.46: window manipulation — applies the custom icon / title / fake borderless
            // to the Roblox window once the client's window is up (the handle was captured by
            // the bootstrapper). Runs once at watcher start.
            WindowManipulation?.Start();

            // v420.28: when Stream Mode is on, keep Roblox's window title
            // rewritten to a generic "Roblox" so streamers don't leak game /
            // account info to viewers. Runs for the lifetime of the watcher.
            using var streamModeCts = new CancellationTokenSource();
            Task? streamModeTask = null;
            if (Utility.StreamMode.IsActive)
            {
                streamModeTask = Utility.StreamMode.RewriteWindowTitleLoopAsync(
                    _watcherData.ProcessId, streamModeCts.Token);
            }

            // v420.26: rolled back v420.23's MainWindowHandle-based force-kill. It
            // was killing live Roblox sessions when the main window briefly went to
            // IntPtr.Zero mid-game (fullscreen toggles, loading screens, in-game
            // transitions). flippi's 2026-05-24 report showed Roblox dying after
            // about a minute of normal gameplay. We're back to passive polling for
            // PID exit — same behaviour as pre-v420.23. The always-spawn change
            // from v420.23 stays (so AutoclosePids cleanup runs for everyone even
            // without EnableActivityTracking).
            bool closeCrashHandler = App.Settings.Prop.CloseRobloxCrashHandler;

            // Record when Roblox started so that when the PID poll below exits we can tell a
            // crash-on-launch from a normal session. The Roblox log file's creation time is the
            // closest "client actually started" signal we have; fall back to now if it can't be read.
            DateTime sessionStartUtc;
            try { sessionStartUtc = new FileInfo(_watcherData.LogFile!).CreationTimeUtc; }
            catch { sessionStartUtc = DateTime.UtcNow; }

            // Hold a handle to the client for the duration so its exit code survives the process
            // itself. The PID poll below can only ever tell us "it's gone" — the exit code tells us
            // whether it left of its own accord (0), was killed (1), or died on an access violation
            // (0xC0000005 and friends). Best-effort: this can fail on access-denied or if the
            // process is already gone, in which case we just don't get the extra signal.
            Process? watchedClient = null;
            try
            {
                watchedClient = Process.GetProcessById(_watcherData.ProcessId);

                // Force the Process object to open and RETAIN a kernel handle.
                //
                // Process.ExitCode requires the object to hold one, and GetProcessById does not set
                // that flag — HasExited opens a handle lazily and releases it again, so ExitCode
                // threw "Process was not started by this object, so requested information cannot be
                // determined" on EVERY session. A user's bundle had it fail 8 times out of 8 across
                // ten days, which meant the one signal that cleanly separates "the user quit" from
                // "it died" was never available and the crash gate ran on guesswork.
                //
                // Touching .Handle calls OpenProcessHandle, which associates the handle for good.
                _ = watchedClient.Handle;
            }
            catch (Exception ex) { App.Logger.WriteException(LOG_IDENT + "::OpenClientHandle", ex); }

            // Poll the handle we already hold rather than snapshotting every process on the machine
            // once a second. Two reasons beyond the obvious waste: Process.HasExited on a held
            // handle is a single GetExitCodeProcess call, and GetProcessesSafe() swallows
            // ArithmeticException by returning an EMPTY array — which at this call site was
            // indistinguishable from "the client is gone" and ended the watch mid-session.
            while (ClientStillRunning(watchedClient))
            {
                // Froststrap-style memory saver: keep RobloxCrashHandler closed while Roblox runs.
                if (closeCrashHandler)
                    CloseRobloxCrashHandlers();

                // Multi-instance RAM reducer: every few seconds trim the working sets of the farm
                // clients that aren't the watched one or the focused window. Idempotent, cheap,
                // and skipped entirely when the reducer (or its working-set trim) is disabled.
                Utility.MultiInstanceRamReducer.TrimOnceEvery(5, _watcherData.ProcessId);

                await Task.Delay(1000);
            }

            streamModeCts.Cancel();
            if (streamModeTask is not null)
            {
                try { await streamModeTask; } catch { /* expected on cancel */ }
            }

            if (_watcherData.AutoclosePids is not null)
            {
                foreach (int pid in _watcherData.AutoclosePids)
                    CloseProcess(pid);
            }

            if (App.LaunchSettings.TestModeFlag.Active)
                Process.Start(Paths.Process, "-settings -testmode");

            // The Roblox PID just disappeared. If it died quickly and never reached gameplay, treat it
            // as a probable crash and surface the crash dialog, whose one-click export produces a
            // bundle that includes the Roblox client logs. "Never reached gameplay" (ActivityWatcher
            // never saw 'Replicator created:') is the discriminator: a normal quit almost always
            // happens after joining, so it's excluded. If activity tracking is off we can't tell,
            // so we stay quiet.
            int? exitCode = null;
            try
            {
                if (watchedClient is not null && watchedClient.HasExited)
                    exitCode = watchedClient.ExitCode;
            }
            catch (Exception ex)
            {
                // The watched client was attached to (GetProcessById), not started by this Process
                // object, so .ExitCode can throw "Process was not started by this object". Fall back
                // to a native GetExitCodeProcess read — Roblox may already be gone, but the handle
                // still carries the exit code.
                App.Logger.WriteException(LOG_IDENT + "::ReadClientExitCode", ex);
                exitCode ??= ReadExitCodeNative(_watcherData.ProcessId);
            }
            finally { watchedClient?.Dispose(); }

            // Did another process pick this session up rather than it dying? Multi Instance spawns a
            // throwaway starter that exits in about a second while the real client carries on under a
            // different PID — and that successor keeps writing to the SAME log file.
            //
            // This used to be answered with "is ANY RobloxPlayerBeta still alive". That is true almost
            // permanently once you have two clients open, so the crash dialog was suppressed for
            // precisely the Multi Instance users who needed it — a 2026-07-27 report had three dead
            // clients and got one dialog. Ask about this session's own log instead: a live client
            // never stops writing to it (the idle home screen alone logs several lines a second),
            // and a dead one stops immediately.
            bool sessionPickedUpByAnotherProcess = LogFileStillGrowing();

            // AFTER LogFileStillGrowing, which sleeps 3s. Taking it before meant the window handed to
            // the analyzer closed three seconds before the decision was actually made.
            DateTime sessionEndUtc = DateTime.UtcNow;
            TimeSpan sessionLength = sessionEndUtc - sessionStartUtc;

            if (exitCode is not null)
                App.Logger.WriteLine(LOG_IDENT, $"Roblox client PID {_watcherData.ProcessId} exited with 0x{exitCode:X8} after {sessionLength.TotalSeconds:F0}s");

            // A crash must be DEMONSTRATED, not merely un-disproven.
            //
            // This gate used to be "never reached gameplay AND died within 60s AND its log stopped
            // growing". Every one of those is true of a completely normal quit: someone who opens
            // Roblox, sits on the home screen and closes it never reaches gameplay, and the log stops
            // growing precisely BECAUSE they quit. So we told users their game had crashed, and then
            // blamed ourselves for it, every single time they closed Roblox without playing.
            //
            // So look for positive evidence that the client left under its own steam first, and only
            // fall through to the timing heuristic when we genuinely have nothing.
            string? notACrashReason =
                exitCode == 0 ? "it exited with code 0"
                : ClientLoggedGracefulShutdown() ? "it logged its normal shutdown sequence"
                : _userClosedApp ? "the user closed the client"
                : TrayModeSuccessorStarted(sessionEndUtc) ? "Roblox handed the session to its tray process"
                : sessionPickedUpByAnotherProcess ? "another process took the session over"
                : null;

            bool everInGame = (ActivityWatcher?.InGame ?? false) || (ActivityWatcher?.History.Count > 0);
            bool probableCrash = ActivityWatcher is not null
                && notACrashReason is null
                && !everInGame
                && sessionLength < TimeSpan.FromSeconds(ProbableCrashWindowSeconds);

            if (notACrashReason is not null)
            {
                App.Logger.WriteLine(LOG_IDENT,
                    $"Roblox PID {_watcherData.ProcessId} exited after {sessionLength.TotalSeconds:F0}s — not a crash, {notACrashReason}");
            }

            if (probableCrash)
            {
                App.Logger.WriteLine(LOG_IDENT,
                    $"Roblox exited after {sessionLength.TotalSeconds:F0}s without reaching in-game — probable crash");

                // Users who Alt+F4 out of the home screen hit this constantly as a false positive,
                // so the dialog is opt-in (Settings -> Integrations -> Crash notifications).
                if (!App.Settings.Prop.EnableCrashNotifications)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Crash notifications are disabled — suppressing the crash dialog.");
                    return;
                }

                try
                {
                    // Blocks (self-marshals to the UI dispatcher) until the user dismisses it, which
                    // also holds off the watcher's teardown/terminate until they're done exporting.
                    // The lifetime is passed through so the analyzer only weighs what BeastStrap was
                    // doing while this client was actually alive.
                    UI.Frontend.ShowPlayerErrorDialog(crash: true, clientStartUtc: sessionStartUtc, clientEndUtc: sessionEndUtc);
                }
                catch (Exception ex)
                {
                    // Never let a dialog failure stop normal watcher teardown.
                    App.Logger.WriteException(LOG_IDENT, ex);
                }
            }
        }

        // Is the client we're watching still alive? Uses the held handle when we have one and only
        // falls back to the process-table scan when opening it failed (access denied, or it had
        // already exited by the time the watcher started).
        private bool ClientStillRunning(Process? watchedClient)
        {
            if (watchedClient is not null)
            {
                try { return !watchedClient.HasExited; }
                catch (Exception ex) { App.Logger.WriteException("Watcher::ClientStillRunning", ex); }
            }

            return Utilities.GetProcessesSafe().Any(x => x.Id == _watcherData!.ProcessId);
        }

        // True when this session's Roblox log grew over a short sample, i.e. a live client still owns
        // it. Anything that goes wrong here returns false, which means we err towards showing the
        // crash dialog rather than silently swallowing a real crash.
        private bool LogFileStillGrowing()
        {
            const string LOG_IDENT = "Watcher::LogFileStillGrowing";

            try
            {
                if (_watcherData is null || string.IsNullOrEmpty(_watcherData.LogFile))
                    return false;

                var log = new FileInfo(_watcherData.LogFile);
                if (!log.Exists)
                    return false;

                long before = log.Length;

                Thread.Sleep(TimeSpan.FromSeconds(3));

                log.Refresh();
                return log.Exists && log.Length > before;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                return false;
            }
        }

        // Did the client write its own shutdown sequence? Roblox logs a recognisable teardown on the
        // way out under its own steam, and a process that got there did not crash. Reads the tail
        // only — these markers are the last thing in the file.
        private bool ClientLoggedGracefulShutdown()
        {
            const string LOG_IDENT = "Watcher::ClientLoggedGracefulShutdown";

            try
            {
                if (_watcherData is null || string.IsNullOrEmpty(_watcherData.LogFile) || !File.Exists(_watcherData.LogFile))
                    return false;

                // Share everything: the client may still hold the file open, and a failure to read
                // here must never be reported as a crash.
                using var stream = new FileStream(_watcherData.LogFile, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                const int TailBytes = 16 * 1024;
                if (stream.Length > TailBytes)
                    stream.Seek(-TailBytes, SeekOrigin.End);

                using var reader = new StreamReader(stream);
                string tail = reader.ReadToEnd();

                foreach (string marker in GracefulShutdownMarkers)
                {
                    if (tail.Contains(marker, StringComparison.Ordinal))
                        return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                return false;
            }
        }

        // Roblox routinely exits the client it just launched and continues in a second process that
        // reports "AppState/TrayMode". That successor writes a brand new log, so a fresh Roblox log
        // appearing around the time ours died means the session was handed off, not lost. Without
        // this, a normal launch handoff looked exactly like a crash-on-launch.
        private bool TrayModeSuccessorStarted(DateTime sessionEndUtc)
        {
            const string LOG_IDENT = "Watcher::TrayModeSuccessorStarted";

            try
            {
                if (_watcherData is null || string.IsNullOrEmpty(_watcherData.LogFile))
                    return false;

                string? dir = Path.GetDirectoryName(_watcherData.LogFile);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    return false;

                foreach (string path in Directory.EnumerateFiles(dir, "*_Player_*.log"))
                {
                    if (string.Equals(path, _watcherData.LogFile, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var info = new FileInfo(path);

                    // Created in the window around our client's exit. Generous on both sides: the
                    // handoff observed in the wild took about 8 seconds.
                    double age = (info.CreationTimeUtc - sessionEndUtc).TotalSeconds;
                    if (age < -30 || age > 30)
                        continue;

                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);

                    // The userAgent line is written within the first handful of lines.
                    for (int i = 0; i < 40; i++)
                    {
                        string? line = reader.ReadLine();
                        if (line is null)
                            break;

                        if (line.Contains("AppState/TrayMode", StringComparison.Ordinal))
                            return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                return false;
            }
        }

        // Reads the exit code of an already-gone process via a native handle, which keeps working
        // after the process itself has exited. Returns null when the process is still running
        // (STILL_ACTIVE), access was denied, or the code could not be determined.
        private static int? ReadExitCodeNative(int processId)
        {
            const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            const uint STILL_ACTIVE = 0x103;

            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (handle == IntPtr.Zero)
                return null;
            try
            {
                if (!GetExitCodeProcess(handle, out uint code))
                    return null;
                return code == STILL_ACTIVE ? null : (int?)code;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint exitCode);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        // Froststrap-style memory saver: close RobloxCrashHandler.exe while Roblox runs. It's the
        // out-of-process crash reporter and isn't needed for the game to run, so closing it frees
        private static void CloseRobloxCrashHandlers()
        {
            const string LOG_IDENT = "Watcher::CloseRobloxCrashHandlers";

            foreach (var process in Process.GetProcessesByName("RobloxCrashHandler"))
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        App.Logger.WriteLine(LOG_IDENT, $"Closed RobloxCrashHandler (pid={process.Id}) to free memory");
                    }
                }
                catch { /* best-effort: handler may be exiting or briefly protected */ }
                finally { process.Dispose(); }
            }
        }

        public void Dispose()
        {
            const string LOG_IDENT = "Watcher::Dispose";

            App.Logger.WriteLine(LOG_IDENT, "Disposing Watcher");

            // Each step is guarded on its own. This runs in the Task continuation that also calls
            // App.Terminate(), so anything escaping here stops the process from ever exiting —
            // and with the tray icon already gone by that point, the leftover process is invisible.
            // One failing step must not skip the others either.
            Step("ActivityWatcher", () =>
            {
                // First, because it owns the log tail and its subscribers are the things being torn
                // down below — leaving it running meant a buffered log line could fire rich presence
                // or the tray toast at an already-disposed object. Its FileStream on the Roblox log
                // was never released either.
                ActivityWatcher?.Dispose();
            });

            Step("NotifyIcon", () => _notifyIcon?.Dispose());
            Step("RichPresence", () => RichPresence?.Dispose());

            // Unhooks the EVENT_OBJECT_NAMECHANGE hook the title rewrite installed.
            Step("WindowManipulation", () => WindowManipulation?.Dispose());

            // Release the per-client watcher lock. It was never disposed before, which didn't
            // matter while the name was machine-wide and the process exited straight after — but
            // now that it's per-PID and could be re-taken, hand it back properly.
            Step("InterProcessLock", () => _lock?.Dispose());

            GC.SuppressFinalize(this);

            static void Step(string name, Action action)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Failed to dispose {name}, carrying on");
                    App.Logger.WriteException(LOG_IDENT, ex);
                }
            }
        }
    }
}
