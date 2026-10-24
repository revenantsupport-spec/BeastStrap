using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Mvvm.Contracts;
using Wpf.Ui.Mvvm.Services;

namespace BeastStrap.UI.Elements.Base
{
    public abstract class WpfUiWindow : UiWindow
    {
        private readonly IThemeService _themeService = new ThemeService();

        // BeastStrap brand accent (electric ice-blue), applied app-wide in place of the
        // Windows system accent so every control reads on-brand.
        public static readonly System.Windows.Media.Color BrandAccent =
            System.Windows.Media.Color.FromRgb(0x38, 0xBD, 0xF8);

        // Warnings about a see-through/Acrylic window backdrop from hal's fork: On Windows builds
        // below 22523 WPF-UI refuses Acrylic outright unless AllowsTransparency is set, and
        // AllowsTransparency forces WindowStyle.None, which strips the native frame — taking the OS
        // window drag with it — and makes any region without a background brush stop hit-testing.
        // In-window glass (translucent cards and controls) is unaffected and lives in
        // Utility.ThemeManager, driven by the Frosted glass toggle.
        public WpfUiWindow()
        {
            ApplyTheme();
        }

        // False for a window that never shows a taskbar or title-bar icon, so it never decodes one. Read from this
        // constructor, so an override must be a constant.
        public virtual bool UsesThemeIcon => true;

        public void ApplyTheme()
        {
            const int customThemeIndex = 2; // index for CustomTheme merged dictionary

            // BeastStrap is dark-only, with the palette's accent (electric ice-blue by default) applied
            // app-wide instead of the Windows system accent so the look stays consistent and on-brand.
            _themeService.SetTheme(ThemeType.Dark);
            BeastStrap.Utility.ThemeManager.ApplyFromSettings();

            // Taskbar / alt-tab icon: custom icon, else the creator theme's, else BeastStrap's. Guarded so an
            // icon problem can never stop a window, least of all the crash dialog, from opening.
            try
            {
                if (UsesThemeIcon && BeastStrap.Utility.ThemeManager.ResolveWindowIcon() is System.Windows.Media.ImageSource icon)
                    Icon = icon;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("WpfUiWindow::ApplyTheme", ex);
            }

            var dict = new ResourceDictionary { Source = new Uri("pack://application:,,,/UI/Style/Dark.xaml") };
            Application.Current.Resources.MergedDictionaries[customThemeIndex] = dict;

#if QA_BUILD
            this.BorderBrush = System.Windows.Media.Brushes.Red;
            this.BorderThickness = new Thickness(4);
#endif
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            if (App.Settings.Prop.WPFSoftwareRender || App.LaunchSettings.NoGPUFlag.Active)
            {
                if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
                    hwndSource.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
            }

            base.OnSourceInitialized(e);
        }
    }
}
