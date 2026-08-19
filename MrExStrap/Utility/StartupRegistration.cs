using Microsoft.Win32;

namespace BeastStrap.Utility
{
    // v420.28: HKCU\...\Run registration for the system tray launcher. Per-user
    // (HKCU) so no admin is required, and easy to remove via Task Manager →
    // Startup or by toggling the Versions Manager setting back off. The value
    // points at the current BeastStrap.exe with a -tray flag so the App
    // wakes up in tray mode instead of opening the menu.
    public static class StartupRegistration
    {
        private const string LOG_IDENT = "StartupRegistration";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "BeastStrap";

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                if (key == null)
                    return false;
                var value = key.GetValue(ValueName) as string;
                return !string.IsNullOrEmpty(value);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::IsEnabled", ex);
                return false;
            }
        }

        public static bool Enable()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                if (key == null)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Couldn't open {RunKeyPath} for write.");
                    return false;
                }

                // Paths.Application (the installed location), not Paths.Process (whatever exe is
                // running). After an upgrade the settings window still runs from the downloaded
                // copy in the same process, so enabling "start with Windows" there used to
                // register a path in Downloads that disappears the moment the user tidies up —
                // and IsEnabled() only checks for a non-empty value, so it kept reporting ON.
                // In portable mode the two are the same path, so no special case is needed.
                string target = string.IsNullOrEmpty(Paths.Application) ? Paths.Process : Paths.Application;
                string value = $"\"{target}\" -tray";
                key.SetValue(ValueName, value, RegistryValueKind.String);
                App.Logger.WriteLine(LOG_IDENT, $"Registered startup entry: {value}");
                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Enable", ex);
                return false;
            }
        }

        public static bool Disable()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key == null)
                    return true; // nothing to delete
                if (key.GetValue(ValueName) == null)
                    return true;
                key.DeleteValue(ValueName);
                App.Logger.WriteLine(LOG_IDENT, $"Removed startup entry {ValueName}.");
                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Disable", ex);
                return false;
            }
        }
    }
}
