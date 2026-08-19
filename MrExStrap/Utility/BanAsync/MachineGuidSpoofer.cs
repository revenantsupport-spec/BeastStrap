using System.Security;
using Microsoft.Win32;

namespace BeastStrap.Utility.BanAsync
{
    public static class MachineGuidSpoofer
    {
        private const string LOG_IDENT = "MachineGuidSpoofer";
        private const string KeyPath = @"SOFTWARE\Microsoft\Cryptography";
        private const string ValueName = "MachineGuid";

        public static string? ReadCurrent()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: false);
                return key?.GetValue(ValueName) as string;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::ReadCurrent", ex);
                return null;
            }
        }

        public static bool Apply(string newGuid, Action<string> log)
        {
            if (!Guid.TryParse(newGuid, out _))
            {
                log($"Refusing to write '{newGuid}' — not a valid GUID");
                return false;
            }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
                if (key == null)
                {
                    log("Couldn't open HKLM\\SOFTWARE\\Microsoft\\Cryptography for write. Run as administrator.");
                    return false;
                }

                key.SetValue(ValueName, newGuid, RegistryValueKind.String);
                log($"MachineGuid set to {newGuid}");
                return true;
            }
            catch (SecurityException)
            {
                log("Access denied. Run as administrator.");
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                log("Access denied. Run as administrator.");
                return false;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Apply", ex);
                log($"Couldn't write MachineGuid: {ex.Message}");
                return false;
            }
        }

        public static string GenerateRandom() => Guid.NewGuid().ToString();
    }
}
