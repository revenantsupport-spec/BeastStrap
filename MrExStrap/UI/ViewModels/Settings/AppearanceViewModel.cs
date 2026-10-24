using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;
using ICSharpCode.SharpZipLib.Zip;

using Microsoft.Win32;

using BeastStrap.UI.Elements.Settings;
using BeastStrap.UI.Elements.Editor;
using BeastStrap.UI.Elements.Dialogs;

namespace BeastStrap.UI.ViewModels.Settings
{
    public class AppearanceViewModel : NotifyPropertyChangedViewModel
    {
        private readonly Page _page;

        public ICommand PreviewBootstrapperCommand => new RelayCommand(PreviewBootstrapper);
        public ICommand BrowseCustomIconLocationCommand => new RelayCommand(BrowseCustomIconLocation);

        public ICommand AddCustomThemeCommand => new RelayCommand(AddCustomTheme);
        public ICommand DeleteCustomThemeCommand => new RelayCommand(DeleteCustomTheme);
        public ICommand RenameCustomThemeCommand => new RelayCommand(RenameCustomTheme);
        public ICommand EditCustomThemeCommand => new RelayCommand(EditCustomTheme);
        public ICommand ExportCustomThemeCommand => new RelayCommand(ExportCustomTheme);

        private void PreviewBootstrapper()
        {
            IBootstrapperDialog dialog = App.Settings.Prop.BootstrapperStyle.GetNew();

            if (App.Settings.Prop.BootstrapperStyle == BootstrapperStyle.ByfronDialog)
                dialog.Message = Strings.Bootstrapper_StylePreview_ImageCancel;
            else
                dialog.Message = Strings.Bootstrapper_StylePreview_TextCancel;

            dialog.CancelEnabled = true;
            dialog.ShowBootstrapper();
        }

        private void BrowseCustomIconLocation()
        {
            var dialog = new OpenFileDialog
            {
                Filter = $"{Strings.Menu_IconFiles}|*.ico"
            };

            if (dialog.ShowDialog() != true)
                return;

            CustomIconLocation = dialog.FileName;
            OnPropertyChanged(nameof(CustomIconLocation));
        }

        public AppearanceViewModel(Page page)
        {
            _page = page;

            foreach (var entry in BootstrapperIconEx.Selections)
                Icons.Add(new BootstrapperIconEntry { IconType = entry });

            PopulateCustomThemes();
        }

        public IEnumerable<Theme> Themes { get; } = Enum.GetValues(typeof(Theme)).Cast<Theme>();

        public Theme Theme
        {
            get => App.Settings.Prop.Theme;
            set
            {
                App.Settings.Prop.Theme = value;
                if (Window.GetWindow(_page) is MainWindow mw)
                    mw.ApplyTheme();
            }
        }

        public static List<string> Languages => Locale.GetLanguages();

        public string SelectedLanguage
        {
            get => Locale.SupportedLocales[App.Settings.Prop.Locale];
            set => App.Settings.Prop.Locale = Locale.GetIdentifierFromName(value);
        }

        // ===== App theming (BeastStrap fork feature) â€” live-editable brand palette =====
        private BeastStrap.Models.ThemePalette Pal => App.Settings.Prop.Palette;
        private void ApplyTheme() => BeastStrap.Utility.ThemeManager.Apply(App.Settings.Prop.Palette);

        public ICommand ResetThemeCommand => new RelayCommand(ResetTheme);
        public ICommand BrowseAppIconCommand => new RelayCommand(BrowseAppIcon);
        public ICommand ClearAppIconCommand => new RelayCommand(() => AppIconLocation = "");

        // Built-in presets, then the creator themes (picking one here applies the whole creator theme,
        // same as its card's Apply button), then Custom.
        public IEnumerable<string> ThemePresets => BeastStrap.Utility.ThemeManager.Presets.Keys
            .Concat(BeastStrap.Models.CreatorTheme.All.Select(c => c.Name))
            .Append("Custom");

        public string SelectedThemePreset
        {
            get => App.Settings.Prop.SelectedThemePreset;
            set
            {
                if (value is null)
                    return;

                var creator = BeastStrap.Models.CreatorTheme.All.FirstOrDefault(c => c.Name == value);
                if (creator != null)
                {
                    ApplyCreator(creator);
                    return;
                }

                App.Settings.Prop.SelectedThemePreset = value;
                OnPropertyChanged(nameof(SelectedThemePreset));

                if (BeastStrap.Utility.ThemeManager.Presets.TryGetValue(value, out var preset))
                {
                    // A plain preset means leaving any creator theme behind, art and all.
                    EndSeasonalForThisYear();
                    App.Settings.Prop.ActiveCreatorTheme = "";
                    App.Settings.Prop.Palette = preset.Clone();
                    ApplyTheme();
                    BeastStrap.Utility.ThemeManager.ApplyWindowIcons();
                    NotifyColours();
                    RefreshCreatorCards();
                    RefreshThemeIcons();
                }
            }
        }

        // ===== Creator themes =====

        public sealed class CreatorCard : NotifyPropertyChangedViewModel
        {
            public BeastStrap.Models.CreatorTheme Theme { get; }
            public System.Windows.Media.ImageSource? Avatar { get; }
            public System.Windows.Media.ImageSource? Banner { get; }

            // The creator's own colours, whatever theme is active right now.
            public IReadOnlyList<System.Windows.Media.Brush> Swatches { get; }
            public System.Windows.Media.Brush RingBrush { get; }

            // The card's Apply button wears the card's own button gradient (the brighter one on hover, as the
            // app's primary buttons do), not the active theme's, so each card previews its own theme.
            public System.Windows.Media.Brush ApplyBrush { get; }
            public System.Windows.Media.Brush ApplyHoverBrush { get; }
            private readonly System.Windows.Media.Brush _accentBrush;

            public ICommand ApplyCommand { get; }
            public ICommand LinkCommand { get; }

            public bool IsActive => App.Settings.Prop.ActiveCreatorTheme == Theme.Id;

            // Active AND still on their own colours. Tweaking a colour keeps the art (still ACTIVE) but makes the
            // palette Custom, and the card used to stay on a disabled "Applied" with no way back to their colours.
            private bool IsPristine => IsActive && App.Settings.Prop.SelectedThemePreset == Theme.Name;

            public Visibility ActiveVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
            public Visibility AppliedVisibility => IsPristine ? Visibility.Visible : Visibility.Collapsed;
            public Visibility ApplyVisibility => IsPristine ? Visibility.Collapsed : Visibility.Visible;
            public string ApplyText => IsActive ? "Re-apply colours" : "Apply theme";
            public Visibility LinkVisibility => Theme.HasLink ? Visibility.Visible : Visibility.Collapsed;
            public System.Windows.Media.Brush CardBorder => IsActive
                ? _accentBrush
                : (System.Windows.Media.Brush)(Application.Current.TryFindResource("GlassBorderBrush") ?? System.Windows.Media.Brushes.Transparent);

            public CreatorCard(BeastStrap.Models.CreatorTheme theme, Action<BeastStrap.Models.CreatorTheme> apply)
            {
                Theme = theme;
                // Decoded at about twice the size they're drawn, not at full size: twelve 1400 px banners for a 66 px
                // strip and 256 px avatars for a 58 px ring cost the settings window about 50 MB. 700 px covers the
                // widest card a maximized window gets, and the strip grows with the card (see AppearancePage).
                Avatar = Load(theme.AvatarUri, 128);
                Banner = Load(theme.CardBannerUri, 700);

                var p = theme.Palette;
                System.Windows.Media.Color C(string hex) => BeastStrap.Utility.ThemeManager.Parse(hex, System.Windows.Media.Colors.Gray);
                // Their three colours. Not the card surface: it's a near-black a step off the card's own
                // background, so it rendered as an empty-looking fourth dot.
                Swatches = new[] { p.Accent, p.GradientEnd, p.Purple }
                    .Select(h => (System.Windows.Media.Brush)Frozen(new System.Windows.Media.SolidColorBrush(C(h))))
                    .ToList();
                _accentBrush = Frozen(new System.Windows.Media.SolidColorBrush(C(p.Accent)));
                var tm = (Start: C(p.GradientStart), End: C(p.GradientEnd), Third: C(p.Purple));
                RingBrush = BeastStrap.Utility.ThemeManager.Gradient(tm.Start, tm.End, tm.Third);
                ApplyBrush = BeastStrap.Utility.ThemeManager.ButtonGradient(tm.Start, tm.End, tm.Third);
                ApplyHoverBrush = BeastStrap.Utility.ThemeManager.Gradient(
                    BeastStrap.Utility.ThemeManager.Lighten(BeastStrap.Utility.ThemeManager.LiftForLabel(tm.Start), 0.28),
                    BeastStrap.Utility.ThemeManager.Lighten(BeastStrap.Utility.ThemeManager.LiftForLabel(tm.End), 0.28),
                    BeastStrap.Utility.ThemeManager.Lighten(tm.Third, 0.28));

                ApplyCommand = new RelayCommand(() => apply(theme));
                LinkCommand = new RelayCommand(() =>
                {
                    if (theme.Url is not null)
                        GlobalViewModel.OpenWebpageCommand.Execute(theme.Url);
                });
            }

            public void Refresh()
            {
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(ActiveVisibility));
                OnPropertyChanged(nameof(AppliedVisibility));
                OnPropertyChanged(nameof(ApplyVisibility));
                OnPropertyChanged(nameof(ApplyText));
                OnPropertyChanged(nameof(CardBorder));
            }

            private static T Frozen<T>(T f) where T : System.Windows.Freezable { f.Freeze(); return f; }

            private static System.Windows.Media.ImageSource? Load(string uri, int decodeWidth)
            {
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(uri, UriKind.Absolute);
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = decodeWidth;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("AppearanceViewModel::CreatorCard", ex);
                    return null;
                }
            }
        }

        private List<CreatorCard>? _creatorCards;
        public List<CreatorCard> CreatorCards => _creatorCards ??= BeastStrap.Models.CreatorTheme.All.Select(c => new CreatorCard(c, ApplyCreator)).ToList();

        // The page shows them in groups. Same card objects as above, so a refresh reaches all of them.
        public List<CreatorCard> YouTuberCards => CreatorCards.Where(c => c.Theme.Kind == BeastStrap.Models.CreatorKind.YouTuber).ToList();
        public List<CreatorCard> SiteCards => CreatorCards.Where(c => c.Theme.Kind == BeastStrap.Models.CreatorKind.ScriptSite).ToList();
        public List<CreatorCard> SeasonalCards => CreatorCards.Where(c => c.Theme.Kind == BeastStrap.Models.CreatorKind.Seasonal).ToList();

        // ===== Seasonal themes =====

        // One yearly on/off switch per seasonal theme, under their cards.
        public sealed class SeasonalSwitch : NotifyPropertyChangedViewModel
        {
            private readonly Action _changed;

            public BeastStrap.Models.CreatorTheme Theme { get; }
            public string Header => Theme.Season?.SwitchHeader ?? "";
            public string Description => Theme.Season?.SwitchDescription ?? "";

            public SeasonalSwitch(BeastStrap.Models.CreatorTheme theme, Action changed)
            {
                Theme = theme;
                _changed = changed;
            }

            public bool IsOn
            {
                get => App.Settings.Prop.SeasonalThemesOff?.Contains(Theme.Id) != true;
                set
                {
                    var off = App.Settings.Prop.SeasonalThemesOff ??= new();
                    off.RemoveAll(id => id == Theme.Id);

                    // Switching it back on means now as well, so it also forgets a "turned off for this year". Without
                    // that, flipping it on during its days did nothing until next year. Only this run's: an old entry
                    // changes nothing, and removing it made closing the window ask about unsaved changes.
                    if (value)
                    {
                        var skips = App.Settings.Prop.SeasonalThemesSkipped;
                        if (skips is not null && Theme.Season?.RunYear(BeastStrap.Utility.ThemeManager.Today) is int year
                            && skips.TryGetValue(Theme.Id, out int skipped) && skipped == year)
                            skips.Remove(Theme.Id);
                    }
                    else
                    {
                        // Kept in order: the unsaved-changes check hashes the list, and off-on-off would otherwise
                        // reorder it and ask about changes that aren't there.
                        off.Add(Theme.Id);
                        off.Sort(StringComparer.Ordinal);
                    }

                    OnPropertyChanged(nameof(IsOn));
                    _changed();
                }
            }
        }

        private List<SeasonalSwitch>? _seasonalSwitches;
        public List<SeasonalSwitch> SeasonalSwitches => _seasonalSwitches ??=
            BeastStrap.Models.CreatorTheme.Seasonals.Select(t => new SeasonalSwitch(t, ReapplyWholeTheme)).ToList();

        // The notice at the top of Creator themes. It covers the seasonal theme whose run today is (yearly switch on),
        // unless that's the user's own saved theme, which simply shows: while it's standing in for theirs, and after
        // it's been turned off for this year, so that's one click to undo.
        private static BeastStrap.Models.CreatorTheme? NoticeTheme =>
            BeastStrap.Models.CreatorTheme.Running(BeastStrap.Utility.ThemeManager.Today, App.Settings.Prop) is { } theme
            && !theme.Id.Equals(App.Settings.Prop.ActiveCreatorTheme, StringComparison.OrdinalIgnoreCase)
                ? theme : null;

        private static bool SeasonalShowing => BeastStrap.Utility.ThemeManager.ActiveSeasonal is not null;

        public Visibility SeasonalNoticeVisibility => NoticeTheme is null ? Visibility.Collapsed : Visibility.Visible;
        public string SeasonalGreeting => NoticeTheme?.Season?.Greeting ?? "";

        public string SeasonalNotice => NoticeTheme is not { Season: { } season } theme ? ""
            : SeasonalShowing ? season.Notice
            : $"The {theme.Name} theme is off until next year.";

        public string SeasonalButtonText => !SeasonalShowing ? "Turn it back on"
            : NoticeTheme?.Season?.Days > 1 ? "Turn it off for this year"
            : "Turn it off for today";

        public ICommand SeasonalButtonCommand => new RelayCommand(() =>
        {
            if (SeasonalShowing)
                EndSeasonalForThisYear();
            else if (NoticeTheme is { } theme)
                App.Settings.Prop.SeasonalThemesSkipped?.Remove(theme.Id);

            ReapplyWholeTheme();
        });

        // Any theme choice made during a seasonal run beats it, for the rest of the run this year. Even when it isn't
        // standing in because it IS the saved theme: otherwise picking something else would bring it straight back.
        private void EndSeasonalForThisYear()
        {
            var today = BeastStrap.Utility.ThemeManager.Today;
            if (BeastStrap.Models.CreatorTheme.InSeason(today, App.Settings.Prop) is { Season: { } season } theme && season.RunYear(today) is int year)
                (App.Settings.Prop.SeasonalThemesSkipped ??= new())[theme.Id] = year;
        }

        private void ReapplyWholeTheme()
        {
            ApplyTheme();
            BeastStrap.Utility.ThemeManager.ApplyWindowIcons();
            RefreshCreatorCards();
            RefreshThemeIcons();
        }

        private void NotifySeasonal()
        {
            OnPropertyChanged(nameof(SeasonalNoticeVisibility));
            OnPropertyChanged(nameof(SeasonalGreeting));
            OnPropertyChanged(nameof(SeasonalNotice));
            OnPropertyChanged(nameof(SeasonalButtonText));
        }

        // Settings pages are cached for the window's lifetime, so this page can come back after the theme changed
        // under it (a seasonal run starting or ending at midnight, or a change made from another window). It follows
        // ThemeManager while it's on screen and catches up each time it's shown. Wired to the page's Loaded/Unloaded.
        public void StartFollowingTheme()
        {
            BeastStrap.Utility.ThemeManager.ThemeStateChanged -= OnThemeStateChanged;
            BeastStrap.Utility.ThemeManager.ThemeStateChanged += OnThemeStateChanged;

            // Bring the theme itself up to date first. The window only rechecks once a minute, so just after midnight
            // the page could describe today while the app still wore yesterday's theme.
            try
            {
                BeastStrap.Utility.ThemeManager.ApplyFromSettings();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("AppearanceViewModel::StartFollowingTheme", ex);
            }

            OnThemeStateChanged();
        }

        public void StopFollowingTheme() => BeastStrap.Utility.ThemeManager.ThemeStateChanged -= OnThemeStateChanged;

        private void OnThemeStateChanged()
        {
            RefreshCreatorCards();
            RefreshThemeIcons();
        }

        // Only for a SAVED creator theme. A seasonal one standing in has the notice's own button, which keeps the
        // user's colours. This one resets them to the Default preset, and it used to appear for everyone during a run.
        public Visibility CreatorActiveVisibility =>
            BeastStrap.Models.CreatorTheme.Find(App.Settings.Prop.ActiveCreatorTheme) is null ? Visibility.Collapsed : Visibility.Visible;

        public ICommand UseStandardLookCommand => new RelayCommand(() => SelectedThemePreset = "Default");

        // The whole creator theme in one go: palette, art, window icons. Tweaking a colour afterwards keeps
        // the art (MarkCustom leaves ActiveCreatorTheme alone), so people can fine-tune an edition.
        private void ApplyCreator(BeastStrap.Models.CreatorTheme creator)
        {
            EndSeasonalForThisYear();
            App.Settings.Prop.ActiveCreatorTheme = creator.Id;
            App.Settings.Prop.Palette = creator.Palette.Clone();
            App.Settings.Prop.SelectedThemePreset = creator.Name;

            ApplyTheme();
            BeastStrap.Utility.ThemeManager.ApplyWindowIcons();

            OnPropertyChanged(nameof(SelectedThemePreset));
            NotifyColours();
            RefreshCreatorCards();
            RefreshThemeIcons();
        }

        private void RefreshCreatorCards()
        {
            foreach (var card in CreatorCards)
                card.Refresh();
            OnPropertyChanged(nameof(CreatorActiveVisibility));
            NotifySeasonal();
        }

        // The Bootstrapper section's "BeastStrap" icon entry is the theme's icon, so redraw it when the theme
        // (not just a colour) changes.
        private void RefreshThemeIcons()
        {
            foreach (var entry in Icons)
                entry.Refresh();
        }

        // Accent drives the gradient start + glow too, so the palette stays cohesive from one colour.
        public string AccentHex
        {
            get => Pal.Accent;
            set { Pal.Accent = value; Pal.GradientStart = value; Pal.Glow = value; MarkCustom(); ApplyTheme(); NotifySwatches(); }
        }
        public string GradientEndHex
        {
            get => Pal.GradientEnd;
            set { Pal.GradientEnd = value; MarkCustom(); ApplyTheme(); NotifySwatches(); }
        }
        public string PurpleHex
        {
            get => Pal.Purple;
            set { Pal.Purple = value; MarkCustom(); ApplyTheme(); NotifySwatches(); }
        }
        public string BackgroundHex
        {
            get => Pal.Background;
            set { Pal.Background = value; MarkCustom(); ApplyTheme(); NotifySwatches(); }
        }
        public string SurfaceHex
        {
            get => Pal.Surface;
            set { Pal.Surface = value; MarkCustom(); ApplyTheme(); NotifySwatches(); }
        }

        // The swatch beside each hex box, from the same SAVED palette the box shows. They used to be the live brand
        // brushes, which during a seasonal run put the seasonal colours next to the user's own hex values.
        private (System.Windows.Media.Color Accent, System.Windows.Media.Color GradientStart, System.Windows.Media.Color GradientEnd,
            System.Windows.Media.Color Purple, System.Windows.Media.Color Background, System.Windows.Media.Color Surface,
            System.Windows.Media.Color Hairline, System.Windows.Media.Color Glow) Saved => BeastStrap.Utility.ThemeManager.Resolve(Pal);

        public System.Windows.Media.Brush AccentSwatch => Swatch(Saved.Accent);
        public System.Windows.Media.Brush GradientSwatch => BeastStrap.Utility.ThemeManager.Gradient(Saved.GradientStart, Saved.GradientEnd, Saved.Purple);
        public System.Windows.Media.Brush PurpleSwatch => Swatch(Saved.Purple);
        public System.Windows.Media.Brush BackgroundSwatch => Swatch(Saved.Background);
        public System.Windows.Media.Brush SurfaceSwatch => Swatch(Saved.Surface);

        private static System.Windows.Media.Brush Swatch(System.Windows.Media.Color c)
        {
            var brush = new System.Windows.Media.SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }

        private void NotifySwatches()
        {
            OnPropertyChanged(nameof(AccentSwatch));
            OnPropertyChanged(nameof(GradientSwatch));
            OnPropertyChanged(nameof(PurpleSwatch));
            OnPropertyChanged(nameof(BackgroundSwatch));
            OnPropertyChanged(nameof(SurfaceSwatch));
        }

        public bool EnableAurora
        {
            get => App.Settings.Prop.EnableAurora;
            set { App.Settings.Prop.EnableAurora = value; ApplyTheme(); }
        }
        public bool EnableGlass
        {
            get => App.Settings.Prop.EnableGlass;
            set { App.Settings.Prop.EnableGlass = value; ApplyTheme(); }
        }
        public bool EnableGlow
        {
            get => App.Settings.Prop.EnableGlow;
            set { App.Settings.Prop.EnableGlow = value; ApplyTheme(); }
        }

        // ===== Per-surface extras: gradient direction + glow intensity =====
        public IEnumerable<BeastStrap.Enums.GradientDirection> GradientDirections
            => Enum.GetValues(typeof(BeastStrap.Enums.GradientDirection)).Cast<BeastStrap.Enums.GradientDirection>();

        public BeastStrap.Enums.GradientDirection GradientDirection
        {
            get => Pal.GradientDirection;
            set { Pal.GradientDirection = value; MarkCustom(); ApplyTheme(); }
        }

        public IEnumerable<BeastStrap.Enums.GlowIntensity> GlowIntensities
            => Enum.GetValues(typeof(BeastStrap.Enums.GlowIntensity)).Cast<BeastStrap.Enums.GlowIntensity>();

        public BeastStrap.Enums.GlowIntensity GlowIntensity
        {
            get => Pal.GlowIntensity;
            set { Pal.GlowIntensity = value; MarkCustom(); ApplyTheme(); }
        }

        // ===== Custom wallpaper =====
        public ICommand BrowseWallpaperCommand => new RelayCommand(BrowseWallpaper);
        public ICommand ClearWallpaperCommand => new RelayCommand(ClearWallpaper);

        public bool EnableWallpaper
        {
            get => App.Settings.Prop.EnableWallpaper;
            set { App.Settings.Prop.EnableWallpaper = value; OnPropertyChanged(nameof(EnableWallpaper)); ApplyTheme(); }
        }

        public string WallpaperLocation
        {
            get => App.Settings.Prop.WallpaperLocation;
            set { App.Settings.Prop.WallpaperLocation = value; OnPropertyChanged(nameof(WallpaperLocation)); ApplyTheme(); }
        }

        public double WallpaperOpacity
        {
            get => App.Settings.Prop.WallpaperOpacity;
            set
            {
                App.Settings.Prop.WallpaperOpacity = value;
                UpdateOpacityResource("WallpaperOpacity", value);
            }
        }

        private void BrowseWallpaper()
        {
            var dialog = new OpenFileDialog { Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.webp" };
            if (dialog.ShowDialog() != true)
                return;
            WallpaperLocation = dialog.FileName;
            EnableWallpaper = true;
        }

        private void ClearWallpaper()
        {
            WallpaperLocation = "";
            EnableWallpaper = false;
        }

        // ===== Animated (GIF) background =====
        public ICommand BrowseGifWallpaperCommand => new RelayCommand(BrowseGifWallpaper);
        public ICommand ClearGifWallpaperCommand => new RelayCommand(ClearGifWallpaper);

        public bool EnableGifWallpaper
        {
            get => App.Settings.Prop.EnableGifWallpaper;
            set { App.Settings.Prop.EnableGifWallpaper = value; OnPropertyChanged(nameof(EnableGifWallpaper)); ApplyTheme(); }
        }

        public string GifWallpaperLocation
        {
            get => App.Settings.Prop.GifWallpaperLocation;
            set { App.Settings.Prop.GifWallpaperLocation = value; OnPropertyChanged(nameof(GifWallpaperLocation)); ApplyTheme(); }
        }

        public double GifWallpaperOpacity
        {
            get => App.Settings.Prop.GifWallpaperOpacity;
            set
            {
                App.Settings.Prop.GifWallpaperOpacity = value;
                UpdateOpacityResource("GifBackgroundOpacity", value);
            }
        }

        private string _gifWallpaperUrl = "";
        public string GifWallpaperUrl
        {
            get => _gifWallpaperUrl;
            set { _gifWallpaperUrl = value; OnPropertyChanged(nameof(GifWallpaperUrl)); }
        }

        public ICommand LoadGifWallpaperUrlCommand => new RelayCommand(LoadGifWallpaperUrl);

        private void LoadGifWallpaperUrl()
        {
            string url = _gifWallpaperUrl?.Trim() ?? "";
            if (string.IsNullOrEmpty(url))
                return;
            GifWallpaperLocation = url;
            EnableGifWallpaper = true;
        }

        private static void UpdateOpacityResource(string key, double value)
            => System.Windows.Application.Current.Resources[key] = Math.Clamp(value, 0.1, 1.0);

        private void BrowseGifWallpaper()
        {
            var dialog = new OpenFileDialog { Filter = "Animated wallpapers|*.gif;*.mp4;*.webm;*.mov;*.m4v;*.png;*.jpg;*.jpeg;*.bmp;*.webp" };
            if (dialog.ShowDialog() != true)
                return;
            GifWallpaperLocation = dialog.FileName;
            EnableGifWallpaper = true;
        }

        private void ClearGifWallpaper()
        {
            GifWallpaperLocation = "";
            EnableGifWallpaper = false;
        }

        private void MarkCustom()
        {
            EndSeasonalForThisYear();
            App.Settings.Prop.SelectedThemePreset = "Custom";
            OnPropertyChanged(nameof(SelectedThemePreset));
            RefreshCreatorCards();
        }

        private void NotifyColours()
        {
            OnPropertyChanged(nameof(AccentHex));
            OnPropertyChanged(nameof(GradientEndHex));
            OnPropertyChanged(nameof(PurpleHex));
            OnPropertyChanged(nameof(BackgroundHex));
            OnPropertyChanged(nameof(SurfaceHex));
            NotifySwatches();
        }

        private void ResetTheme()
        {
            EndSeasonalForThisYear();
            App.Settings.Prop.Palette = new BeastStrap.Models.ThemePalette();
            App.Settings.Prop.SelectedThemePreset = "Default";
            App.Settings.Prop.ActiveCreatorTheme = "";
            App.Settings.Prop.EnableAurora = true;
            App.Settings.Prop.EnableGlass = true;
            App.Settings.Prop.EnableGlow = true;

            ApplyTheme();
            BeastStrap.Utility.ThemeManager.ApplyWindowIcons();
            RefreshCreatorCards();
            RefreshThemeIcons();
            NotifyColours();
            OnPropertyChanged(nameof(SelectedThemePreset));
            OnPropertyChanged(nameof(EnableAurora));
            OnPropertyChanged(nameof(EnableGlass));
            OnPropertyChanged(nameof(EnableGlow));
            OnPropertyChanged(nameof(GradientDirection));
            OnPropertyChanged(nameof(GlowIntensity));
        }

        // ===== Custom app icon =====
        public string AppIconLocation
        {
            get => App.Settings.Prop.CustomAppIconLocation;
            set { App.Settings.Prop.CustomAppIconLocation = value; OnPropertyChanged(nameof(AppIconLocation)); ApplyAppIcon(); }
        }

        private void BrowseAppIcon()
        {
            var dialog = new OpenFileDialog { Filter = "Icon and image files|*.ico;*.png;*.jpg;*.jpeg" };

            if (dialog.ShowDialog() != true)
                return;

            AppIconLocation = dialog.FileName;
        }

        // ThemeManager owns the icon choice now (custom icon, else the creator theme's, else BeastStrap's)
        // and applies it to every window as it opens, so a custom icon also survives a restart.
        private void ApplyAppIcon()
        {
            try
            {
                BeastStrap.Utility.ThemeManager.ApplyWindowIcons();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("AppearanceViewModel::ApplyAppIcon", ex);
            }
        }

        public IEnumerable<BootstrapperStyle> Dialogs { get; } = BootstrapperStyleEx.Selections;

        public BootstrapperStyle Dialog
        {
            get => App.Settings.Prop.BootstrapperStyle;
            set
            {
                App.Settings.Prop.BootstrapperStyle = value;
                OnPropertyChanged(nameof(CustomThemesExpanded)); // TODO: only fire when needed
            }
        }

        public bool CustomThemesExpanded => App.Settings.Prop.BootstrapperStyle == BootstrapperStyle.CustomDialog;

        public ObservableCollection<BootstrapperIconEntry> Icons { get; set; } = new();

        public BootstrapperIcon Icon
        {
            get => App.Settings.Prop.BootstrapperIcon;
            set => App.Settings.Prop.BootstrapperIcon = value; 
        }

        public string Title
        {
            get => App.Settings.Prop.BootstrapperTitle;
            set => App.Settings.Prop.BootstrapperTitle = value;
        }

        public string CustomIconLocation
        {
            get => App.Settings.Prop.BootstrapperIconCustomLocation;
            set
            {
                if (String.IsNullOrEmpty(value))
                {
                    if (App.Settings.Prop.BootstrapperIcon == BootstrapperIcon.IconCustom)
                        App.Settings.Prop.BootstrapperIcon = BootstrapperIcon.IconBloxstrap;
                }
                else
                {
                    App.Settings.Prop.BootstrapperIcon = BootstrapperIcon.IconCustom;
                }

                App.Settings.Prop.BootstrapperIconCustomLocation = value;

                OnPropertyChanged(nameof(Icon));
                OnPropertyChanged(nameof(Icons));
            }
        }

        private void DeleteCustomThemeStructure(string name)
        {
            string dir = Path.Combine(Paths.CustomThemes, name);
            Directory.Delete(dir, true);
        }

        private void RenameCustomThemeStructure(string oldName, string newName)
        {
            string oldDir = Path.Combine(Paths.CustomThemes, oldName);
            string newDir = Path.Combine(Paths.CustomThemes, newName);
            Directory.Move(oldDir, newDir);
        }

        private void AddCustomTheme()
        {
            var dialog = new AddCustomThemeDialog();
            dialog.ShowDialog();

            if (dialog.Created)
            {
                CustomThemes.Add(dialog.ThemeName);
                SelectedCustomThemeIndex = CustomThemes.Count - 1;

                OnPropertyChanged(nameof(SelectedCustomThemeIndex));
                OnPropertyChanged(nameof(IsCustomThemeSelected));

                if (dialog.OpenEditor)
                    EditCustomTheme();
            }
        }

        private void DeleteCustomTheme()
        {
            if (SelectedCustomTheme is null)
                return;

            // DeleteCustomThemeStructure is a recursive Directory.Delete, so this really does
            // take the files off disk â€” and not via the recycle bin. Say so plainly.
            var confirm = Frontend.ShowMessageBox(
                $"Delete the custom theme '{SelectedCustomTheme}'?\n\nIts folder is removed from disk along with everything in it â€” Theme.xml, images, sounds. There's no undo, so export it first if you might want it back.",
                MessageBoxImage.Warning, MessageBoxButton.YesNo, MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes)
                return;

            try
            {
                DeleteCustomThemeStructure(SelectedCustomTheme);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("AppearanceViewModel::DeleteCustomTheme", ex);
                Frontend.ShowMessageBox($"Couldn't delete the theme '{SelectedCustomTheme}'. Something is probably holding its files open â€” close the theme editor and any Explorer window in its folder, then try again.", MessageBoxImage.Error);
                return;
            }

            CustomThemes.Remove(SelectedCustomTheme);

            if (CustomThemes.Any())
            {
                SelectedCustomThemeIndex = CustomThemes.Count - 1;
                OnPropertyChanged(nameof(SelectedCustomThemeIndex));
            }

            OnPropertyChanged(nameof(IsCustomThemeSelected));
        }

        private void RenameCustomTheme()
        {
            const string LOG_IDENT = "AppearanceViewModel::RenameCustomTheme";

            if (SelectedCustomTheme is null || SelectedCustomTheme == SelectedCustomThemeName)
                return;

            if (string.IsNullOrEmpty(SelectedCustomThemeName))
            {
                Frontend.ShowMessageBox(Strings.CustomTheme_Add_Errors_NameEmpty, MessageBoxImage.Error);
                return;
            }

            var validationResult = PathValidator.IsFileNameValid(SelectedCustomThemeName);

            if (validationResult != PathValidator.ValidationResult.Ok)
            {
                switch (validationResult)
                {
                    case PathValidator.ValidationResult.IllegalCharacter:
                        Frontend.ShowMessageBox(Strings.CustomTheme_Add_Errors_NameIllegalCharacters, MessageBoxImage.Error);
                        break;
                    case PathValidator.ValidationResult.ReservedFileName:
                        Frontend.ShowMessageBox(Strings.CustomTheme_Add_Errors_NameReserved, MessageBoxImage.Error);
                        break;
                    default:
                        App.Logger.WriteLine(LOG_IDENT, $"Got unhandled PathValidator::ValidationResult {validationResult}");
                        Debug.Assert(false);

                        Frontend.ShowMessageBox(Strings.CustomTheme_Add_Errors_Unknown, MessageBoxImage.Error);
                        break;
                }

                return;
            }

            // better to check for the file instead of the directory so broken themes can be overwritten
            string path = Path.Combine(Paths.CustomThemes, SelectedCustomThemeName, "Theme.xml");
            if (File.Exists(path))
            {
                Frontend.ShowMessageBox(Strings.CustomTheme_Add_Errors_NameTaken, MessageBoxImage.Error);
                return;
            }

            try
            {
                RenameCustomThemeStructure(SelectedCustomTheme, SelectedCustomThemeName);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                Frontend.ShowMessageBox($"Couldn't rename the theme '{SelectedCustomTheme}'. Something is probably holding its files open â€” close the theme editor and any Explorer window in its folder, then try again.", MessageBoxImage.Error);
                return;
            }

            int idx = CustomThemes.IndexOf(SelectedCustomTheme);
            CustomThemes[idx] = SelectedCustomThemeName;

            SelectedCustomThemeIndex = idx;
            OnPropertyChanged(nameof(SelectedCustomThemeIndex));
        }

        private void EditCustomTheme()
        {
            if (SelectedCustomTheme is null)
                return;

            new BootstrapperEditorWindow(SelectedCustomTheme).ShowDialog();
        }

        private void ExportCustomTheme()
        {
            if (SelectedCustomTheme is null)
                return;

            var dialog = new SaveFileDialog
            {
                FileName = $"{SelectedCustomTheme}.zip",
                Filter = $"{Strings.FileTypes_ZipArchive}|*.zip"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                string themeDir = Path.Combine(Paths.CustomThemes, SelectedCustomTheme);

                using (var memStream = new MemoryStream())
                {
                    using (var zipStream = new ZipOutputStream(memStream))
                    {
                        foreach (var filePath in Directory.EnumerateFiles(themeDir, "*.*", SearchOption.AllDirectories))
                        {
                            string relativePath = filePath[(themeDir.Length + 1)..];

                            var entry = new ZipEntry(relativePath);
                            entry.DateTime = DateTime.Now;

                            zipStream.PutNextEntry(entry);

                            using var fileStream = File.OpenRead(filePath);
                            fileStream.CopyTo(zipStream);
                        }

                        zipStream.CloseEntry();
                        zipStream.Finish();
                    }

                    // Buffer the whole archive in memory first, then write in one shot â€” a
                    // failure part-way through never leaves a truncated .zip at the target.
                    File.WriteAllBytes(dialog.FileName, memStream.ToArray());
                }

                Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("AppearanceViewModel::ExportCustomTheme", ex);
                Frontend.ShowMessageBox($"Couldn't export the theme '{SelectedCustomTheme}'. Check you can write to the folder you picked and that the theme's files aren't open somewhere else, then try again.", MessageBoxImage.Error);
            }
        }

        private void PopulateCustomThemes()
        {
            string? selected = App.Settings.Prop.SelectedCustomTheme;

            Directory.CreateDirectory(Paths.CustomThemes);

            foreach (string directory in Directory.GetDirectories(Paths.CustomThemes))
            {
                if (!File.Exists(Path.Combine(directory, "Theme.xml")))
                    continue; // missing the main theme file, ignore

                string name = Path.GetFileName(directory);
                CustomThemes.Add(name);
            }

            if (selected != null)
            {
                int idx = CustomThemes.IndexOf(selected);

                if (idx != -1)
                {
                    SelectedCustomThemeIndex = idx;
                    OnPropertyChanged(nameof(SelectedCustomThemeIndex));
                }
                else
                {
                    SelectedCustomTheme = null;
                }
            }
        }

        public string? SelectedCustomTheme
        {
            get => App.Settings.Prop.SelectedCustomTheme;
            set
            {
                App.Settings.Prop.SelectedCustomTheme = value;

                // The list binds straight to this, so it's the only place that knows the
                // selection moved. Seed the rename box from the current name and let the
                // action buttons re-evaluate IsEnabled â€” without this they stay greyed out
                // no matter what you click.
                SelectedCustomThemeName = value ?? "";

                OnPropertyChanged(nameof(SelectedCustomTheme));
                OnPropertyChanged(nameof(SelectedCustomThemeName));
                OnPropertyChanged(nameof(IsCustomThemeSelected));
            }
        }

        public string SelectedCustomThemeName { get; set; } = "";

        public int SelectedCustomThemeIndex { get; set; }

        public ObservableCollection<string> CustomThemes { get; set; } = new();
        public bool IsCustomThemeSelected => SelectedCustomTheme is not null;
    }
}
