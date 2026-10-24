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

    // There used to be a SelfCleared flag here, which the dialog turned into "We checked
    // BeastStrap's own logs for this launch and found nothing wrong on our end". It was computed
    // as `ourSessions.Count > 0` — literally "we found at least one log file". It never checked
    // whether anything was wrong, and since the watcher's own log always exists it was
    // unconditionally true. Removed rather than fixed: the dialog now stays silent when it has no
    // verdict, so there is nothing left to reassure anyone about.
    /// <param name="Certain">
    /// False when the evidence shows we were doing something risky nearby, but not that we actually
    /// killed the client. The dialog softens its opening line for these. Real uncertainty stated
    /// plainly is honest; what is not allowed is a confident headline over evidence that cannot
    /// carry it.
    /// </param>
    public sealed record CrashVerdict(CrashFault Fault, string RuleId, string Message, bool Certain = true)
    {
        public static CrashVerdict Inconclusive() =>
            new(CrashFault.Unknown, "none", "");
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

        private sealed record OursRule(string Id, Func<AppLogSession, bool> Matches, string Message, bool Certain = true);

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

        // (MixedBuild regex removed along with ours/mixed-build — see the note in the rule table.)

        #endregion

        // Ordered most-definitive first, same convention as the third-party table below.
        private static readonly OursRule[] OurRules =
        {
            // COPY RULES for everything below. These strings go straight into a dialog the user has
            // to dismiss, so: state the cause in one sentence, give one thing they can do, stop.
            // No bold lead-ins (MarkdownTextBlock truncates nested inlines anyway), no em dashes,
            // no "it looks like" / "this may have been", and no reassurance nobody asked for.
            // Never claim something is fixed unless it actually is — several of these causes are
            // still open, and telling someone to update when that would not help is just noise.
            // ⚠️ Do NOT add "this is fixed, just update" here. UpgradeRoblox now refuses to clear the
            // version directory when a client is still running out of it, but Cancel() still does the
            // same unguarded recursive delete over that same folder, so ours/install-deleted and
            // ours/install-wiped-on-cancel below both stay exactly as they are. Telling someone on
            // the newest build that the newest build fixes it is worse than saying nothing.
            new("ours/install-deleted",
                s => DeletedInstall.IsMatch(s.Text),
                "BeastStrap was clearing out the Roblox folder while your game was running, so Roblox "
                + "lost files it needed.\n\nClose Roblox before starting another launch that updates it."),

            new("ours/install-overwritten",
                s => ExtractionCollided.IsMatch(s.Text) || (s.IsBackgroundUpdater && WasExtracting.IsMatch(s.Text)),
                "BeastStrap wrote a Roblox update into the folder your game was already running from, "
                + "which corrupts the running copy.\n\nClose Roblox before letting an update run."),

            new("ours/client-killed",
                s => KilledClients.IsMatch(s.Text),
                "Another launch needed to update Roblox and closed every running client to do it, "
                + "including this one.\n\nIf you use Multi Instance, let one launch finish updating "
                + "before you start the next."),

            new("ours/install-wiped-on-cancel",
                s => CancelledLaunch.IsMatch(s.Text) && WasInstalling.IsMatch(s.Text),
                "A launch was cancelled part way through an update, and BeastStrap deleted the Roblox "
                + "folder while your game was still using it.\n\nLaunch again and BeastStrap will put "
                + "back a clean copy."),

            // Certain: false — backing off from a move is evidence we were busy near this client's
            // install, not evidence we closed it. The dialog softens its opening line to match.
            new("ours/layout-backoff",
                s => LayoutBackedOff.IsMatch(s.Text),
                "BeastStrap tried to move Roblox's install folder while a client was still running from "
                + "it, and gave up part way.\n\nClose every Roblox window before switching version "
                + "profiles.",
                Certain: false),

            // ours/mixed-build DELETED.
            //
            // It matched "[Bootstrapper::InstalledExeMatchesLatest] ...: differs", which is written on
            // an ORDINARY launch every time the client on disk is older than latest — i.e. the first
            // launch after every Roblox version bump, before the upgrade lands. Roblox updates, the
            // user launches, "differs" is logged, the upgrade succeeds, and if that client then dies
            // of something unrelated the line is still inside the crash window and we accused
            // ourselves off the back of routine upgrade bookkeeping.
            //
            // Same defect class as the deleted theirs/blocked-overlay rule: it fires on healthy
            // launches. The audit question applies to OUR rules too, not just Roblox's logs.
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

        // Roblox's client log uses the same prefix plus milliseconds, then a comma:
        // "2026-08-19T10:08:59.180Z,0.180925,badc,6,Warning [FLog::...]"
        private const string RobloxTimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

        /// <summary>
        /// Reads the leading UTC timestamp off a log line, in either BeastStrap's or Roblox's
        /// format.
        /// </summary>
        /// <remarks>
        /// Handling BOTH formats is what lets Roblox's logs be narrowed to the crash window the same
        /// way ours are. They never used to be: the whole 1 MB tail went to the rules, so a line
        /// from the first minute of a six-hour session could "explain" a death at the end of it, and
        /// a healthy concurrent client's log could supply the verdict for a different client's
        /// crash. Every third-party rule was unreliable for that reason alone.
        /// </remarks>
        public static bool TryParseTimestamp(string line, out DateTime stamp)
        {
            stamp = default;

            if (line.Length >= TimestampFormat.Length
                && line[TimestampFormat.Length - 1] == 'Z'
                && DateTime.TryParseExact(
                    line.Substring(0, TimestampFormat.Length),
                    TimestampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out stamp))
                return true;

            if (line.Length >= RobloxTimestampFormat.Length
                && line[RobloxTimestampFormat.Length - 1] == 'Z'
                && DateTime.TryParseExact(
                    line.Substring(0, RobloxTimestampFormat.Length),
                    RobloxTimestampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out stamp))
                return true;

            return false;
        }

        #endregion

        /// <summary>
        /// Decides who to blame. <paramref name="ourSessions"/> must already be narrowed to the
        /// crash window — this method does no time filtering of its own.
        /// </summary>
        public static CrashVerdict Evaluate(IReadOnlyList<AppLogSession> ourSessions, IReadOnlyList<string> robloxLogs)
        {
            foreach (var rule in OurRules)
            {
                foreach (var session in ourSessions)
                {
                    if (rule.Matches(session))
                        return new CrashVerdict(CrashFault.Ours, rule.Id, rule.Message, rule.Certain);
                }
            }

            foreach (string log in robloxLogs)
            {
                var theirs = ClassifyRobloxLog(log);
                if (theirs is not null)
                    return theirs;
            }

            // Unknown is a perfectly good answer and the caller shows nothing for it. Guessing here
            // is how we ended up telling people to switch off their graphics overlay.
            return CrashVerdict.Inconclusive();
        }

        // Third-party and environmental causes, read out of Roblox's own client log. First match
        // wins, ordered most-definitive first.
        //
        // ⚠️ EVERY rule here must survive one question: DOES THIS STRING ALSO APPEAR IN A SESSION
        // THAT DID NOT CRASH? Two rules failed that badly enough to be deleted outright:
        //
        //   theirs/blocked-overlay matched "Blocked DLL: <name>" and told the user to switch off
        //   their NVIDIA overlay. Checked against 15 real client logs: the only 2 containing it
        //   were HEALTHY sessions of 251s and 109s that both shut down gracefully. Roblox's
        //   anti-cheat refusing to let an overlay inject is routine designed behaviour, not a
        //   fault. It even scraped "robloxcrashhandler.exe" — Roblox's own crash handler, and not
        //   a DLL — and called it an overlay.
        //
        //   theirs/out-of-memory matched "out of memory" / "OutOfMemory" case-insensitively, which
        //   hits component names, telemetry keys and crashpad annotations in perfectly healthy
        //   logs. It never meant the process died.
        //
        // Do not add a rule here that fires on a single substring. If it cannot carry a repeat
        // count or some other corroboration, it is not evidence and it does not belong.
        //
        // Note the caller narrows this log to the crash window first, so a match is also recent —
        // that is what stops a line from hour one of a long session explaining a death at hour six.
        private static CrashVerdict? ClassifyRobloxLog(string log)
        {
            // Graphics driver lost the device. Only the two hard DXGI codes, which mean the renderer
            // is gone — the looser prose forms ("graphics device lost", "D3D ... device removed") and
            // the bare token "GfxCrash" are out, because they match lines that merely NAME the
            // failure or the crash-reporting component.
            //
            // Deliberately a single occurrence. A repeat count reads as prudent here but is not: a
            // session that survives a TDR never reaches this analyzer at all, so requiring two would
            // only ever suppress the fatal single-occurrence case it is supposed to catch. The
            // corroboration is structural instead — the caller narrowed this to the dead client's
            // own window, and the gate above already established it died before reaching gameplay.
            if (Regex.IsMatch(log, @"DXGI_ERROR_DEVICE_REMOVED|DXGI_ERROR_DEVICE_HUNG", RegexOptions.IgnoreCase))
                return new(CrashFault.Theirs, "theirs/gpu-device-lost",
                    "Roblox lost contact with your graphics card.\n\nUpdate your graphics driver, or roll "
                    + "it back if you updated it recently.");

            // Winsock 10013 is ACCESS DENIED — a security product deliberately refusing the socket.
            // \b so 10013 can't match inside 100134, and repeats required because a single refused
            // endpoint (telemetry, a CDN host) is retried and recovered without the user noticing.
            if (Regex.Matches(log, @"OS_ERRNO:\s*10013\b|WSAEACCES|errno[:=\s]+10013\b", RegexOptions.IgnoreCase).Count >= 2)
                return new(CrashFault.Theirs, "theirs/socket-access-denied",
                    "Something on your PC is blocking Roblox from connecting. That is usually a firewall, "
                    + "antivirus or VPN.\n\nAllow Roblox through, or turn the VPN off, then try again.");

            // A run of connection failures. This was always the best-evidenced rule here — it is the
            // only one that ever required corroboration.
            if (Regex.Matches(log, @"Failed to connect to \S*roblox\.com", RegexOptions.IgnoreCase).Count >= 3)
                return new(CrashFault.Theirs, "theirs/no-connection",
                    "Roblox could not reach its servers.\n\nCheck your connection and try again.");

            return null;
        }
    }
}
