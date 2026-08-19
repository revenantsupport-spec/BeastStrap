using System.Drawing;

namespace BeastStrap.Extensions
{
    static class BootstrapperIconEx
    {
        public static IReadOnlyCollection<BootstrapperIcon> Selections => new BootstrapperIcon[]
        {
            BootstrapperIcon.IconBloxstrap,
            BootstrapperIcon.Icon2022,
            BootstrapperIcon.Icon2019,
            BootstrapperIcon.Icon2017,
            BootstrapperIcon.IconLate2015,
            BootstrapperIcon.IconEarly2015,
            BootstrapperIcon.Icon2011,
            BootstrapperIcon.Icon2008,
            BootstrapperIcon.IconBloxstrapClassic,
            BootstrapperIcon.IconCustom
        };

        // small note on handling icon sizes
        // i'm using multisize icon packs here with sizes 16, 24, 32, 48, 64 and 128
        // use this for generating multisize packs: https://www.aconvert.com/icon/

        public static Icon GetIcon(this BootstrapperIcon icon)
        {
            const string LOG_IDENT = "BootstrapperIconEx::GetIcon";

            // load the custom icon file
            if (icon == BootstrapperIcon.IconCustom)
            {
                Icon? customIcon = null;
                string location = App.Settings.Prop.BootstrapperIconCustomLocation;

                if (String.IsNullOrEmpty(location)) 
                {
                    App.Logger.WriteLine(LOG_IDENT, "Warning: custom icon is not set.");
                }
                else
                {
                    try
                    {
                        customIcon = new Icon(location);
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Failed to load custom icon!");
                        App.Logger.WriteException(LOG_IDENT, ex);
                    }
                }

                return customIcon ?? GetBrandIcon();
            }

            return icon switch
            {
                BootstrapperIcon.IconBloxstrap => GetBrandIcon(),
                BootstrapperIcon.Icon2008 => Properties.Resources.Icon2008,
                BootstrapperIcon.Icon2011 => Properties.Resources.Icon2011,
                BootstrapperIcon.IconEarly2015 => Properties.Resources.IconEarly2015,
                BootstrapperIcon.IconLate2015 => Properties.Resources.IconLate2015,
                BootstrapperIcon.Icon2017 => Properties.Resources.Icon2017,
                BootstrapperIcon.Icon2019 => Properties.Resources.Icon2019,
                BootstrapperIcon.Icon2022 => Properties.Resources.Icon2022,
                BootstrapperIcon.IconBloxstrapClassic => Properties.Resources.IconBloxstrapClassic,
                _ => GetBrandIcon()
            };
        }

        private static Icon? _brandIcon;

        // BeastStrap's own icon as a System.Drawing.Icon, loaded once from the embedded application
        // icon and cached (shared for the app lifetime — callers must not dispose it). Used for the
        // default bootstrapper icon, the system-tray icon, and toast/balloon notifications. The enum
        // member is still named IconBloxstrap for settings back-compat but renders this brand icon.
        // Falls back to the upstream icon only if the embedded icon can't be loaded.
        public static Icon GetBrandIcon()
        {
            if (_brandIcon is not null)
                return _brandIcon;

            try
            {
                var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/BeastStrap.ico"));
                if (info?.Stream is Stream stream)
                    using (stream)
                        _brandIcon = new Icon(stream);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("BootstrapperIconEx::GetBrandIcon", ex);
            }

            return _brandIcon ?? Properties.Resources.IconBloxstrap;
        }
    }
}
