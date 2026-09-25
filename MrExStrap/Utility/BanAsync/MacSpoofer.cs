using System.Net.NetworkInformation;
using System.Security;
using Microsoft.Win32;

namespace BeastStrap.Utility.BanAsync
{
    // What actually happened to a spoof attempt. SpoofAdapter used to answer this with a bool
    // that only meant "netsh exited zero", which is a different question from "did the MAC
    // change" — the driver gets the last word and can keep its own address without complaining.
    public enum MacSpoofOutcome
    {
        // The registry write never happened. Nothing to remember, nothing to revert.
        WriteFailed,

        // The adapter came back reporting the MAC we asked for. This is the only success.
        Applied,

        // The adapter came back and is still reporting a different MAC, so the driver turned the
        // override down. The value is still sitting in the registry and still needs reverting.
        NotApplied,

        // Written, but we could not prove it either way — the adapter didn't come back in time,
        // or it never restarted so nothing has re-read the value yet.
        Unverified
    }

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

        // Writes the override, cycles the adapter, then reads the MAC back and reports what
        // really happened. This used to return whether netsh exited zero, which told the caller
        // nothing about the MAC — the driver quietly keeps its own address whenever it doesn't
        // like the override, and the whole feature reported success through that.
        public static MacSpoofOutcome SpoofAdapter(NetworkAdapter adapter, string newMac, Action<string> log)
        {
            // One normalised string for both the write and the read-back, so what we compare
            // against afterwards is exactly what went into the registry. Windows wants the value
            // with no separators, and a hand-typed "00-11-22-…" would otherwise be written as-is.
            string normalized = NormalizeMacHex(newMac);

            if (!WriteNetworkAddress(adapter, normalized, log))
                return MacSpoofOutcome.WriteFailed;

            // Past this point the override IS in the registry whatever happens next, which is why
            // the caller gets an outcome instead of a bool. It has to remember this adapter even
            // when the spoof didn't take, or Revert and the Persistent=off cleanup on exit will
            // never find the value again.
            bool bounced = RestartAdapter(adapter.Name, log);
            if (!bounced)
                log($"{adapter.Name} didn't cycle cleanly. Checking what it actually reports anyway.");

            return VerifyAdapterMac(adapter, normalized, bounced, log);
        }

        // The registry half on its own. False means nothing was written, so there is no leftover
        // value for the caller to track, revert, or clear on exit.
        private static bool WriteNetworkAddress(NetworkAdapter adapter, string newMac, Action<string> log)
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
                App.Logger.WriteException(LOG_IDENT + "::WriteNetworkAddress::Security", ex);
                return false;
            }
            catch (Exception ex)
            {
                log($"Failed to write MAC for {adapter.Name}: {ex.Message}");
                App.Logger.WriteException(LOG_IDENT + "::WriteNetworkAddress", ex);
                return false;
            }

            // Written. Cycling the adapter and finding out whether the driver took it is
            // SpoofAdapter's job now, because those are the two parts that can fail silently.
            return true;
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

        // Reads the adapter's MAC back after the bounce and says whether the spoof actually took.
        // Windows reports whatever address the driver ended up using, so this is the only thing
        // that can tell a real spoof apart from a write the driver quietly ignored.
        //
        // 'bounced' is whether the adapter really went down and came back. When it didn't, a
        // mismatch proves nothing — the driver has not re-read NetworkAddress yet, so blaming it
        // would be a lie in the other direction and the verdict has to stay Unverified.
        private static MacSpoofOutcome VerifyAdapterMac(NetworkAdapter adapter, string expectedMac, bool bounced, Action<string> log)
        {
            // 15 seconds, not the two or three a wired card needs. An administratively disabled
            // adapter drops out of the interface list completely, and a Wi-Fi card can take most
            // of ten seconds to rebind after the enable. A spoof that worked returns on the first
            // or second poll anyway, so a longer deadline only costs time on the failure path,
            // and a wrong verdict costs far more than a wait. Don't shorten this.
            const int TimeoutMs = 15000;
            const int PollMs = 500;

            log($"Checking what MAC {adapter.Name} actually reports…");

            string? lastSeen = null;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);

            while (true)
            {
                string? current = ReadCurrentMac(adapter.Id);
                if (!string.IsNullOrEmpty(current))
                {
                    lastSeen = current;
                    if (string.Equals(current, expectedMac, StringComparison.OrdinalIgnoreCase))
                    {
                        // A match is a match whether or not netsh was happy, so this deliberately
                        // doesn't look at 'bounced'. What the adapter reports is the truth.
                        log($"Confirmed {adapter.Name} is now {NetworkAdapter.FormatMac(expectedMac)}.");
                        return MacSpoofOutcome.Applied;
                    }
                }

                if (DateTime.UtcNow >= deadline)
                    break;

                Thread.Sleep(PollMs);
            }

            if (lastSeen is null)
            {
                // Never saw the adapter at all in the whole window, so there is no reading to
                // report. Saying it "still reports" something would be inventing one.
                log($"{adapter.Name} never came back within {TimeoutMs / 1000}s, so we couldn't read its MAC. The registry value is written either way — Revert MAC clears it.");
                return MacSpoofOutcome.Unverified;
            }

            if (!bounced)
            {
                log($"{adapter.Name} still reports {NetworkAdapter.FormatMac(lastSeen)}, but it never restarted, so nothing has re-read the new MAC yet. Disable and re-enable it in Network Connections, or reboot.");
                return MacSpoofOutcome.Unverified;
            }

            log($"{adapter.Name} still reports {NetworkAdapter.FormatMac(lastSeen)}. The driver turned down {NetworkAdapter.FormatMac(expectedMac)} and kept its own MAC. The registry value is still there, so use Revert MAC to clear it.");
            return MacSpoofOutcome.NotApplied;
        }

        // Current MAC for one adapter GUID, or null when Windows isn't listing that adapter right
        // now — which is exactly what happens for the whole time it is administratively disabled,
        // so the caller has to keep polling rather than treat a null as an answer. The GUID is the
        // same NetCfgInstanceId this class matches on in the registry, so it doesn't change when
        // the adapter cycles, which makes it a safer key than the friendly name.
        private static string? ReadCurrentMac(string adapterGuid)
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!string.Equals(nic.Id, adapterGuid, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string mac = nic.GetPhysicalAddress().ToString().ToUpperInvariant();
                    return string.IsNullOrEmpty(mac) ? null : mac;
                }
            }
            catch (Exception ex)
            {
                // One failed poll is not a verdict — let the loop try again.
                App.Logger.WriteException(LOG_IDENT + "::ReadCurrentMac", ex);
            }

            return null;
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

                // …then force the locally-administered bit back on, which costs the first octet
                // of the mirror: 00-1B-21 comes out as 02-1B-21 and the other two bytes survive.
                // A real vendor OUI has this bit clear, and that is the specific thing NDIS
                // miniport drivers filter a NetworkAddress override on — they keep the burned-in
                // MAC and say nothing about it, which is how this toggle could report success and
                // change nothing at all.
                bytes[0] = (byte)((bytes[0] & 0xFC) | 0x02);
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

        // True when the MAC has the locally-administered bit set on the first octet, which shows
        // up as a second hex digit of 2, 6, A or E. Drivers that filter NetworkAddress overrides
        // at all tend to filter on exactly this, so it is worth saying before the write rather
        // than leaving the user to work out afterwards why their MAC didn't stick.
        public static bool IsLocallyAdministered(string mac)
        {
            string clean = NormalizeMacHex(mac);
            if (clean.Length != 12)
                return false;

            return byte.TryParse(clean.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte first)
                   && (first & 0x02) != 0;
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
