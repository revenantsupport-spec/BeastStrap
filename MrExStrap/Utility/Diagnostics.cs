using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BeastStrap.Utility
{
    // Collects a one-shot diagnostic blob the user can paste into a support thread.
    // Intentionally minimal — fork version, OS, runtime, pinned state, running Roblox PIDs.
    // No account, cookie, or path-to-home info.
    public static class Diagnostics
    {
        public static async Task<string> BuildAsync()
        {
            var sb = new StringBuilder();
            string portableTag = App.IsPortableMode
                ? (App.IsPortableFastCache ? " (portable, fast-cache)" : " (portable)")
                : "";
            sb.AppendLine($"BeastStrap {App.Version}{portableTag}");
            sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
            sb.AppendLine($"Arch: {RuntimeInformation.OSArchitecture} / Process {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"Locale: {App.Settings.Prop.Locale}");
            sb.AppendLine($"Theme: {App.Settings.Prop.Theme}");
            sb.AppendLine($"Bootstrapper style: {App.Settings.Prop.BootstrapperStyle}");
            sb.AppendLine();

            sb.AppendLine("Pin:");
            if (App.Settings.Prop.UseCustomVersion && !string.IsNullOrEmpty(App.Settings.Prop.CustomVersionGuid))
                sb.AppendLine($"  pinned = {App.Settings.Prop.CustomVersionGuid}");
            else
                sb.AppendLine("  pinned = (none, following LIVE)");

            sb.AppendLine();
            sb.AppendLine("Feature toggles:");
            sb.AppendLine($"  privacy mode = {App.Settings.Prop.EnablePrivacyMode}");
            sb.AppendLine($"  multi-instance = {App.Settings.Prop.MultiInstanceEnabled}");
            sb.AppendLine($"  auto window tiling = {App.Settings.Prop.WindowTilingEnabled} ({App.Settings.Prop.WindowTilingLayout})");
            sb.AppendLine($"  LIVE channel toast = {App.Settings.Prop.ShowLiveChannelToast}");
            sb.AppendLine();

            sb.AppendLine("Current LIVE (live fetch):");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var info = await RobloxDeploymentClient.GetCurrentLiveAsync(cts.Token);
                if (info is null)
                    sb.AppendLine("  (network error)");
                else
                    sb.AppendLine($"  {info.Hash}  (Roblox v{info.Version})");
            }
            catch
            {
                sb.AppendLine("  (fetch failed)");
            }
            sb.AppendLine();

            sb.AppendLine("Running RobloxPlayerBeta processes:");
            try
            {
                var procs = Process.GetProcessesByName("RobloxPlayerBeta");
                if (procs.Length == 0)
                {
                    sb.AppendLine("  (none)");
                }
                else
                {
                    foreach (var p in procs)
                    {
                        try
                        {
                            var uptime = DateTime.Now - p.StartTime;
                            sb.AppendLine($"  PID {p.Id} — up {FormatUptime(uptime)} — mem {p.WorkingSet64 / 1024 / 1024} MB");
                        }
                        catch
                        {
                            sb.AppendLine($"  PID {p.Id} — (limited info)");
                        }
                        finally
                        {
                            p.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  (enumerate failed: {ex.Message})");
            }

            return sb.ToString();
        }

        private static string FormatUptime(TimeSpan t)
        {
            if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
            if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
            return $"{(int)t.TotalSeconds}s";
        }
    }
}
