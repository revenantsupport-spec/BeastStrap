using BeastStrap.AppData;
using System.ComponentModel;

namespace BeastStrap
{
    static class Utilities
    {
        /// <summary>
        /// Puts <paramref name="text"/> on the clipboard, returning false instead of throwing when
        /// Windows won't hand it over.
        /// </summary>
        /// <remarks>
        /// The clipboard is a single machine-wide resource and any process can hold it open.
        /// Clipboard.SetDataObject throws COMException (CLIPBRD_E_CANT_OPEN) when one does, which
        /// is common enough with clipboard managers and remote-desktop tools running. Two of the
        /// call sites live in the tray/watcher process where that took the whole process down.
        /// </remarks>
        public static bool TrySetClipboardText(string? text, string logIdent)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            try
            {
                System.Windows.Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(logIdent, "Couldn't write to the clipboard — something else is holding it open.");
                App.Logger.WriteException(logIdent, ex);
                return false;
            }
        }

        public static void ShellExecute(string website)
        {
            try
            {
                Process.Start(new ProcessStartInfo 
                { 
                    FileName = website, 
                    UseShellExecute = true 
                });
            }
            catch (Win32Exception ex)
            {
                // lmfao

                if (ex.NativeErrorCode != (int)ErrorCode.CO_E_APPNOTFOUND)
                    throw;

                Process.Start(new ProcessStartInfo
                {
                    FileName = "rundll32.exe",
                    Arguments = $"shell32,OpenAs_RunDLL {website}"
                });
            }
        }

        public static Version GetVersionFromString(string version)
        {
            if (version.StartsWith('v'))
                version = version[1..];

            int idx = version.IndexOf('+'); // commit info
            if (idx != -1)
                version = version[..idx];

            // BeastStrap fork uses single-number versioning (420, 420.1, 420.2...).
            // System.Version requires at least major.minor — pad single-segment values with ".0".
            if (!version.Contains('.'))
                version += ".0";

            var v = new Version(version);

            // Trailing zero segments are hidden from the displayed version (App.FormatAssemblyVersion
            // shows "420.49" for 420.49.0). System.Version treats a missing segment as -1, so
            // "420.49" compared to a "v420.49.0" tag came out LessThan — every launch decided there
            // was an update, re-installed it, and prompted again forever. Normalize to the same
            // trimmed form so "420.49" == "420.49.0" == "420.49.0.0".
            if (v.Build > 0)
                return new Version(v.Major, v.Minor, v.Build);
            if (v.Minor > 0)
                return new Version(v.Major, v.Minor);
            return new Version(v.Major);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="versionStr1"></param>
        /// <param name="versionStr2"></param>
        /// <returns>
        /// Result of System.Version.CompareTo <br />
        /// -1: version1 &lt; version2 <br />
        ///  0: version1 == version2 <br />
        ///  1: version1 &gt; version2
        /// </returns>
        public static VersionComparison CompareVersions(string versionStr1, string versionStr2)
        {
            try
            {
                var version1 = GetVersionFromString(versionStr1);
                var version2 = GetVersionFromString(versionStr2);

                return (VersionComparison)version1.CompareTo(version2);
            }
            catch (Exception)
            {
                // temporary diagnostic log for the issue described here:
                // https://github.com/bloxstraplabs/bloxstrap/issues/3193
                // the problem is that this happens only on upgrade, so my only hope of catching this is bug reports following the next release

                App.Logger.WriteLine("Utilities::CompareVersions", "An exception occurred when comparing versions");
                App.Logger.WriteLine("Utilities::CompareVersions", $"versionStr1={versionStr1} versionStr2={versionStr2}");

                throw;
            }
        }

        /// <summary>
        /// Parses the input version string and prints if fails
        /// </summary>
        public static Version? ParseVersionSafe(string versionStr)
        {
            const string LOG_IDENT = "Utilities::ParseVersionSafe";

            if (!Version.TryParse(versionStr, out Version? version))
            {
                App.Logger.WriteLine(LOG_IDENT, $"Failed to convert {versionStr} to a valid Version type.");
                return version;
            }

            return version;
        }

        public static string GetRobloxVersionStr(IAppData data)
        {
            string playerLocation = data.ExecutablePath;

            if (!File.Exists(playerLocation))
                return "";

            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(playerLocation);

            if (versionInfo.ProductVersion is null)
                return "";

            return versionInfo.ProductVersion.Replace(", ", ".");
        }

        public static string GetRobloxVersionStr(bool studio)
        {
            IAppData data = studio ? new RobloxStudioData() : new RobloxPlayerData();

            return GetRobloxVersionStr(data);
        }

        public static Version? GetRobloxVersion(IAppData data)
        {
            string str = GetRobloxVersionStr(data);
            return ParseVersionSafe(str);
        }

        public static Process[] GetProcessesSafe()
        {
            const string LOG_IDENT = "Utilities::GetProcessesSafe";

            try
            {
                return Process.GetProcesses();
            }
            catch (ArithmeticException ex) // thanks microsoft
            {
                App.Logger.WriteLine(LOG_IDENT, $"Unable to fetch processes!");
                App.Logger.WriteException(LOG_IDENT, ex);
                return Array.Empty<Process>(); // can we retry?
            }
        }

        public static bool DoesMutexExist(string name)
        {
            try
            {
                Mutex.OpenExisting(name).Close();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Signals the background updater to stop and waits for it to actually go away.
        /// Returns true when it's gone, false if it outlived <paramref name="timeout"/>.
        /// </summary>
        /// <remarks>
        /// This used to Set() the event and return immediately, and the caller then assumed the
        /// other process was dead and went straight into UpgradeRoblox. It isn't dead — it takes
        /// the signal, calls Bootstrapper.Cancel(), and Cancel() recursively deletes the version
        /// directory. Both processes compute the same directory, so the foreground install and the
        /// background cleanup ran over each other. Waiting for the mutex to disappear is the
        /// difference between "asked it to stop" and "it stopped".
        /// </remarks>
        public static bool KillBackgroundUpdater(string mutexName, TimeSpan timeout)
        {
            const string LOG_IDENT = "Utilities::KillBackgroundUpdater";

            // Held open for the whole wait: disposing it immediately can destroy the event before
            // the target ever opens it.
            using EventWaitHandle handle = new EventWaitHandle(false, EventResetMode.AutoReset, "BeastStrap-BackgroundUpdaterKillEvent");
            handle.Set();

            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                if (!DoesMutexExist(mutexName))
                {
                    App.Logger.WriteLine(LOG_IDENT, "Background updater has exited.");
                    return true;
                }

                Thread.Sleep(200);
            }

            App.Logger.WriteLine(LOG_IDENT, $"Background updater was still running after {timeout.TotalSeconds:F0}s.");
            return false;
        }
    }
}
