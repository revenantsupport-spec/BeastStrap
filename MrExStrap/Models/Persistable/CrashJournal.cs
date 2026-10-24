namespace BeastStrap.Models.Persistable
{
    /// <summary>
    /// Crashes and background failures BeastStrap noticed. Local only, never transmitted.
    /// </summary>
    /// <remarks>
    /// ⚠️ Deliberately its OWN file rather than a few more properties on State.
    ///
    /// State is whole-written by the settings UI — MainWindow saves it on close, and starring a
    /// script saves it immediately — and those writes serialise the snapshot that process loaded at
    /// startup. A watcher process recording a crash while the settings window happened to be open
    /// would have its record erased the moment the user closed that window. That loses precisely the
    /// evidence this feature exists to keep, and it loses it silently.
    ///
    /// Only background writers append here, and only the Home notice reads it, so nothing ever
    /// whole-writes this file and SaveMerged from the watcher is safe.
    /// </remarks>
    public class CrashJournal
    {
        private const int MaxEntries = 10;

        /// <summary>Roblox crashes.</summary>
        public List<CrashRecord> RecentCrashes { get; set; } = new();

        /// <summary>BeastStrap's own failures — unhandled exceptions and faulted background tasks.</summary>
        /// <remarks>
        /// Kept apart from <see cref="RecentCrashes"/> on purpose. Sharing one capped list let a
        /// process with a recurring faulted task evict the user's real crash records, and made the
        /// Home notice describe our own bugs as Roblox crashes.
        /// </remarks>
        public List<CrashRecord> RecentBackgroundFailures { get; set; } = new();

        /// <summary>
        /// UTC time the user last dismissed or acted on the notice. Entries older than this stop
        /// counting towards it, so dismissing is sticky without deleting the history.
        /// </summary>
        public DateTime? CrashNoticeAcknowledgedUtc { get; set; }

        public void PushCrash(CrashRecord record) => Push(RecentCrashes ??= new(), record);

        public void PushBackgroundFailure(CrashRecord record) => Push(RecentBackgroundFailures ??= new(), record);

        // Newest first, then trim the tail. Deliberately no dedupe: two crashes of the same shape an
        // hour apart are two data points, and "how often" is the question we can't answer today.
        private static void Push(List<CrashRecord> list, CrashRecord record)
        {
            if (record is null)
                return;

            list.Insert(0, record);

            while (list.Count > MaxEntries)
                list.RemoveAt(list.Count - 1);
        }
    }
}
