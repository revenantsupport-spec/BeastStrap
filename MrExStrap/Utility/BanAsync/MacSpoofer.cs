using System.Net.NetworkInformation;
using System.Security;
using Microsoft.Win32;

namespace BeastStrap.Utility.BanAsync
{
    public static class MacSpoofer
    {
        private const string LOG_IDENT = "MacSpoofer";

        // GUID of the Network class. Every NIC has a subkey under this whose NetCfgInstanceId
        // matches the adapter GUID exposed by NetworkInterface.Id.
        private const string NetworkClassKeyPath =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

        // Keywords in adapter description that mark it as virtual/tunneling and not worth spoofing.
        // Spoofing these usually does nothing useful and can break VPNs.
        //
        // Bluetooth and Wi-Fi Direct (the real ones, not Microsoft's virtual variants) are
        // INTENTIONALLY allowed through — TMAC and other tools show them, users expect to see
        // them, and spoofing a Bluetooth PAN adapter is a legitimate operation. Microsoft's
        // synthetic "Wi-Fi Direct Virtual Adapter" still falls out via the "virtual" keyword.
        private static readonly string[] VirtualKeywords =
        {
            "vpn", "warp", "tailscale", "wireguard", "openvpn",
            "tap-windows", "tap adapter", "tap-",
            "teredo", "isatap", "6to4",
            "miniport", "wan ",
            "virtual", "hyper-v", "vmware", "virtualbox", "vbox", "wsl",
            "loopback", "pseudo", "qemu"
        };

        public static IReadOnlyList<NetworkAdapter> EnumeratePhysicalAdapters()
        {
            var result = new List<NetworkAdapter>();
            NetworkInterface[] all;
            try
            {
                all = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::EnumeratePhysicalAdapters", ex);
                return result;
            }

            var classKey = OpenNetworkClassKey(writable: false);
            string[] subkeyNames = classKey?.GetSubKeyNames() ?? Array.Empty<string>();

            foreach (var nic in all)
            {
                if (nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                    nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 &&
                    nic.NetworkInterfaceType != NetworkInterfaceType.GigabitEthernet)
                    continue;

                if (LooksVirtual(nic.Description) || LooksVirtual(nic.Name))
                    continue;

                string regPath = FindAdapterRegistryPath(classKey, subkeyNames, nic.Id);
                if (string.IsNullOrEmpty(regPath))
                    continue;

                result.Add(new NetworkAdapter
                {
                    Id = nic.Id,
                    Name = nic.Name,
                    Description = nic.Description,
                    PhysicalAddress = nic.GetPhysicalAddress().ToString().ToUpperInvariant(),
                    InterfaceType = nic.NetworkInterfaceType,
                    Status = nic.OperationalStatus,
                    ClassRegistryPath = regPath
                });
            }

            classKey?.Dispose();
            return result;
        }

        public static bool SpoofAdapter(NetworkAdapter adapter, string newMac, Action<string> log)
        {
            if (!IsValidMacHex(newMac))
            {
                log($"Refusing to write invalid MAC '{newMac}' to {adapter.Name}");
                return false;
            }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(adapter.ClassRegistryPath, writable: true);
                if (key == null)
                {
                    log($"Registry path missing for {adapter.Name} ({adapter.ClassRegistryPath})");
                    return false;
                }

                key.SetValue("NetworkAddress", newMac.ToUpperInvariant(), RegistryValueKind.String);
                log($"Wrote NetworkAddress={NetworkAdapter.FormatMac(newMac.ToUpperInvariant())} to {adapter.Name}");
            }
            catch (SecurityException ex)
            {
                log($"Access denied writing to {adapter.Name}. Relaunch as administrator.");
                App.Logger.WriteException(LOG_IDENT + "::SpoofAdapter::Security", ex);
                return false;
            }
            catch (Exception ex)
            {
                log($"Failed to write MAC for {adapter.Name}: {ex.Message}");
                App.Logger.WriteException(LOG_IDENT + "::SpoofAdapter", ex);
                return false;
            }

            return RestartAdapter(adapter.Name, log);
        }

        public static bool RevertAdapter(NetworkAdapter adapter, Action<string> log)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(adapter.ClassRegistryPath, writable: true);
                if (key == null)
                {
                    log($"Registry path missing for {adapter.Name}");
                    return false;
                }

                if (key.GetValue("NetworkAddress") != null)
                {
                    key.DeleteValue("NetworkAddress", throwOnMissingValue: false);
                    log($"Cleared NetworkAddress for {adapter.Name} (will use hardware MAC)");
                }
                else
                {
                    log($"{adapter.Name} was not spoofed via registry — nothing to clear");
                }
            }
            catch (SecurityException ex)
            {
                log($"Access denied reverting {adapter.Name}. Relaunch as administrator.");
                App.Logger.WriteException(LOG_IDENT + "::RevertAdapter::Security", ex);
                return false;
            }
            catch (Exception ex)
            {
                log($"Failed to revert {adapter.Name}: {ex.Message}");
                App.Logger.WriteException(LOG_IDENT + "::RevertAdapter", ex);
                return false;
            }

            return RestartAdapter(adapter.Name, log);
        }

        // Best-effort registry-only revert. Used by the ProcessExit handler when the user
        // has the "Persistent" toggle off — we delete the NetworkAddress value without
        // restarting the adapter (too slow inside ProcessExit's ~3s budget). The spoofed
        // MAC stays active for the current session and the hardware MAC returns on next
        // driver reload or reboot.
        public static void DeleteNetworkAddressByGuid(string adapterGuid)
        {
            try
            {
                using var classKey = Registry.LocalMachine.OpenSubKey(NetworkClassKeyPath, writable: false);
                if (classKey == null) return;

                foreach (string sub in classKey.GetSubKeyNames())
                {
                    if (sub.Length != 4 || !int.TryParse(sub, out _)) continue;

                    try
                    {
                        using var subKey = classKey.OpenSubKey(sub, writable: true);
                        if (subKey == null) continue;

                        string? id = subKey.GetValue("NetCfgInstanceId") as string;
                        if (!string.Equals(id, adapterGuid, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (subKey.GetValue("NetworkAddress") != null)
                        {
                            subKey.DeleteValue("NetworkAddress", throwOnMissingValue: false);
                            App.Logger.WriteLine(LOG_IDENT, $"ProcessExit: cleared NetworkAddress for {adapterGuid}");
                        }
                        return;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteException(LOG_IDENT + "::DeleteNetworkAddressByGuid::SubKey", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::DeleteNetworkAddressByGuid", ex);
            }
        }

        public static bool RestartAdapter(string friendlyName, Action<string> log)
        {
            log($"Bouncing adapter '{friendlyName}'…");
            bool down = RunNetsh($"interface set interface name=\"{friendlyName}\" admin=disabled", log);
            // Brief settle before re-enabling. Some drivers don't like back-to-back transitions.
            Thread.Sleep(500);
            bool up = RunNetsh($"interface set interface name=\"{friendlyName}\" admin=enabled", log);

            if (!down || !up)
            {
                log($"Adapter bounce reported a problem for '{friendlyName}'. The MAC may still apply after a manual disable/enable.");
            }
            return down && up;
        }

        public static void DhcpRefresh(string? friendlyName, Action<string> log)
        {
            string scope = string.IsNullOrEmpty(friendlyName) ? "all adapters" : $"'{friendlyName}'";
            log($"Refreshing DHCP lease for {scope}…");

            string releaseArgs = string.IsNullOrEmpty(friendlyName) ? "/release" : $"/release \"{friendlyName}\"";
            string renewArgs = string.IsNullOrEmpty(friendlyName) ? "/renew" : $"/renew \"{friendlyName}\"";

            RunProcess("ipconfig", releaseArgs, log);
            RunProcess("ipconfig", renewArgs, log);
        }

        public static string GenerateRandomMac(string? ouiToMirror = null)
        {
            var bytes = new byte[6];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);

            if (!string.IsNullOrEmpty(ouiToMirror) && ouiToMirror.Length >= 6)
            {
                // Keep the first 3 bytes (vendor OUI) from the source so the spoof
                // looks like it belongs to the same vendor as the real card.
                bytes[0] = Convert.ToByte(ouiToMirror.Substring(0, 2), 16);
                bytes[1] = Convert.ToByte(ouiToMirror.Substring(2, 2), 16);
                bytes[2] = Convert.ToByte(ouiToMirror.Substring(4, 2), 16);
            }
            else
            {
                // Locally administered, unicast: clear multicast bit, set LAA bit on first byte.
                bytes[0] = (byte)((bytes[0] & 0xFC) | 0x02);
            }

            return BitConverter.ToString(bytes).Replace("-", "").ToUpperInvariant();
        }

        public static bool IsValidMacHex(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return false;
            string clean = mac.Replace("-", "").Replace(":", "").Replace(" ", "");
            if (clean.Length != 12) return false;
            return Regex.IsMatch(clean, "^[0-9A-Fa-f]{12}$");
        }

        public static string NormalizeMacHex(string mac)
        {
            return mac.Replace("-", "").Replace(":", "").Replace(" ", "").ToUpperInvariant();
        }

        private static bool LooksVirtual(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string lower = text.ToLowerInvariant();
            return VirtualKeywords.Any(k => lower.Contains(k));
        }

        private static RegistryKey? OpenNetworkClassKey(bool writable)
        {
            try
            {
                return Registry.LocalMachine.OpenSubKey(NetworkClassKeyPath, writable: writable);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::OpenNetworkClassKey", ex);
                return null;
            }
        }

        private static string FindAdapterRegistryPath(RegistryKey? classKey, string[] subkeyNames, string adapterGuid)
        {
            if (classKey == null) return "";

            foreach (string sub in subkeyNames)
            {
                // Skip the "Properties" / "Configuration" siblings that aren't 4-digit numeric.
                if (sub.Length != 4 || !int.TryParse(sub, out _))
                    continue;

                try
                {
                    using var subKey = classKey.OpenSubKey(sub, writable: false);
                    if (subKey == null) continue;

                    string? netCfgId = subKey.GetValue("NetCfgInstanceId") as string;
                    if (string.IsNullOrEmpty(netCfgId)) continue;

                    if (string.Equals(netCfgId, adapterGuid, StringComparison.OrdinalIgnoreCase))
                        return $"{NetworkClassKeyPath}\\{sub}";
                }
                catch (Exception ex)
                {
                    // Tolerate one bad subkey — keep scanning.
                    App.Logger.WriteException(LOG_IDENT + "::SubKeyScan", ex);
                }
            }

            return "";
        }

        private static bool RunNetsh(string args, Action<string> log) => RunProcess("netsh", args, log);

        private static bool RunProcess(string fileName, string args, Action<string> log)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    log($"Couldn't start {fileName} {args}");
                    return false;
                }

                // Read streams asynchronously while WaitForExit runs — Process.WaitForExit + ReadToEnd
                // can deadlock if the child writes more than the pipe buffer. Reading async avoids that
                // and also lets us bail cleanly on timeout by killing the process so the streams close.
                Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
                Task<string> errTask = proc.StandardError.ReadToEndAsync();

                if (!proc.WaitForExit(15000))
                {
                    try { proc.Kill(); } catch { /* ok if already exited */ }
                    log($"{fileName} {args} timed out after 15s — killed");
                    return false;
                }

                string output = (outTask.Wait(2000) ? outTask.Result : "").Trim();
                string error = (errTask.Wait(2000) ? errTask.Result : "").Trim();

                if (!string.IsNullOrEmpty(output))
                    App.Logger.WriteLine(LOG_IDENT + "::RunProcess", $"{fileName} {args} -> {output}");
                if (!string.IsNullOrEmpty(error))
                    App.Logger.WriteLine(LOG_IDENT + "::RunProcess", $"{fileName} {args} ERR -> {error}");

                if (proc.ExitCode != 0)
                {
                    log($"{fileName} exited {proc.ExitCode}: {(string.IsNullOrEmpty(error) ? output : error)}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::RunProcess", ex);
                log($"Couldn't run {fileName}: {ex.Message}");
                return false;
            }
        }
    }
}
