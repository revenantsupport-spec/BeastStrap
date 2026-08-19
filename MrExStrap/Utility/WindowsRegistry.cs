using Microsoft.Win32;

namespace BeastStrap.Utility
{
    static class WindowsRegistry
    {
        private const string RobloxPlaceKey = "Roblox.Place";
        
        public static readonly List<RegistryKey> Roots = new() { Registry.CurrentUser, Registry.LocalMachine };

        public static void RegisterProtocol(string key, string name, string handler, string handlerParam = "%1")
        {
            string handlerArgs = $"\"{handler}\" {handlerParam}";

            using var uriKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{key}");
            using var uriIconKey = uriKey.CreateSubKey("DefaultIcon");
            using var uriCommandKey = uriKey.CreateSubKey(@"shell\open\command");

            if (uriKey.GetValue("") is null)
            {
                uriKey.SetValueSafe("", $"URL: {name} Protocol");
                uriKey.SetValueSafe("URL Protocol", "");
            }

            if (uriCommandKey.GetValue("") as string != handlerArgs)
            {
                uriIconKey.SetValueSafe("", handler);
                uriCommandKey.SetValueSafe("", handlerArgs);
            }
        }

        /// <summary>
        /// Registers Roblox Player protocols for BeastStrap
        /// </summary>
        public static void RegisterPlayer() => RegisterPlayer(Paths.Application, "-player \"%1\"");

        public static void RegisterPlayer(string handler, string handlerParam)
        {
            RegisterProtocol("roblox", "Roblox", handler, handlerParam);
            RegisterProtocol("roblox-player", "Roblox", handler, handlerParam);
        }

        /// <summary>
        /// Registers all Roblox Studio classes for BeastStrap
        /// </summary>
        public static void RegisterStudio()
        {
            RegisterStudioProtocol(Paths.Application, "-studio \"%1\"");
            RegisterStudioFileClass(Paths.Application, "-studio \"%1\"");
            RegisterStudioFileTypes();
        }

        /// <summary>
        /// Registers roblox-studio and roblox-studio-auth protocols
        /// </summary>
        /// <param name="handler"></param>
        /// <param name="handlerParam"></param>
        public static void RegisterStudioProtocol(string handler, string handlerParam)
        {
            RegisterProtocol("roblox-studio", "Roblox", handler, handlerParam);
            RegisterProtocol("roblox-studio-auth", "Roblox", handler, handlerParam);
        }

        /// <summary>
        /// Registers file associations for Roblox.Place class
        /// </summary>
        public static void RegisterStudioFileTypes()
        {
            RegisterStudioFileType(".rbxl");
            RegisterStudioFileType(".rbxlx");
        }

        /// <summary>
        /// Registers Roblox.Place class
        /// </summary>
        /// <param name="handler"></param>
        /// <param name="handlerParam"></param>
        public static void RegisterStudioFileClass(string handler, string handlerParam)
        {
            const string keyValue = "Roblox Place";
            string handlerArgs = $"\"{handler}\" {handlerParam}";
            string iconValue = $"{handler},0";

            using RegistryKey uriKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + RobloxPlaceKey);
            using RegistryKey uriIconKey = uriKey.CreateSubKey("DefaultIcon");
            using RegistryKey uriOpenKey = uriKey.CreateSubKey(@"shell\Open");
            using RegistryKey uriCommandKey = uriOpenKey.CreateSubKey(@"command");

            if (uriKey.GetValue("") as string != keyValue)
                uriKey.SetValueSafe("", keyValue);

            if (uriCommandKey.GetValue("") as string != handlerArgs)
                uriCommandKey.SetValueSafe("", handlerArgs);

            if (uriOpenKey.GetValue("") as string != "Open")
                uriOpenKey.SetValueSafe("", "Open");

            if (uriIconKey.GetValue("") as string != iconValue)
                uriIconKey.SetValueSafe("", iconValue);
        }

        public static void RegisterStudioFileType(string key)
        {
            using RegistryKey uriKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{key}");
            uriKey.CreateSubKey(RobloxPlaceKey + @"\ShellNew");

            if (uriKey.GetValue("") as string != RobloxPlaceKey)
                uriKey.SetValueSafe("", RobloxPlaceKey);
        }

        public static void Unregister(string key)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{key}");
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("Protocol::Unregister", $"Failed to unregister {key}: {ex}");
            }
        }

        /// <summary>
        /// Hand the roblox:// / roblox-player:// / roblox-studio:// protocol handlers back to
        /// stock Roblox WITHOUT uninstalling BeastStrap, so the user can temporarily run
        /// normal Roblox or an executor that doesn't support bootstrappers (e.g. Volt).
        ///
        /// Smart restore, mirroring Installer.DoUninstall's protocol block: if a stock Roblox
        /// install is registered, point the protocols at its exe; if not, just unregister
        /// BeastStrap's handlers so the next Roblox/executor installer claims them.
        ///
        /// Fully reversible: the next Roblox launch through BeastStrap calls RegisterPlayer
        /// again (Bootstrapper.cs), so no reinstall is needed to re-hook.
        /// Returns a short human-readable summary of what happened for the UI + log.
        /// </summary>
        public static string ResetToStockRoblox()
        {
            const string LOG_IDENT = "WindowsRegistry::ResetToStockRoblox";
            var summary = new List<string>();

            // ---- Player ----
            try
            {
                using var playerKey = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\roblox-player");
                var playerFolder = playerKey?.GetValue("InstallLocation") as string;

                if (string.IsNullOrEmpty(playerFolder))
                {
                    Unregister("roblox");
                    Unregister("roblox-player");
                    summary.Add("Removed BeastStrap's Roblox player handler (no stock Roblox install found — the next Roblox or executor installer will claim it).");
                    App.Logger.WriteLine(LOG_IDENT, "No stock player install; unregistered roblox + roblox-player.");
                }
                else
                {
                    string playerPath = Path.Combine(playerFolder, "RobloxPlayerBeta.exe");
                    RegisterPlayer(playerPath, "%1");
                    summary.Add($"Pointed the Roblox player handler back at your stock Roblox install ({playerPath}).");
                    App.Logger.WriteLine(LOG_IDENT, $"Restored stock player protocol -> {playerPath}");
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Player", ex);
                summary.Add("Couldn't fully reset the player handler — see the log.");
            }

            // ---- Studio ----
            try
            {
                using var studioKey = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\roblox-studio");
                var studioFolder = studioKey?.GetValue("InstallLocation") as string;

                if (string.IsNullOrEmpty(studioFolder))
                {
                    Unregister("roblox-studio");
                    Unregister("roblox-studio-auth");
                    Unregister("Roblox.Place");
                    Unregister(".rbxl");
                    Unregister(".rbxlx");
                    App.Logger.WriteLine(LOG_IDENT, "No stock studio install; unregistered studio protocols + file types.");
                }
                else
                {
                    string studioPath = Path.Combine(studioFolder, "RobloxStudioBeta.exe");
                    RegisterStudioProtocol(studioPath, "%1");
                    RegisterStudioFileClass(studioPath, "-ide \"%1\"");
                    App.Logger.WriteLine(LOG_IDENT, $"Restored stock studio protocol -> {studioPath}");
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Studio", ex);
            }

            return string.Join("\n\n", summary);
        }
    }
}
