// Multi-instance support, two layers:
//
// 1) Hold "ROBLOX_singletonMutex" from our own process BEFORE any Roblox client starts.
//    Roblox only enforces single-instance when a client manages to take ownership of that
//    mutex and become the "primary" instance — the primary creates ROBLOX_singletonEvent,
//    and the next client to start signals it, shows "the previous instance will be closed"
//    and kills the older client. While an outside process owns the mutex, no client ever
//    becomes primary and every client runs side by side, silently. This is the same
//    long-proven technique used by Roblox Account Manager's "Multi Roblox" button and by
//    MultiBloxy (no code taken from either, the technique is a public one-liner).
//
// 2) Event sweep, adapted from robloxmanager by sasha / centerepic (MIT) —
//    https://gitlab.com/centerepic/robloxmanager. If a client became primary anyway (it
//    started while the setting was off, or no BeastStrap process was alive to hold the
//    mutex), enumerate its handle table and close its "ROBLOX_singletonEvent" so the next
//    launch isn't blocked and doesn't kill it. C# port using NtQuery*/DuplicateHandle.
//    Same-user-only; no admin required.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BeastStrap.Utility
{
    public static class MultiInstance
    {
        private const string LOG_IDENT = "MultiInstance";

        private const string SingletonMutexName = "ROBLOX_singletonMutex";
        // Also used as an EndsWith filter when scanning handle tables, where names come
        // back as full NT paths ("\Sessions\1\BaseNamedObjects\ROBLOX_singletonEvent").
        private const string SingletonEventName = "ROBLOX_singletonEvent";

        #region Singleton mutex holder

        private static Mutex? _heldMutex;
        private static Thread? _holderThread;
        private static readonly object _holderLock = new();
        private static readonly ManualResetEventSlim _holderReady = new(false);

        // Hold ROBLOX_singletonMutex for the lifetime of this process. Mutex ownership is
        // per-thread and a thread that exits abandons it, so a dedicated background thread
        // takes ownership and then sleeps forever. When this process exits, ownership flows
        // to the next BeastStrap process waiting on it (the watcher, or the next launch's
        // bootstrapper) — Roblox clients never own it, they only ever open a handle.
        public static void HoldSingletonMutex()
        {
            lock (_holderLock)
            {
                if (_holderThread is not null)
                    return;

                _holderThread = new Thread(HolderThreadBody)
                {
                    IsBackground = true,
                    Name = "RobloxSingletonMutexHolder"
                };
                _holderThread.Start();
            }
        }

        // Blocks until the holder thread has either taken ownership or confirmed some other
        // live process owns the mutex. Bounded so a launch can never hang on it.
        public static void WaitUntilSingletonMutexHeld(TimeSpan timeout)
        {
            HoldSingletonMutex();

            if (!_holderReady.Wait(timeout))
                App.Logger.WriteLine(LOG_IDENT, "Timed out waiting for the singleton mutex holder, launching anyway.");
        }

        private static void HolderThreadBody()
        {
            try
            {
                _heldMutex = new Mutex(initiallyOwned: true, SingletonMutexName, out bool createdNew);

                // For a named mutex that already exists, initiallyOwned is ignored — we only
                // own it if we created it. Otherwise try to grab it without blocking (covers
                // a stale unowned mutex left behind by a dead holder).
                bool owned = createdNew;
                if (!owned)
                {
                    try
                    {
                        owned = _heldMutex.WaitOne(0);
                    }
                    catch (AbandonedMutexException)
                    {
                        owned = true; // abandonment still grants ownership
                    }
                }

                App.Logger.WriteLine(LOG_IDENT, owned
                    ? $"Holding {SingletonMutexName} — no Roblox client can become the primary instance."
                    : $"{SingletonMutexName} is owned by another process, queueing to inherit it.");

                _holderReady.Set();

                if (!owned)
                {
                    // Queue behind the current owner (another BeastStrap process, or a Roblox
                    // client that launched while multi-instance was off). When it exits we
                    // inherit ownership and keep the session in multi-instance mode.
                    try
                    {
                        _heldMutex.WaitOne();
                    }
                    catch (AbandonedMutexException)
                    {
                        // still granted ownership
                    }

                    App.Logger.WriteLine(LOG_IDENT, $"Inherited ownership of {SingletonMutexName}.");
                }

                // Hold ownership until this process exits. The thread must stay alive — a
                // returning thread would abandon the mutex.
                Thread.Sleep(Timeout.Infinite);
            }
            catch (Exception ex)
            {
                _holderReady.Set(); // never leave a launch waiting on a failed holder
                App.Logger.WriteException(LOG_IDENT + "::Holder", ex);
            }
        }

        #endregion

        // Called by the bootstrapper right before it starts the Roblox client. Makes sure the
        // singleton mutex is held first (so this client can't become primary), then clears the
        // singleton event of any client that became primary earlier — e.g. one that launched
        // while multi-instance was still off. Without that sweep, the client we're about to
        // start would kill it with Roblox's "the previous instance will be closed" dialog.
        public static void PrepareForLaunch()
        {
            WaitUntilSingletonMutexHeld(TimeSpan.FromSeconds(3));

            try
            {
                if (!SingletonEventExists())
                    return;

                App.Logger.WriteLine(LOG_IDENT, "A running client holds the singleton event — sweeping it before launch.");
                SweepSingletonEvents();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Prepare", ex);
            }
        }

        // Safety net behind the held mutex: if the client we just launched still became the
        // primary instance (no BeastStrap process was holding the mutex when it started), its
        // singleton event would make the next launch kill it. Probe for the event for a while
        // and close it wherever it shows up. This replaces the old single pass at a fixed 4s
        // after launch, which silently missed clients that took longer than that to create
        // the event — that's exactly how "the previous instance will close" kept appearing on
        // slower machines with the setting on. When the held mutex did its job the event
        // never exists and every probe is a cheap no-op.
        public static void ScheduleSingletonSweep()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var deadline = DateTime.UtcNow.AddSeconds(45);

                    while (DateTime.UtcNow < deadline)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(3));

                        if (!SingletonEventExists())
                            continue;

                        if (SweepSingletonEvents() > 0)
                            return;
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::Sweep", ex);
                }
            });
        }

        // Close ROBLOX_singletonEvent wherever it's open, without holding the mutex first. The
        // bootstrapper calls this when a launch it started was handed off to an already-running
        // client that then did nothing with it (a leftover tray-mode process, typically): with the
        // event gone there's no handoff target left, so the retry runs on its own instead of
        // exiting into the same dead end. Returns how many handles were closed.
        public static int ClearSingletonEvents()
        {
            try
            {
                return SingletonEventExists() ? SweepSingletonEvents() : 0;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Clear", ex);
                return 0;
            }
        }

        // True if any process in this session currently has ROBLOX_singletonEvent open,
        // i.e. some Roblox client is acting as the primary instance.
        private static bool SingletonEventExists()
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(SingletonEventName, out var handle))
                {
                    handle.Dispose();
                    return true;
                }

                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true; // it exists, we just can't open it (elevated client) — still worth a sweep attempt
            }
            catch
            {
                return false;
            }
        }

        // Close the singleton event in every running Roblox client. Returns how many handles
        // were closed.
        private static int SweepSingletonEvents()
        {
            var pids = new List<int>();

            foreach (var process in Process.GetProcessesByName(App.RobloxPlayerAppName))
            {
                pids.Add(process.Id);
                process.Dispose();
            }

            return CloseSingletonEvents(pids);
        }

        private static int CloseSingletonEvents(IReadOnlyCollection<int> pids)
        {
            if (pids.Count == 0)
                return 0;

            var entries = EnumerateHandles(pids.Select(p => (long)p).ToHashSet());
            if (entries.Count == 0)
            {
                App.Logger.WriteLine(LOG_IDENT, "No handles enumerated for the running Roblox processes.");
                return 0;
            }

            int closed = 0;

            foreach (var group in entries.GroupBy(e => e.UniqueProcessId.ToInt64()))
            {
                IntPtr srcProcess = OpenProcess(PROCESS_DUP_HANDLE | PROCESS_QUERY_LIMITED_INFORMATION, false, (int)group.Key);
                if (srcProcess == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    App.Logger.WriteLine(LOG_IDENT, $"OpenProcess({group.Key}) failed: {err}. Skipping.");
                    continue;
                }

                try
                {
                    foreach (var entry in group)
                    {
                        if (TryCloseIfSingletonEvent(srcProcess, entry))
                            closed++;
                    }
                }
                finally
                {
                    CloseHandle(srcProcess);
                }
            }

            App.Logger.WriteLine(LOG_IDENT, $"Closed {closed} singleton handle(s) across {pids.Count} Roblox process(es).");
            return closed;
        }

        private static bool TryCloseIfSingletonEvent(IntPtr srcProcess, SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX entry)
        {
            // Duplicate the target handle into our process so we can query it safely.
            if (!DuplicateHandle(srcProcess, entry.HandleValue, GetCurrentProcess(),
                out IntPtr dupHandle, 0, false, DUPLICATE_SAME_ACCESS))
            {
                return false;
            }

            try
            {
                // Type first — cheap, and filters out 99% of handles without risking a hang
                // on a name query for a synchronous pipe.
                string? typeName = QueryObjectType(dupHandle);
                if (typeName != "Event")
                    return false;

                string? objectName = QueryObjectName(dupHandle);
                if (string.IsNullOrEmpty(objectName))
                    return false;

                if (!objectName.EndsWith(SingletonEventName, StringComparison.Ordinal))
                    return false;

                // Close in source — dup with DUPLICATE_CLOSE_SOURCE and discard.
                if (!DuplicateHandle(srcProcess, entry.HandleValue, GetCurrentProcess(),
                    out IntPtr closer, 0, false, DUPLICATE_CLOSE_SOURCE))
                {
                    App.Logger.WriteLine(LOG_IDENT, $"DuplicateHandle(close) failed for {objectName}");
                    return false;
                }

                CloseHandle(closer);
                App.Logger.WriteLine(LOG_IDENT, $"Closed {objectName}");
                return true;
            }
            finally
            {
                CloseHandle(dupHandle);
            }
        }

        private static List<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX> EnumerateHandles(HashSet<long> pids)
        {
            var result = new List<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();

            int length = 0x10000;
            IntPtr buffer = Marshal.AllocHGlobal(length);

            try
            {
                uint status;
                while (true)
                {
                    status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, length, out int returnLength);
                    if (status == STATUS_INFO_LENGTH_MISMATCH)
                    {
                        Marshal.FreeHGlobal(buffer);
                        length = Math.Max(length * 2, returnLength);
                        buffer = Marshal.AllocHGlobal(length);
                        continue;
                    }
                    break;
                }

                if (status != 0)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"NtQuerySystemInformation failed: 0x{status:X}");
                    return result;
                }

                long count = Marshal.ReadIntPtr(buffer).ToInt64();
                IntPtr arrayStart = IntPtr.Add(buffer, IntPtr.Size * 2);
                int entrySize = Marshal.SizeOf<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();

                for (long i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>(
                        IntPtr.Add(arrayStart, (int)(i * entrySize)));
                    if (pids.Contains(entry.UniqueProcessId.ToInt64()))
                        result.Add(entry);
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Enumerate", ex);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return result;
        }

        // NtQueryObject can block on specific handle types. Run on a timed thread and
        // bail if it doesn't answer quickly. In practice Event handles answer in microseconds.
        private static string? QueryObjectType(IntPtr handle) => QueryObjectWithTimeout(handle, ObjectTypeInformation);
        private static string? QueryObjectName(IntPtr handle) => QueryObjectWithTimeout(handle, ObjectNameInformation);

        private static string? QueryObjectWithTimeout(IntPtr handle, int infoClass)
        {
            string? result = null;
            var thread = new Thread(() =>
            {
                try { result = QueryObjectInner(handle, infoClass); }
                catch { /* swallow, result stays null */ }
            })
            { IsBackground = true };

            thread.Start();
            if (!thread.Join(TimeSpan.FromMilliseconds(300)))
            {
                // Thread hung on a slow handle — abandon it, it's a background thread.
                return null;
            }
            return result;
        }

        private static string? QueryObjectInner(IntPtr handle, int infoClass)
        {
            int length = 0x1000;
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                uint status = NtQueryObject(handle, infoClass, buffer, length, out int returnLength);
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    Marshal.FreeHGlobal(buffer);
                    length = Math.Max(returnLength, length * 2);
                    buffer = Marshal.AllocHGlobal(length);
                    status = NtQueryObject(handle, infoClass, buffer, length, out _);
                }
                if (status != 0)
                    return null;

                // Both ObjectType and ObjectName info start with a UNICODE_STRING at offset 0
                // (for ObjectName it's literally the struct; for ObjectType it's the TypeName field).
                var us = Marshal.PtrToStructure<UNICODE_STRING>(buffer);
                if (us.Buffer == IntPtr.Zero || us.Length == 0)
                    return null;

                return Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        #region P/Invoke

        private const uint PROCESS_DUP_HANDLE = 0x0040;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint DUPLICATE_CLOSE_SOURCE = 0x1;
        private const uint DUPLICATE_SAME_ACCESS = 0x2;
        private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
        private const int SystemExtendedHandleInformation = 64;
        private const int ObjectNameInformation = 1;
        private const int ObjectTypeInformation = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
        {
            public IntPtr Object;
            public IntPtr UniqueProcessId;
            public IntPtr HandleValue;
            public uint GrantedAccess;
            public ushort CreatorBackTraceIndex;
            public ushort ObjectTypeIndex;
            public uint HandleAttributes;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [DllImport("ntdll.dll")]
        private static extern uint NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

        [DllImport("ntdll.dll")]
        private static extern uint NtQueryObject(IntPtr handle, int infoClass, IntPtr buffer, int length, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
            out IntPtr targetHandle, uint desiredAccess, bool inheritHandle, uint options);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        #endregion
    }
}
