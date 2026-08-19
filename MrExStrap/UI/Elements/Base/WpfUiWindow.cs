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

        public WpfUiWindow()
        {
            ApplyTheme();
        }

        public void ApplyTheme()
        {
            const int customThemeIndex = 2; // index for CustomTheme merged dictionary

            // BeastStrap is dark-only, with the neon-cyan brand accent applied app-wide instead
            // of the Windows system accent so the look stays consistent and on-brand.
            _themeService.SetTheme(ThemeType.Dark);
            BeastStrap.Utility.ThemeManager.ApplyFromSettings();

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
