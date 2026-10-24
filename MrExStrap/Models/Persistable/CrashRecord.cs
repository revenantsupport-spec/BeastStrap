using System.Text.Json.Serialization;

namespace BeastStrap.Models.Persistable
{
    /// <summary>
    /// One crash BeastStrap noticed. Stored locally in State, never transmitted anywhere.
    /// </summary>
    /// <remarks>
    /// Deliberately small and boring. This exists so the settings page can say "we saw N of these"
    /// and offer an export, which is the only thing that was lost when the crash dialog stopped
    /// guessing at causes. Everything genuinely diagnostic lives in the preserved log folder this
    /// points at, not in here.
    /// </remarks>
    public class CrashRecord
    {
        public DateTime TimeUtc { get; set; } = DateTime.UtcNow;

        /// <summary>BeastStrap version, so an old entry is obviously an old entry after an update.</summary>
        public string AppVersion { get; set; } = "";

        /// <summary>Roblox client version, taken from the client log file name.</summary>
        public string RobloxVersion { get; set; } = "";

        /// <summary>
        /// The verdict rule id, or "none" when nothing matched. Background failures use their own
        /// ids ("unhandled-exception", "unobserved-task") since no crash rule ran for them.
        /// </summary>
        public string RuleId { get; set; } = "none";

        /// <summary>
        /// True when the user was shown nothing at all for this.
        /// </summary>
        /// <remarks>
        /// This, not the rule id, is what the notice counts. It covers both populations that are
        /// otherwise invisible: crashes where no rule matched so the dialog stayed quiet, and
        /// background-thread failures that never had a dialog in the first place. A crash that DID
        /// show a dialog needs no further nagging — the user already had their chance to export.
        /// </remarks>
        public bool Silent { get; set; } = true;

        /// <summary>Client exit code if we managed to read it, else null. 0 never lands here.</summary>
        public int? ExitCode { get; set; }

        public int SessionSeconds { get; set; }

        /// <summary>
        /// Folder under Logs\Preserved holding the copies taken at crash time, or empty if the copy
        /// failed. Relative, so moving the install doesn't strand it.
        /// </summary>
        public string PreservedFolder { get; set; } = "";

        [JsonIgnore]
        public bool Unexplained => string.IsNullOrEmpty(RuleId) || RuleId == "none";
    }
}
