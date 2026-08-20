using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;
using ICSharpCode.SharpZipLib.Zip;

using Microsoft.Win32;

using Wpf.Ui.Common.Interfaces;

using BeastStrap.UI.Elements.Settings;
using BeastStrap.UI.Elements.Editor;
using BeastStrap.UI.Elements.Dialogs;

namespace BeastStrap.UI.ViewModels.Settings
{
    public class AppearanceViewModel : NotifyPropertyChangedViewModel, INavigationAware
    {
        private readonly Page _page;

        public ICommand PreviewBootstrapperCommand => new RelayCommand(PreviewBootstrapper);
        public ICommand BrowseCustomRobloxIconLocationCommand => new RelayCommand(BrowseCustomRobloxIconLocation);

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

        private void BrowseCustomRobloxIconLocation()
        {
            var dialog = new OpenFileDialog
            {
                Filter = $"{Strings.Menu_IconFiles}|*.ico"
            };

            if (dialog.ShowDialog() != true)
                return;

            CustomRobloxIconLocation = dialog.FileName;
            OnPropertyChanged(nameof(CustomRobloxIconLocation));
        }

        // v420.46: the Roblox window options (icon / title) only apply while window
        // manipulation is enabled on the Integrations page, so the gating has to be
        // refreshed whenever the user navigates here. See INavigationAware.
        public bool WindowManipulationEnabled => App.Settings.Prop.EnableWindowManipulation;

        public void OnNavigatedTo() => OnPropertyChanged(nameof(WindowManipulationEnabled));

        public void OnNavigatedFrom() { }

        public AppearanceViewModel(Page page)
        {
            _page = page;

            foreach (var entry in RobloxIconEx.Selections)
                RobloxIcons.Add(new RobloxIconEntry { IconType = entry });

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

        // ===== App theming (BeastStrap fork feature) — live-editable brand palette =====
        private BeastStrap.Models.ThemePalette Pal => App.Settings.Prop.Palette;
        private void ApplyTheme() => BeastStrap.Utility.ThemeManager.Apply(App.Settings.Prop.Palette);

        public ICommand ResetThemeCommand => new RelayCommand(ResetTheme);
        public ICommand BrowseAppIconCommand => new RelayCommand(BrowseAppIcon);
        public ICommand ClearAppIconCommand => new RelayCommand(() => AppIconLocation = "");

        public IEnumerable<string> ThemePresets => BeastStrap.Utility.ThemeManager.Presets.Keys.Append("Custom");

        public string SelectedThemePreset
        {
            get => App.Settings.Prop.SelectedThemePreset;
            set
            {
                App.Settings.Prop.SelectedThemePreset = value;
                OnPropertyChanged(nameof(SelectedThemePreset));

                if (BeastStrap.Utility.ThemeManager.Presets.TryGetValue(value, out var preset))
                {
                    App.Settings.Prop.Palette = preset.Clone();
                    ApplyTheme();
                    NotifyColours();
                }
            }
        }

        // Accent drives the gradient start + glow too, so the palette stays cohesive from one colour.
        public string AccentHex
        {
            get => Pal.Accent;
            set { Pal.Accent = value; Pal.GradientStart = value; Pal.Glow = value; MarkCustom(); ApplyTheme(); }
        }
        public string GradientEndHex
        {
            get => Pal.GradientEnd;
            set { Pal.GradientEnd = value; MarkCustom(); ApplyTheme(); }
        }
        public string PurpleHex
        {
            get => Pal.Purple;
            set { Pal.Purple = value; MarkCustom(); ApplyTheme(); }
        }
        public string BackgroundHex
        {
            get => Pal.Background;
            set { Pal.Background = value; MarkCustom(); ApplyTheme(); }
        }
        public string SurfaceHex
        {
            get => Pal.Surface;
            set { Pal.Surface = value; MarkCustom(); ApplyTheme(); }
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
            // Just push the live opacity resource (consumed via DynamicResource) instead of a
            // full ThemeManager.Apply — a full rebuild re-decodes the image and re-applies the
            // accent on every slider tick, which is what made the slider laggy.
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

        // ===== Animated (GIF) background — mirrors the custom wallpaper above =====
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

        public IEnumerable<BeastStrap.Enums.BackgroundFit> GifWallpaperStretches
            => Enum.GetValues(typeof(BeastStrap.Enums.BackgroundFit)).Cast<BeastStrap.Enums.BackgroundFit>();

        public BeastStrap.Enums.BackgroundFit GifWallpaperStretch
        {
            get => App.Settings.Prop.GifWallpaperStretch;
            set { App.Settings.Prop.GifWallpaperStretch = value; ApplyTheme(); }
        }

        // Giphy / direct URL entry. Loaded into the same GifWallpaperLocation setting, so a
        // remote GIF survives restarts exactly like a local file.
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

        // Pushing a resource value is cheap and updates every DynamicResource consumer (the
        // WallpaperBackground / GifBackground controls) instantly — no theme rebuild.
        private static void UpdateOpacityResource(string key, double value)
            => System.Windows.Application.Current.Resources[key] = Math.Clamp(value, 0.1, 1.0);

        private void BrowseGifWallpaper()
        {
            var dialog = new OpenFileDialog { Filter = "Image and GIF files|*.gif;*.png;*.jpg;*.jpeg;*.bmp;*.webp" };
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
            App.Settings.Prop.SelectedThemePreset = "Custom";
            OnPropertyChanged(nameof(SelectedThemePreset));
        }

        private void NotifyColours()
        {
            OnPropertyChanged(nameof(AccentHex));
            OnPropertyChanged(nameof(GradientEndHex));
            OnPropertyChanged(nameof(PurpleHex));
            OnPropertyChanged(nameof(BackgroundHex));
            OnPropertyChanged(nameof(SurfaceHex));
        }

        private void ResetTheme()
        {
            App.Settings.Prop.Palette = new BeastStrap.Models.ThemePalette();
            App.Settings.Prop.SelectedThemePreset = "Default";
            App.Settings.Prop.EnableAurora = true;
            App.Settings.Prop.EnableGlass = true;
            App.Settings.Prop.EnableGlow = true;

            ApplyTheme();
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

        private void ApplyAppIcon()
        {
            try
            {
                string path = App.Settings.Prop.CustomAppIconLocation;

                Uri uri = !string.IsNullOrEmpty(path) && File.Exists(path)
                    ? new Uri(path)
                    : new Uri("pack://application:,,,/BeastStrap.ico");

                var src = new System.Windows.Media.Imaging.BitmapImage();
                src.BeginInit();
                src.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                src.UriSource = uri;
                src.EndInit();
                src.Freeze();

                foreach (Window window in Application.Current.Windows)
                    window.Icon = src;
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

        // ===== Roblox window (v420.46, ported from FishyStrap) =====
        // Custom icon / title for the RUNNING Roblox window. Gated behind
        // EnableWindowManipulation on the Integrations page; only applied by the watcher
        // when that toggle is on.
        public ObservableCollection<RobloxIconEntry> RobloxIcons { get; set; } = new();

        public RobloxIcon RobloxIcon
        {
            get => App.Settings.Prop.RobloxIcon;
            set => App.Settings.Prop.RobloxIcon = value;
        }

        public string WindowTitle
        {
            get => App.Settings.Prop.RobloxTitle;
            set => App.Settings.Prop.RobloxTitle = value;
        }

        public string CustomRobloxIconLocation
        {
            get => App.Settings.Prop.RobloxIconCustomLocation;
            set
            {
                if (String.IsNullOrEmpty(value))
                {
                    if (App.Settings.Prop.RobloxIcon == RobloxIcon.IconCustom)
                        App.Settings.Prop.RobloxIcon = RobloxIcon.IconDefault;
                }
                else
                {
                    App.Settings.Prop.RobloxIcon = RobloxIcon.IconCustom;
                }

                App.Settings.Prop.RobloxIconCustomLocation = value;

                OnPropertyChanged(nameof(RobloxIcon));
                OnPropertyChanged(nameof(RobloxIcons));
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

            try
            {
                DeleteCustomThemeStructure(SelectedCustomTheme);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("AppearanceViewModel::DeleteCustomTheme", ex);
                Frontend.ShowMessageBox(string.Format(Strings.Menu_Appearance_CustomThemes_DeleteFailed, SelectedCustomTheme, ex.Message), MessageBoxImage.Error);
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
                Frontend.ShowMessageBox(string.Format(Strings.Menu_Appearance_CustomThemes_RenameFailed, SelectedCustomTheme, ex.Message), MessageBoxImage.Error);
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

                    // Buffer the whole archive in memory first, then write in one shot — a
                    // failure part-way through never leaves a truncated .zip at the target.
                    File.WriteAllBytes(dialog.FileName, memStream.ToArray());
                }

                Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("AppearanceViewModel::ExportCustomTheme", ex);
                Frontend.ShowMessageBox($"Couldn't export the theme '{SelectedCustomTheme}': {ex.Message}", MessageBoxImage.Error);
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
                // action buttons re-evaluate IsEnabled — without this they stay greyed out
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
