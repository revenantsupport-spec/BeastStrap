// Multi-instance RAM reducer, two layers:
//
// 1) Launch-time FastFlags (all LIVE allowlisted, same keys the FastFlags editor uses).
//    Farm clients launch with a capped framerate (DFIntTaskSchedulerTargetFps), clamped
//    textures (DFFlagTextureQualityOverrideEnabled + DFIntTextureQualityOverride) and no
//    grass (FIntFRMMin/MaxGrassDistance). They are merged over the active Versions Manager
//    profile's flag set for multi-instance launches ONLY — and replace whatever the profile
//    set for those exact keys, which is the point of the toggle. Normal single launches
//    never see them, and turning the toggle off re-materialises the untouched profile set
//    on the next launch.
//
// 2) Runtime working-set trim. The watcher's 1s poll loop calls TrimOnceEvery; every few
//    seconds it walks RobloxPlayerBeta processes and, for every client that is not the
//    focused window, marks it LOW memory priority (SetProcessInformation, so Windows trims
//    it before anything else under pressure) and trims its working set
//    (SetProcessWorkingSetSize with -1/-1 — the classic EmptyWorkingSet). Idempotent and
//    cheap, so every watcher just runs it; the focused client is never touched, because
//    that is the one being looked at and a trim there causes page-fault hitches.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace BeastStrap.Utility
{
    public static class MultiInstanceRamReducer
    {
        private const string LOG_IDENT = "MultiInstanceRamReducer";

        // Canonical overlay file ApplyModifications copies into the install. Mirrors
        // FastFlagProfiles.CanonicalFile, which is private to that class.
        public static string CanonicalFile =>
            Path.Combine(Paths.Modifications, "ClientSettings", "ClientAppSettings.json");

        // Keys this reducer owns outright. When the toggle is on these always win.
        private const string FlagFpsTarget = "DFIntTaskSchedulerTargetFps";
        private const string FlagTextureOverrideEnabled = "DFFlagTextureQualityOverrideEnabled";
        private const string FlagTextureOverrideLevel = "DFIntTextureQualityOverride";
        private const string FlagGrassMin = "FIntFRMMinGrassDistance";
        private const string FlagGrassMax = "FIntFRMMaxGrassDistance";

        // True when the reducer is switched on AND this launch is part of the multi-instance
        // setup — same condition the Watcher uses to decide whether to hold the singleton
        // mutex. The -multiinstance flag covers account/alt launches even when the global
        // toggle is off.
        public static bool IsActive =>
            App.Settings.Prop.MultiInstanceRamReducerEnabled
            && App.Settings.Prop.UseFastFlagManager
            && (App.LaunchSettings.MultiInstanceFlag.Active || App.Settings.Prop.MultiInstanceEnabled);

        #region Launch-time FastFlag layer

        private static Dictionary<string, object> BuildFlags(Settings settings)
        {
            float fps = Math.Clamp(settings.MultiInstanceRamReducerTargetFps, 15, 120);

            var flags = new Dictionary<string, object>
            {
                { FlagFpsTarget, fps.ToString(CultureInfo.InvariantCulture) },
                { FlagGrassMin, "0" },
                { FlagGrassMax, "0" }
            };

            if (settings.MultiInstanceRamReducerLowTextures)
            {
                flags[FlagTextureOverrideEnabled] = "True";
                flags[FlagTextureOverrideLevel] = "0";
            }

            return flags;
        }

        // Merge the reducer flags over the canonical file AFTER the active profile was
        // materialised into it. Non-destructive: everything the profile set is kept, only
        // the reducer's own keys are written. No-op when the reducer isn't active (which
        // also covers the FastFlag manager being off — that path already skips the overlay).
        public static void LayerOverCanonicalIfActive()
        {
            if (!IsActive)
                return;

            try
            {
                Dictionary<string, object> merged = new(StringComparer.OrdinalIgnoreCase);

                if (File.Exists(CanonicalFile))
                {
                    string existing = File.ReadAllText(CanonicalFile).Trim();
                    if (existing.Length > 0 && existing != "{}")
                    {
                        using var doc = JsonDocument.Parse(existing);
                        foreach (var prop in doc.RootElement.EnumerateObject())
                            merged[prop.Name] = prop.Value.Clone();
                    }
                }

                foreach (var pair in BuildFlags(App.Settings.Prop))
                    merged[pair.Key] = pair.Value;

                Directory.CreateDirectory(Path.GetDirectoryName(CanonicalFile)!);
                File.WriteAllText(CanonicalFile, JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true }));

                App.Logger.WriteLine(LOG_IDENT, "Layered RAM-reducer FastFlags over the canonical client settings "
                    + $"(fps cap {App.Settings.Prop.MultiInstanceRamReducerTargetFps}, "
                    + $"low textures {App.Settings.Prop.MultiInstanceRamReducerLowTextures}).");
            }
            catch (Exception ex)
            {
                // A failure here must never block the launch — worst case the farm runs at
                // full quality and the runtime trim below still does its share.
                App.Logger.WriteException(LOG_IDENT + "::LayerOverCanonicalIfActive", ex);
            }
        }

        #endregion

        #region Runtime working-set trim

        // Last trim pass in TickCount64 units. Within one process a second watcher could be
        // running too — Interlocked keeps the throttle exact.
        private static long _lastTrimTick;

        // Pids whose trim was already reported, so the log doesn't spam every pass.
        private static readonly ConcurrentDictionary<int, byte> _trimLogReported = new();

        // Called from the watcher's 1s poll loop. Short-circuits when the reducer is off, or
        // when the working-set trim is disabled, so an unarmed watcher costs nothing per tick.
        public static void TrimOnceEvery(int everySeconds, int watchedPid)
        {
            if (!IsActive || !App.Settings.Prop.MultiInstanceRamReducerTrimWorkingSet)
                return;

            long now = Environment.TickCount64;
            if (Interlocked.Read(ref _lastTrimTick) + everySeconds * 1000L > now)
                return;
            Interlocked.Exchange(ref _lastTrimTick, now);

            TrimBackgroundClients(watchedPid);
        }

        public static void TrimBackgroundClients(int watchedPid)
        {
            int foregroundPid = GetForegroundPid();
            int trimmed = 0;

            foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
            {
                try
                {
                    // Never trim the client this watcher is watching or the one the user is
                    // actively looking at — a trim there just pages everything back in and
                    // adds hitch.
                    if (process.HasExited || process.Id == watchedPid || process.Id == foregroundPid)
                        continue;

                    if (TrimProcess(process.Id))
                        trimmed++;
                }
                catch
                {
                    // Best-effort: the client may be mid-exit or briefly protected.
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (trimmed > 0)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Trimmed working sets of {trimmed} background client(s) "
                    + $"(watched PID {watchedPid}, foreground PID {foregroundPid}).");
            }
        }

        private static bool TrimProcess(int pid)
        {
            IntPtr process = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_SET_QUOTA, false, pid);
            if (process == IntPtr.Zero)
                return false;

            try
            {
                // LOW memory priority: Windows reclaims these clients' pages before anything
                // else once the system is under pressure.
                var info = new PROCESS_MEMORY_PRIORITY_INFO { MemoryPriority = PROCESS_MEMORY_PRIORITY_LOW };
                SetProcessInformation(process, ProcessMemoryPriorityInfo, ref info,
                    (uint)Marshal.SizeOf<PROCESS_MEMORY_PRIORITY_INFO>());

                // EmptyWorkingSet equivalent: hand every physical page back to the OS. The
                // client keeps working — pages fault back in on demand — but resident RAM drops.
                SetProcessWorkingSetSize(process, (IntPtr)(-1), (IntPtr)(-1));

                if (_trimLogReported.TryAdd(pid, 0))
                    App.Logger.WriteLine(LOG_IDENT, $"Marked client pid={pid} LOW memory priority and trimmed its working set.");

                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::TrimProcess", ex);
                return false;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        private static int GetForegroundPid()
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero)
                return 0;

            GetWindowThreadProcessId(foreground, out uint pid);
            return (int)pid;
        }

        #endregion

        #region P/Invoke

        private const uint PROCESS_SET_INFORMATION = 0x0200;
        private const uint PROCESS_SET_QUOTA = 0x0100;
        private const int ProcessMemoryPriorityInfo = 1;
        private const uint PROCESS_MEMORY_PRIORITY_LOW = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_PRIORITY_INFO
        {
            public uint MemoryPriority;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr hProcess, int processInformationClass,
            ref PROCESS_MEMORY_PRIORITY_INFO processInformation, uint processInformationSize);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        #endregion
    }
}