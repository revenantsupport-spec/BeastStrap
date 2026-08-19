namespace BeastStrap.Utility
{
    // Who caused the crash we're about to explain to the user.
    public enum CrashFault
    {
        // BeastStrap did it. Something we ran pulled the ground out from under the client.
        Ours,

        // Something outside both BeastStrap and Roblox did it — a blocked overlay, the
        // firewall, the graphics driver, the machine running out of memory.
        Theirs,

        // We looked and couldn't tell.
        Unknown
    }

    /// <param name="SelfCleared">
    /// True only when we actually managed to read BeastStrap's own logs for the crash window
    /// and found nothing incriminating. This gates the "it wasn't us" line in the dialog — if we
    /// never looked, we don't get to claim innocence.
    /// </param>
    public sealed record CrashVerdict(CrashFault Fault, string RuleId, string Message, bool SelfCleared)
    {
        public static CrashVerdict Inconclusive(bool selfCleared) =>
            new(CrashFault.Unknown, "none", "", selfCleared);
    }

    /// <summary>
    /// One BeastStrap log file, already narrowed to the lines that fall inside the crash window.
    /// </summary>
    /// <param name="Name">Log file name, for the log line naming the rule that fired.</param>
    /// <param name="IsBackgroundUpdater">That session ran as `-backgroundupdater`.</param>
    /// <param name="Text">The in-window lines, joined.</param>
    public sealed record AppLogSession(string Name, bool IsBackgroundUpdater, string Text);

    // The rule engine, deliberately free of App, Paths, Logger and every other kind of I/O so it
    // can be pointed straight at a user's diagnostics bundle and checked. CrashAnalyzer does the
    // file reading and the logging and hands the results here.
    //
    // Two halves, and the order between them is the whole point of this class. Until 2026-07-27
    // there was only the second half: five regexes over Roblox's own client log, every one of
    // them ending in "This is not an BeastStrap issue." A user on v420.40 sent in a bundle
    // where BeastStrap had recursively deleted the Roblox folder out from under a running
    // client — the client's log shows it failing to load its own fonts 0.2s later — and we told
    // them to turn off their NVIDIA overlay. An analyzer that only ever reads the other guy's
    // logs can only ever blame the other guy.
    //
    // So OUR logs are checked first, and the innocence line is only allowed once that check has
    // actually run and come back clean.
    public static class CrashRules
    {
        private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        private sealed record OursRule(string Id, Func<AppLogSession, bool> Matches, string Message);

        #region Signatures of BeastStrap wrecking its own install

        // A recursive delete of a version directory. The exception frames catch the case where the
        // delete hit a file the running client had locked — which is the loud version, because
        // .NET's recursive delete removes everything it can reach before it rethrows, so by the
        // time it fails the content tree is already gone. The plain-text lines catch the quiet
        // version where the delete succeeded completely.
        // ⚠️ Match sites that delete the ACTIVE install, not routine housekeeping.
        //
        // Two alternatives were removed because they made this fire on healthy launches:
        //
        //   "at System\.IO\.Directory\.Delete" matched any logged stack frame anywhere. Logger dumps
        //   full stack traces, and CleanupVersionsFolder runs on EVERY launch, so a single
        //   undeletable leftover directory (locked file, antivirus, a stale orphan) made this rule
        //   match forever, on every single launch, and blame us for a crash that never happened.
        //
        //   "Deleted orphan" is logged on the SUCCESS path of that same routine cleanup. Pruning an
        //   unreferenced directory is exactly what it is supposed to do, and it is not evidence of
        //   anything going wrong.
        //
        // What is left names the paths that genuinely clear a live install.
        private static readonly Regex DeletedInstall = new(
            @"RemoveDirectoryRecursive"
            + @"|Bootstrapper::ClearJunctionTargetContents"
            + @"|Failed to clear the latest version directory"
            + @"|removed redundant real dir"
            + @"|Could not fully clean up installation",
            Opts);

        // Package extraction blowing up on a file another process holds open, i.e. we were writing
        // a Roblox update into a folder a client was already running from.
        private static readonly Regex ExtractionCollided = new(
            @"Bootstrapper\.ExtractPackage"
            + @"|FastZipEvents\.OnFileFailure"
            + @"|ExtractFileEntry",
            Opts);

        // The background updater merely *extracting* during the window is enough on its own — it
        // skips the shutdown step by design and writes straight into the live install.
        private static readonly Regex WasExtracting = new(@"\[Bootstrapper::ExtractPackage\] Extracting ", Opts);

        // We shut Roblox down ourselves to run an upgrade. KillRobloxInstances is unfiltered, so
        // "an upgrade" anywhere means "every client on the machine", including ones this launch
        // has nothing to do with.
        //
        // ⚠️ This must only match PROOF THAT WE KILLED SOMETHING. It used to also match
        // "SetStatus] Shutting down", which is the UI status label written at the top of
        // UpgradeRoblox BEFORE the Studio/Player branch and before anything is touched — it fires
        // even when zero processes are running. The result was that every routine update told the
        // user "This wasn't a crash — it was us... we shut down every running client", when in fact
        // nothing had been killed and their client had exited perfectly normally. Do not put status
        // strings back in here; match the line KillRobloxInstances writes after a real kill.
        private static readonly Regex KilledClients = new(
            @"\[Bootstrapper::KillRobloxInstances\] Killed \d+ Roblox process",
            Opts);

        private static readonly Regex CancelledLaunch = new(@"\[Bootstrapper::Cancel\] Cancelling launch", Opts);
        private static readonly Regex WasInstalling = new(@"SetStatus\] (Upgrading|Installing)", Opts);

        // Park-and-rename backed off, or the layout threw. The install may be half-shuffled.
        //
        // Three of the four alternatives here used to be dead: the log line says "Couldn't MOVE",
        // not "park"; ParkCurrentOccupant was renamed to ParkRecordedOccupant; and junctions are
        // long gone. So the only live one was ::EnsureActive, which fires solely when EnsureActive
        // throws — never on the ordinary back-off this rule is named after. Matched against the
        // messages VersionProfileLayout actually writes now.
        private static readonly Regex LayoutBackedOff = new(
            @"Couldn't move .*it's in use"
            + @"|VersionProfileLayout::EnsureActive"
            + @"|VersionProfileLayout::TryMoveAside",
            Opts);

        // The client on disk isn't the build we thought was installed, so it may have started with
        // an exe and a content tree from two different Roblox versions.
        private static readonly Regex MixedBuild = new(
            @"\[Bootstrapper::InstalledExeMatchesLatest\].*: differs", Opts);

        #endregion

        // Ordered most-definitive first, same convention as the third-party table below.
        private static readonly OursRule[] OurRules =
        {
            new("ours/install-deleted",
                s => DeletedInstall.IsMatch(s.Text),
                "**This one was on us.** While your game was running, BeastStrap was clearing out the "
                + "Roblox folder it was running from, so Roblox lost files it still needed and died. "
                + "Nothing is wrong with your PC. Make sure you're on the latest version of BeastStrap — "
                + "this doesn't happen any more."),

            new("ours/install-overwritten",
                s => ExtractionCollided.IsMatch(s.Text) || (s.IsBackgroundUpdater && WasExtracting.IsMatch(s.Text)),
                "**This one was on us.** BeastStrap was writing a Roblox update into the same folder your "
                + "game was already running from, which corrupts the copy that's running. Nothing is wrong "
                + "with your PC. Make sure you're on the latest version of BeastStrap."),

            new("ours/client-killed",
                s => KilledClients.IsMatch(s.Text),
                "**This wasn't a crash — it was us.** Another BeastStrap launch decided Roblox needed "
                + "updating and shut down every running client to do it, including this one. If you use "
                + "Multi Instance, let one launch finish updating before starting the next."),

            new("ours/install-wiped-on-cancel",
                s => CancelledLaunch.IsMatch(s.Text) && WasInstalling.IsMatch(s.Text),
                "**This one was on us.** A launch was cancelled part way through an update, and BeastStrap "
                + "deleted the Roblox folder while your game was still using it."),

            new("ours/layout-backoff",
                s => LayoutBackedOff.IsMatch(s.Text),
                "**This may have been us.** BeastStrap tried to move Roblox's install folder around while a "
                + "client was still running out of it, and had to give up part way. If you're switching "
                + "version profiles, close every Roblox window first, then launch."),

            new("ours/mixed-build",
                s => MixedBuild.IsMatch(s.Text),
                "**This may have been us.** The Roblox client on disk didn't match the build BeastStrap "
                + "thought was installed, so the game may have started with files from two different "
                + "versions. Launch again and BeastStrap will lay down a clean copy."),
        };

        #region Turning a raw log file into a windowed session

        private static readonly Regex BackgroundUpdaterSession = new(
            @"Opening background updater|LaunchHandler::LaunchBackgroundUpdater|backgroundupdater",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>That session ran as `-backgroundupdater`. Decide this from the WHOLE file — the
        /// line that says so is logged during startup and is usually outside the crash window.</summary>
        public static bool IsBackgroundUpdaterLog(string content) => BackgroundUpdaterSession.IsMatch(content);

        private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";

        /// <summary>
        /// Keeps only the lines logged between <paramref name="start"/> and <paramref name="end"/>.
        /// </summary>
        /// <remarks>
        /// Log lines look like "2026-07-27T14:30:52Z [Ident] message". Exception dumps continue onto
        /// untimestamped lines ("   at System.IO..."), which belong to whichever timestamped line
        /// came before them — so they inherit it rather than being dropped. Without that, every
        /// stack trace (the most useful evidence we have) would fall straight out of the window.
        /// </remarks>
        public static string NarrowToWindow(string content, DateTime start, DateTime end)
        {
            var kept = new StringBuilder();
            bool sawTimestamp = false;
            bool keeping = false;

            foreach (string line in content.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');

                if (TryParseTimestamp(trimmed, out var stamp))
                {
                    sawTimestamp = true;
                    keeping = stamp >= start && stamp <= end;
                }
                else if (!sawTimestamp)
                {
                    // Leading fragment from a tail read, or a pre-timestamp line. No idea when it
                    // happened, so leave it out.
                    continue;
                }

                if (keeping)
                    kept.Append(trimmed).Append('\n');
            }

            return kept.ToString();
        }

        public static bool TryParseTimestamp(string line, out DateTime stamp)
        {
            stamp = default;

            if (line.Length < TimestampFormat.Length || line[TimestampFormat.Length - 1] != 'Z')
                return false;

            return DateTime.TryParseExact(
                line.Substring(0, TimestampFormat.Length),
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out stamp);
        }

        #endregion

        /// <summary>
        /// Decides who to blame. <paramref name="ourSessions"/> must already be narrowed to the
        /// crash window — this method does no time filtering of its own.
        /// </summary>
        public static CrashVerdict Evaluate(IReadOnlyList<AppLogSession> ourSessions, IReadOnlyList<string> robloxLogs)
        {
            // We only get to say "not our fault" if we actually managed to read our own logs.
            bool selfChecked = ourSessions.Count > 0;

            foreach (var rule in OurRules)
            {
                foreach (var session in ourSessions)
                {
                    if (rule.Matches(session))
                        return new CrashVerdict(CrashFault.Ours, rule.Id, rule.Message, false);
                }
            }

            foreach (string log in robloxLogs)
            {
                var theirs = ClassifyRobloxLog(log);
                if (theirs is not null)
                    return theirs with { SelfCleared = selfChecked };
            }

            return CrashVerdict.Inconclusive(selfChecked);
        }

        // Third-party and environmental causes, read out of Roblox's own client log. First match
        // wins, ordered most-definitive first. These messages deliberately stop at "here's the
        // cause and here's the fix" — the "it wasn't BeastStrap" line is added by the caller,
        // and only when the self-check above actually cleared us.
        private static CrashVerdict? ClassifyRobloxLog(string log)
        {
            // Graphics driver lost the device — a hard, unambiguous crash.
            if (Regex.IsMatch(log, @"DXGI_ERROR_DEVICE_REMOVED|DXGI_ERROR_DEVICE_HUNG|GfxCrash|graphics device (removed|lost)|D3D.{0,20}device (removed|lost)", RegexOptions.IgnoreCase))
                return new(CrashFault.Theirs, "theirs/gpu-device-lost",
                    "This looks like a **graphics driver** problem — Roblox lost contact with your GPU. "
                    + "Update your graphics driver (or roll it back if you just updated it), and try lowering "
                    + "Roblox's graphics quality.", false);

            // Ran out of memory.
            if (Regex.IsMatch(log, @"bad_alloc|out of memory|OutOfMemory|Not enough memory", RegexOptions.IgnoreCase))
                return new(CrashFault.Theirs, "theirs/out-of-memory",
                    "Roblox ran **out of memory**. Close other apps (browsers especially) and try again. "
                    + "If you're running several clients at once, try running fewer.", false);

            // Firewall / antivirus / VPN deliberately blocking the connection: Winsock 10013 is
            // ACCESS DENIED, a security product refusing the socket. Ranked above the overlay
            // block below because that block is usually benign, while access-denied is a real,
            // deliberate block that breaks connectivity.
            if (Regex.IsMatch(log, @"OS_ERRNO:\s*10013|WSAEACCES|errno[:=\s]+10013", RegexOptions.IgnoreCase))
                return new(CrashFault.Theirs, "theirs/socket-access-denied",
                    "Your **firewall, antivirus, or VPN** looks like it is blocking Roblox from connecting "
                    + "(Windows error 10013, access denied). Allow Roblox through your firewall and antivirus, "
                    + "or turn off your VPN, then try again.", false);

            // Roblox's anti-cheat blocked a third-party overlay/capture tool from hooking the game.
            var blocked = Regex.Match(log, @"Blocked DLL:.*?([^\\/]+\.dll)", RegexOptions.IgnoreCase);
            if (blocked.Success)
            {
                string dll = blocked.Groups[1].Value.ToLowerInvariant();
                string tool =
                    dll.Contains("nvspcap") || dll.Contains("nvcamera") || dll.StartsWith("nvgx") || dll.Contains("nvidia") ? "the NVIDIA GeForce Experience / ShadowPlay overlay"
                    : dll.Contains("rtss") ? "the RivaTuner / MSI Afterburner overlay"
                    : dll.Contains("discord") ? "the Discord in-game overlay"
                    : dll.Contains("gameoverlayrenderer") ? "the Steam overlay"
                    : dll.Contains("graphics-hook") ? "OBS (game capture)"
                    : dll.Contains("fraps") ? "Fraps"
                    : "a third-party overlay or screen-capture tool";
                return new(CrashFault.Theirs, "theirs/blocked-overlay",
                    $"Roblox's anti-cheat blocked **{tool}** from hooking into the game, which can crash it. "
                    + "Turn that overlay or capture tool off for Roblox and try again.", false);
            }

            // Weaker signal, last: a run of connection failures straight to Roblox with no explicit
            // access-denied. Generic connectivity trouble rather than a named blocker.
            if (Regex.Matches(log, @"Failed to connect to \S*roblox\.com", RegexOptions.IgnoreCase).Count >= 3)
                return new(CrashFault.Theirs, "theirs/no-connection",
                    "Roblox couldn't reach its servers — this looks like a **network or connection** problem "
                    + "on your side (Wi-Fi, VPN, or an unstable connection). Check your connection and try again.", false);

            return null;
        }
    }
}
