namespace BeastStrap.Models
{
    public enum CreatorKind
    {
        YouTuber,
        ScriptSite,
        Seasonal,
    }

    // When a seasonal theme takes over by itself (see ThemeManager.ActiveSeasonal): the same run of days every
    // year, by the local date. Plus what the Appearance page says about it.
    public sealed class Season
    {
        // The first day, which has to exist every year (so never Feb 29), and how many days the run lasts.
        public int Month { get; }
        public int Day { get; }
        public int Days { get; }

        // The Appearance notice while the run is on: "Merry Christmas!", then what's happening.
        public string Greeting { get; }
        public string Notice { get; }

        // The Appearance page's yearly on/off switch for it.
        public string SwitchHeader { get; }
        public string SwitchDescription { get; }

        public Season(int month, int day, int days, string greeting, string notice, string switchHeader, string switchDescription)
        {
            Month = month;
            Day = day;
            Days = days;
            Greeting = greeting;
            Notice = notice;
            SwitchHeader = switchHeader;
            SwitchDescription = switchDescription;
        }

        // The year of the run that `date` falls in, or null when it's outside the run. It's the year the run
        // STARTED, so a run over New Year still counts as one: that's what "turned off for this year" is kept
        // against (Settings.SeasonalThemesSkipped).
        public int? RunYear(DateTime date)
        {
            date = date.Date;

            for (int year = date.Year; year >= date.Year - 1; year--)
            {
                var start = new DateTime(year, Month, Day);
                if (date >= start && date < start.AddDays(Days))
                    return year;
            }

            return null;
        }
    }

    // A full re-skin of the app for a YouTuber or a script site: their colours, their avatar or logo as the
    // app icon (taskbar, title bars, launcher, tray, bootstrapper), an "BeastStrap × Name" lockup in place
    // of the wordmark, and a hero on the Home page built from their banner.
    //
    // The art (avatar, .ico, banner) is embedded from Resources/Creators at build time, not fetched at
    // runtime: the app promises nothing is sent anywhere just by opening a page. YouTuber palettes were
    // sampled from each channel's own avatar and banner (2026-09-30), then brightened where needed so
    // accents stay readable on the dark UI.
    public sealed class CreatorTheme
    {
        public string Id { get; }
        public CreatorKind Kind { get; }
        public string Name { get; }

        // "@handle" for a YouTuber, the bare domain for a site.
        public string Handle { get; }

        // Their channel or their site, or null for a theme that deliberately carries no link: the owner only
        // wants a "Visit site" button for their own sites and V3rmillion, not for competing script hubs.
        public string? Url { get; }
        public bool HasLink => Url is not null;

        public string Tagline { get; }
        public ThemePalette Palette { get; }

        // Set for a seasonal theme only: the days it takes over by itself.
        public Season? Season { get; }

        public string LinkText => Kind == CreatorKind.YouTuber ? "YouTube channel" : "Visit site";

        public Wpf.Ui.Common.SymbolRegular LinkIcon =>
            Kind == CreatorKind.YouTuber ? Wpf.Ui.Common.SymbolRegular.PlayCircle24 : Wpf.Ui.Common.SymbolRegular.Globe24;

        public string AvatarUri => $"pack://application:,,,/Resources/Creators/{Id}-avatar.png";
        public string BannerUri => $"pack://application:,,,/Resources/Creators/{Id}-banner.jpg";
        public string IconUri => $"pack://application:,,,/Resources/Creators/{Id}.ico";

        // The Appearance card shows the banner's left part with the avatar on top of it. Banners with their
        // own emblem there (ZeroDetections' Guy Fawkes, under a Guy Fawkes avatar) ship a card copy without it.
        private readonly bool _cardArt;
        public string CardBannerUri => _cardArt ? $"pack://application:,,,/Resources/Creators/{Id}-card.jpg" : BannerUri;

        private CreatorTheme(string id, CreatorKind kind, string name, string handle, string? url, string tagline, ThemePalette palette, bool cardArt = false, Season? season = null)
        {
            Id = id;
            Kind = kind;
            Name = name;
            Handle = handle;
            Url = url;
            Tagline = tagline;
            Palette = palette;
            _cardArt = cardArt;
            Season = season;
        }

        private static CreatorTheme YouTuber(string id, string name, string handle, string tagline, ThemePalette palette, bool cardArt = false) =>
            new(id, CreatorKind.YouTuber, name, handle, $"https://www.youtube.com/{handle}", tagline, palette, cardArt);

        private static CreatorTheme Site(string id, string name, string domain, string tagline, ThemePalette palette, bool link = true) =>
            new(id, CreatorKind.ScriptSite, name, domain, link ? $"https://{domain}/" : null, tagline, palette);

        private static CreatorTheme Seasonal(string id, string name, string when, string tagline, ThemePalette palette, Season season) =>
            new(id, CreatorKind.Seasonal, name, when, null, tagline, palette, season: season);

        public static readonly IReadOnlyList<CreatorTheme> All = new List<CreatorTheme>
        {
            // Guy Fawkes in a top hat on a crimson disc, navy suit, charcoal striped banner. Crimson runs
            // into a hot coral for the gradients, and the suit's navy drives the aurora.
            YouTuber("zerodetections", "ZeroDetections", "@ZeroDetections", "In the end, we're all detected.",
                new ThemePalette
                {
                    Accent = "#E63946", GradientStart = "#E63946", GradientEnd = "#FF7A59", Purple = "#3A5BC7",
                    Background = "#09090C", Surface = "#141319", Hairline = "#2A232B", Glow = "#E63946",
                }, cardArt: true),

            // A liquid magenta-and-violet swirl with a white script logo on deep blue.
            YouTuber("vaehz", "vaehz", "@vaehz", "Your #1 skid.",
                new ThemePalette
                {
                    Accent = "#FF2BD6", GradientStart = "#FF2BD6", GradientEnd = "#8B3DFF", Purple = "#2F4BFF",
                    Background = "#08061A", Surface = "#130E2A", Hairline = "#2A1F4A", Glow = "#FF2BD6",
                }),

            // A cursed Mario 64 face: red cap, N64 green backdrop, the gold star in the HUD, and a
            // banner of navy-and-red gaming chairs. Red to star-gold, with the green in the aurora.
            YouTuber("7granddad", "7GrandDad", "@7GrandDadVape", "developer for lego game",
                new ThemePalette
                {
                    Accent = "#E83A32", GradientStart = "#E83A32", GradientEnd = "#FFC83D", Purple = "#36D418",
                    Background = "#0A0D16", Surface = "#141A28", Hairline = "#262E42", Glow = "#E83A32",
                }),

            // Script sites. Palettes come from each site's own CSS tokens, checked against pixels sampled
            // from a live screenshot (2026-09-30); logos and art are the sites' own.

            // Amber-500 into orange-500 (the site's own button gradients) on its navy, with the sticker
            // logo's magenta as the third colour. The avatar is that sticker logo, not the site's favicon.
            Site("robloxscripts", "Roblox Scripts", "robloxscripts.com", "Free Scripts & Exploits for Roblox Games",
                new ThemePalette
                {
                    Accent = "#F59E0B", GradientStart = "#F59E0B", GradientEnd = "#F97316", Purple = "#FC02F7",
                    Background = "#0A0F19", Surface = "#111827", Hairline = "#1F2937", Glow = "#F59E0B",
                }),

            // Violet-400 into indigo-400 (the site's own heading gradient) on slate navy.
            Site("scriptblox", "ScriptBlox", "scriptblox.com", "Roblox Scripts & Live Executors",
                new ThemePalette
                {
                    Accent = "#A684FF", GradientStart = "#A684FF", GradientEnd = "#7C86FF", Purple = "#8E51FF",
                    Background = "#090D19", Surface = "#0F172B", Hairline = "#1D293D", Glow = "#A684FF",
                }, link: false),

            // Red-500 on cool charcoal, with the flame logo's fire orange and gold as the second and third.
            Site("haxhell", "HaxHell", "haxhell.com", "The Best Roblox Script Sharing Platform!",
                new ThemePalette
                {
                    Accent = "#EF4444", GradientStart = "#EF4444", GradientEnd = "#FB7829", Purple = "#FBBF24",
                    Background = "#0A0A0E", Surface = "#16161A", Hairline = "#2A2A32", Glow = "#EF4444",
                }, link: false),

            // The site's button blue (its lighter edge tint, so it reads on dark) into the periwinkle of its own
            // "Tap. Copy. Play." art, with the indigo of its aurora, on its cool Vercel-style charcoal.
            Site("rscripts", "Rscripts", "rscripts.net", "Roblox Scripts & Executors",
                new ThemePalette
                {
                    Accent = "#4F7EFB", GradientStart = "#4F7EFB", GradientEnd = "#92A5FD", Purple = "#5C54F2",
                    Background = "#0F0F12", Surface = "#1B1B1E", Hairline = "#2B2B2E", Glow = "#4F7EFB",
                }, link: false),

            // The neon wordmark's hot magenta and spring green, with the site's violet glow, on violet-black.
            Site("sirmemehub", "Sir Meme Hub", "sirmemehub.com", "Taking script hubs to the next level.",
                new ThemePalette
                {
                    Accent = "#FF2EC8", GradientStart = "#FF2EC8", GradientEnd = "#2BF584", Purple = "#B36BFF",
                    Background = "#08050E", Surface = "#161020", Hairline = "#241C33", Glow = "#FF2EC8",
                }),

            // The forum's #CD1818 (category bars, nav underline), brightened at the same hue so it reads on
            // dark, into its #FF5C5C link red, on its own charcoal with a faint red tint.
            Site("v3rm", "V3rmillion", "v3rm.net", "Programming and Gaming community forum",
                new ThemePalette
                {
                    Accent = "#E83B3B", GradientStart = "#E83B3B", GradientEnd = "#FF5C5C", Purple = "#CD1818",
                    Background = "#110D0D", Surface = "#1D1717", Hairline = "#2D2525", Glow = "#E83B3B",
                }),

            // Seasonal. Each one takes over the whole app by itself on its days unless the user turned that off
            // (see ThemeManager.ActiveSeasonal), in calendar order here because the Appearance page lists them so.
            // All our own art, code-generated.

            // BeastStrap (v420.x, fittingly) switches to this for the day every April 20th. A neon leaf,
            // "4 (leaf) 20" on green and purple haze. Leaf green into gold, with the purple haze as the third colour.
            Seasonal("420", "420", "Every April 20th", "It's always 4/20 somewhere.",
                new ThemePalette
                {
                    Accent = "#6FE33C", GradientStart = "#6FE33C", GradientEnd = "#FFD23F", Purple = "#A66BFF",
                    Background = "#070B06", Surface = "#0F170D", Hairline = "#1E2B1A", Glow = "#6FE33C",
                },
                new Season(4, 20, 1, "Happy 4/20!",
                    "The 420 theme is on for today. Your own theme is back tomorrow, or straight away if you pick one.",
                    "Switch to the 420 theme every April 20th",
                    "Just for the day, then your own theme is back on the 21st. Picking a theme or changing a colour on the day turns it off until next year.")),

            // Halloween season, the owner's call: all of October plus Nov 1, the day after (like Boxing Day below). A
            // glossy ribbed jack-o'-lantern lit from inside, "HALL (pumpkin) WEEN" in the 420's lettering, a haunted house
            // on the moon, a corner web, on violet-black haze. Pumpkin orange into candle gold: only 14 degrees apart, so
            // the app adds no middle stop (orange to violet put a hot pink in every button). Night violet as the third
            // colour, deeper than the 420's lavender so the Seasonal row doesn't show the same dot twice.
            Seasonal("halloween", "Halloween", "Oct 1 to Nov 1", "Trick or execute.",
                new ThemePalette
                {
                    Accent = "#FF7A1A", GradientStart = "#FF7A1A", GradientEnd = "#FFD27A", Purple = "#9450FF",
                    Background = "#0B0613", Surface = "#160E22", Hairline = "#291C3A", Glow = "#FF7A1A",
                },
                new Season(10, 1, 32, "Spooky season!",
                    "The Halloween theme is on until Nov 1. Your own theme is back on the 2nd, or straight away if you pick one.",
                    "Switch to the Halloween theme every October",
                    "From Oct 1 to Nov 1, then your own theme is back on the 2nd. Picking a theme or changing a colour while it's on turns it off until next year.")),

            // Christmas Eve, Christmas Day and Boxing Day. A glossy three-tier tree with a gold star, "MERRY (tree) XMAS"
            // with snow on the letters, wind-blown snow where the 420 has smoke, on midnight navy. Holly red into snow
            // white (a candy cane), evergreen as the third colour. Never red to green: the app would insert a lime
            // middle stop, and red + gold + green next to the 420 card reads as the Rasta flag.
            Seasonal("christmas", "Christmas", "Christmas Eve to Boxing Day", "Snow detections this year.",
                new ThemePalette
                {
                    Accent = "#FF3B52", GradientStart = "#FF3B52", GradientEnd = "#EEF4FF", Purple = "#2FD77F",
                    Background = "#060A18", Surface = "#0D1429", Hairline = "#1B2543", Glow = "#FF3B52",
                },
                new Season(12, 24, 3, "Merry Christmas!",
                    "The Christmas theme is on until Boxing Day. Your own theme is back on the 27th, or straight away if you pick one.",
                    "Switch to the Christmas theme every Christmas",
                    "From Christmas Eve to Boxing Day, then your own theme is back on the 27th. Picking a theme or changing a colour while it's on turns it off until next year.")),
        };

        public static IEnumerable<CreatorTheme> YouTubers => All.Where(c => c.Kind == CreatorKind.YouTuber);
        public static IEnumerable<CreatorTheme> Sites => All.Where(c => c.Kind == CreatorKind.ScriptSite);
        public static IEnumerable<CreatorTheme> Seasonals => All.Where(c => c.Season is not null);

        // Which seasonal theme counts on `date`, in three steps. No clock or app state in any of them, so seasoncheck can
        // try every day of the year. Runs never overlap (it checks that too).

        // The seasonal theme whose run `date` falls in with its yearly switch on, or null, whether or not it was turned
        // off for this run. The Appearance notice covers it either way.
        public static CreatorTheme? Running(DateTime date, Persistable.Settings settings) =>
            Seasonals.FirstOrDefault(t => t.Season!.RunYear(date) is not null && settings.SeasonalThemesOff?.Contains(t.Id) != true);

        // Running, unless it was turned off for this run (from the Appearance notice, or by a theme choice made during
        // it). This is what a theme choice during the run turns off.
        public static CreatorTheme? InSeason(DateTime date, Persistable.Settings settings) =>
            Running(date, settings) is { } theme
            && !(settings.SeasonalThemesSkipped?.TryGetValue(theme.Id, out int skipped) == true && skipped == theme.Season!.RunYear(date))
                ? theme : null;

        // What stands in for the saved theme on `date` (ThemeManager.ActiveSeasonal asks about today): InSeason, unless
        // it already IS the user's saved theme. Then their own copy shows instead, colour tweaks and all, rather than
        // the stock one plus a notice promising their theme back.
        public static CreatorTheme? SeasonalOn(DateTime date, Persistable.Settings settings) =>
            InSeason(date, settings) is { } theme && !theme.Id.Equals(settings.ActiveCreatorTheme, StringComparison.OrdinalIgnoreCase)
                ? theme : null;

        // Drops the "turned off for this year" entries whose run is over (or whose theme no longer exists). A skip only
        // counts for the run it was made in, so they change nothing, but they stayed in Settings.json for good, and a
        // switch or the notice button removing one made the settings window ask about changes that weren't there.
        public static void PruneFinishedSkips(Persistable.Settings settings, DateTime date)
        {
            if (settings.SeasonalThemesSkipped is not { Count: > 0 } skipped)
                return;

            foreach (var (id, year) in skipped.ToList())
                if (Find(id)?.Season?.RunYear(date) != year)
                    skipped.Remove(id);
        }

        public static CreatorTheme? Find(string? id) =>
            string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }
}
