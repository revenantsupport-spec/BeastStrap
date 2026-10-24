namespace BeastStrap.Utility
{
    // What a section of a release note is about. Drives the badge and the coloured rail on the
    // Changelog page — None renders as a plain section header with no badge.
    public enum ChangelogCategory
    {
        None,
        New,
        Fixed,
        Changed,
        Note
    }

    // One rendered line. Bullets get a marker and an indent; everything else is a paragraph.
    public class ChangelogLine
    {
        public bool IsBullet { get; init; }

        public string Text { get; init; } = "";
    }

    // A heading plus the lines under it. Text appearing before a release's first heading lands in a
    // section with an empty Heading, so a lead-in paragraph isn't silently dropped.
    public class ChangelogSection
    {
        public ChangelogCategory Category { get; init; }

        public string Heading { get; init; } = "";

        public List<ChangelogLine> Lines { get; } = new();
    }

    public class ChangelogEntry
    {
        public string Version { get; init; } = "";

        public string Title { get; init; } = "";

        public DateTime Published { get; init; }

        public string Url { get; init; } = "";

        public bool IsPrerelease { get; init; }

        public List<ChangelogSection> Sections { get; } = new();
    }

    // Reads BeastStrap's own releases off the forge and turns each release body into sections the
    // Changelog page can colour-code.
    //
    // The bodies are hand-written Markdown, so this parses the subset they actually use — ATX
    // headings, "-" bullets, horizontal rules — and leaves inline formatting (bold, links, `code`)
    // to InlineMarkup at render time. WPF-UI's MarkdownTextBlock is not an option here: it discards
    // headings and lists outright, which is exactly the structure the colour-coding depends on.
    public static class ChangelogClient
    {
        private const string LOG_IDENT = "ChangelogClient";

        // Matches RobloxNewsClient, so navigating away and back doesn't refetch.
        public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

        // Nine releases exist today and each body is ~1-3 KB. 20 keeps the whole history in one
        // request without the page ever growing unbounded as releases pile up.
        private const int MaxReleases = 20;

        private static List<ChangelogEntry>? _cache;
        private static DateTime _cachedAt;

        public static DateTime FetchedAt => _cachedAt;

        public static string ReleasesUrl => $"{App.ProjectHost}/{App.ProjectRepository}/releases";

        public static async Task<List<ChangelogEntry>> GetAsync(bool forceRefresh = false, CancellationToken token = default)
        {
            if (!forceRefresh && _cache is not null && DateTime.Now - _cachedAt < CacheTtl)
                return _cache;

            string url = $"{App.ProjectApiBase}/repos/{App.ProjectRepository}/releases?limit={MaxReleases}";

            var releases = await Http.GetJson<List<GithubRelease>>(url, token) ?? new List<GithubRelease>();

            var entries = new List<ChangelogEntry>();

            foreach (var release in releases)
            {
                // Drafts are releases we haven't published yet — the upload flow creates one before
                // the exe is attached, so they'd otherwise show up as an empty "coming soon" card.
                if (release.Draft)
                    continue;

                entries.Add(Parse(release));
            }

            _cache = entries;
            _cachedAt = DateTime.Now;

            App.Logger.WriteLine(LOG_IDENT, $"Loaded {entries.Count} release(s)");

            return entries;
        }

        private static ChangelogEntry Parse(GithubRelease release)
        {
            string tag = string.IsNullOrWhiteSpace(release.TagName) ? release.Name ?? "" : release.TagName;

            var entry = new ChangelogEntry
            {
                Version = tag,
                Title = release.Name ?? "",
                Published = ParseDate(release.PublishedAt ?? release.CreatedAt),
                Url = string.IsNullOrWhiteSpace(release.HtmlUrl) ? ReleasesUrl : release.HtmlUrl!,
                IsPrerelease = release.Prerelease
            };

            entry.Sections.AddRange(ParseBody(release.Body ?? ""));

            return entry;
        }

        private static DateTime ParseDate(string? iso) =>
            DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToLocalTime()
                : DateTime.MinValue;

        // "---", "***" and "___" are separators in the source and would otherwise render as a stray
        // line of dashes.
        private static readonly Regex HorizontalRuleRegex = new(@"^(-{3,}|\*{3,}|_{3,})$", RegexOptions.Compiled);

        public static List<ChangelogSection> ParseBody(string body)
        {
            var sections = new List<ChangelogSection>();
            var current = new ChangelogSection();

            // The notes are hard-wrapped in the editor, and Markdown joins consecutive non-blank
            // lines into one block. So a line is buffered rather than emitted, and only flushed at a
            // blank line, a heading, a rule or the next bullet. Emitting per source line instead put
            // a paragraph break through the middle of every wrapped sentence.
            string pendingText = "";
            bool pendingIsBullet = false;

            void Flush()
            {
                if (pendingText.Length > 0)
                    current.Lines.Add(new ChangelogLine { IsBullet = pendingIsBullet, Text = pendingText });

                pendingText = "";
                pendingIsBullet = false;
            }

            foreach (string raw in body.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();

                if (line.Length == 0)
                {
                    Flush();
                    continue;
                }

                // A thematic break ends the section, not just the paragraph. Every rule in our notes
                // sits immediately before the "Official site / Source / VirusTotal" footer, which
                // would otherwise be swallowed by whichever heading came last and render under, say,
                // a green NEW rail as though the download links were a new feature.
                if (HorizontalRuleRegex.IsMatch(line))
                {
                    Flush();

                    if (current.Lines.Count > 0 || current.Heading.Length > 0)
                        sections.Add(current);

                    current = new ChangelogSection();
                    continue;
                }

                if (line.StartsWith('#'))
                {
                    string heading = line.TrimStart('#').Trim();

                    if (heading.Length == 0)
                        continue;

                    Flush();

                    if (current.Lines.Count > 0 || current.Heading.Length > 0)
                        sections.Add(current);

                    current = new ChangelogSection { Category = Classify(heading), Heading = heading };
                    continue;
                }

                bool bullet = line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("+ ");

                if (bullet)
                {
                    Flush();
                    line = line.Substring(2).Trim();
                }
                else if (line.StartsWith("> "))
                {
                    line = line.Substring(2).Trim();
                }

                if (line.Length == 0)
                    continue;

                // Anything that isn't a new bullet continues what came before it — Markdown's lazy
                // continuation, which is what keeps a wrapped bullet a single bullet.
                if (pendingText.Length > 0)
                {
                    pendingText += " " + line;
                }
                else
                {
                    pendingText = line;
                    pendingIsBullet = bullet;
                }
            }

            Flush();

            if (current.Lines.Count > 0 || current.Heading.Length > 0)
                sections.Add(current);

            return sections;
        }

        // Our headings are written as plain sentences ("The tray icon vanishing", "Your settings
        // could get wiped") far more often than as tidy labels, so matching only "New"/"Fixed" would
        // leave most sections uncoloured. These are deliberately *generic* signals rather than
        // phrases lifted from any one release, so future notes classify too — and anything with no
        // clear signal stays None, which renders as a neutral header instead of guessing wrong.
        //
        // Checked in order: Note wins first (an "Antivirus heads-up" is advice, not a change),
        // then New, then Fixed, then Changed.
        private static readonly string[] NoteWords =
            { "antivirus", "heads-up", "heads up", "warning", "disclaimer", "known issue", "important", "where to get", "still on", "privacy", "read this" };

        private static readonly string[] NewWords = { "new", "added", "adds", "introduc" };

        private static readonly string[] FixWords =
            { "fix", "broke", "broken", "wrong", "vanish", "no longer", "wiped", "crash", "could", "fail", "stopped", "again", "leaving" };

        private static readonly string[] ChangedWords = { "chang", "removed", "moved", "renamed" };

        public static ChangelogCategory Classify(string heading)
        {
            if (MatchesAny(heading, NoteWords))
                return ChangelogCategory.Note;

            if (MatchesAny(heading, NewWords))
                return ChangelogCategory.New;

            if (MatchesAny(heading, FixWords))
                return ChangelogCategory.Fixed;

            if (MatchesAny(heading, ChangedWords))
                return ChangelogCategory.Changed;

            return ChangelogCategory.None;
        }

        // Prefix-at-a-word-boundary, so "fix" catches fixed/fixes but not prefix/suffix, and "new"
        // doesn't fire on "renewed".
        private static bool MatchesAny(string text, string[] words) =>
            words.Any(w => Regex.IsMatch(text, @"\b" + Regex.Escape(w), RegexOptions.IgnoreCase));
    }
}
