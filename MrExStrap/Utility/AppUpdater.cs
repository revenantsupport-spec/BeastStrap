using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

namespace BeastStrap.Utility
{
    // Shared auto-update helpers used by both the Roblox-launch path (Bootstrapper.CheckForUpdates)
    // and the menu-open path (LaunchHandler.LaunchMenu). Keeps the download + relaunch contract
    // in one place so the two callers can't drift apart.
    public static class AppUpdater
    {
        private const string LOG_IDENT = "AppUpdater";

        // Reported as (bytes downloaded so far, total bytes if known else 0).
        public readonly record struct DownloadProgress(long Downloaded, long Total);

        // Outcome of an auto-update attempt. On success Started is true and Reason is null.
        // On failure Started is false and Reason carries a human-readable explanation that the
        // caller can drop straight into a user-facing dialog. The reason is also written to
        // App.Logger so a support log already contains the same string the user saw.
        public readonly record struct UpgradeResult(bool Started, string? Reason)
        {
            public static UpgradeResult Success() => new(true, null);
            public static UpgradeResult Fail(string reason) => new(false, reason);
        }

        // Common gate. Returns false if the auto-update path is disabled for this session:
        //   - the user turned off CheckForUpdates,
        //   - we're already mid-upgrade (-upgrade flag passed),
        //   - we're in portable mode (the user updates by re-downloading the portable folder),
        //   - or there's another BeastStrap instance running (likely a background watcher).
        // Keeps the update-handoff mutex alive until this process exits. See the
        // acquisition site in UpgradeAsync for the full protocol.
        private static InterProcessLock? _upgradeHandoffLock;

        // Note this gates the auto-REPLACE, not the check — see Bootstrapper.CheckForUpdates for
        // why the two were split. Every branch logs, because a silent "no" here used to be
        // indistinguishable from "you're up to date".
        public static bool IsAutoUpdateEligible()
        {
            if (!App.Settings.Prop.CheckForUpdates)
            {
                App.Logger.WriteLine(LOG_IDENT, "Auto-update skipped: the user turned update checks off.");
                return false;
            }
            if (App.LaunchSettings.UpgradeFlag.Active)
                return false; // already mid-upgrade, nothing to report
            if (App.IsPortableMode)
            {
                App.Logger.WriteLine(LOG_IDENT, "Auto-update skipped: portable mode updates by re-downloading the folder.");
                return false;
            }
            if (Process.GetProcessesByName(App.ProjectName).Length > 1)
            {
                App.Logger.WriteLine(LOG_IDENT, "Auto-update skipped: another BeastStrap process has the exe open.");
                return false;
            }
            return true;
        }

        // Pick the installer exe from a release's assets. Built explicitly because GitHub returns
        // assets in upload order, and historically the portable zip used to sit ahead of the exe —
        // grabbing Assets[0] would download the wrong file.
        public static GithubReleaseAsset? PickExeAsset(GithubRelease release) =>
            release.Assets?.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        // Downloads the .exe asset of the release to Paths.TempUpdates and starts it with the
        // given args ("-upgrade" is prepended automatically). On success the new process is
        // launched and the caller MUST exit so it can take over. On failure the result carries
        // a human-readable Reason that callers can drop straight into a user dialog.
        public static async Task<UpgradeResult> DownloadAndRelaunchAsync(
            GithubRelease release,
            IEnumerable<string> extraArgs,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            string downloadLocation = "";
            try
            {
                var asset = PickExeAsset(release);
                if (asset is null)
                {
                    string reason = $"The {release.TagName} release has no .exe asset attached. Grab the installer manually from the releases page.";
                    App.Logger.WriteLine(LOG_IDENT, reason);
                    return UpgradeResult.Fail(reason);
                }

                downloadLocation = Path.Combine(Paths.TempUpdates, asset.Name);

                try
                {
                    Directory.CreateDirectory(Paths.TempUpdates);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT + "::CreateTempDir", ex);
                    return UpgradeResult.Fail($"Couldn't create the update folder at {Paths.TempUpdates}. {ex.GetType().Name}: {ex.Message}");
                }

                // Always truncate. The previous behaviour was to skip when the file existed,
                // which silently ran a partial exe if a previous download was interrupted.
                if (File.Exists(downloadLocation))
                {
                    try { File.Delete(downloadLocation); }
                    catch (Exception ex) { App.Logger.WriteException(LOG_IDENT + "::PreDelete", ex); }
                }

                App.Logger.WriteLine(LOG_IDENT, $"Downloading {release.TagName} from {asset.BrowserDownloadUrl}");

                using var response = await App.HttpClient.GetAsync(
                    asset.BrowserDownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    int code = (int)response.StatusCode;
                    string reason = code switch
                    {
                        403 => $"The update server returned 403 (forbidden) downloading {asset.Name}. Your network or a firewall may be blocking the request.",
                        404 => $"The update server returned 404 (not found) downloading {asset.Name}. The release may have been deleted or renamed.",
                        429 => $"The update server returned 429 (rate limited) downloading {asset.Name}. Wait a minute and try again.",
                        >= 500 and < 600 => $"The update server returned {code} downloading {asset.Name}. It's having issues — try again shortly.",
                        _ => $"The update server returned HTTP {code} downloading {asset.Name}."
                    };
                    App.Logger.WriteLine(LOG_IDENT, reason);
                    return UpgradeResult.Fail(reason);
                }

                long total = response.Content.Headers.ContentLength ?? 0;
                progress?.Report(new DownloadProgress(0, total));

                long totalRead = 0;

                // IMPORTANT: write the download inside its own scope so the FileStream is
                // disposed BEFORE the Process.Start call below. Without this scope the
                // `await using var fileStream` lives until the end of the try block, so
                // when we try to launch the freshly-written exe, Windows refuses with
                // ERROR_SHARING_VIOLATION (Win32 32) because our handle is still open and
                // FileStream's default sharing mode is None. Took out v420.15→v420.16
                // auto-updates until this scoping fix landed in v420.17.
                {
                    await using var fileStream = new FileStream(downloadLocation, FileMode.Create, FileAccess.Write);
                    await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

                    byte[] buffer = new byte[81920];
                    int lastReportedPercent = -1;

                    while (true)
                    {
                        int read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                        if (read == 0)
                            break;

                        await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        totalRead += read;

                        if (progress != null)
                        {
                            // Throttle: only report when the integer percent changes (or every chunk if
                            // total is unknown). Avoids hammering the UI dispatcher on a fast LAN.
                            if (total > 0)
                            {
                                int pct = (int)(totalRead * 100 / total);
                                if (pct != lastReportedPercent)
                                {
                                    progress.Report(new DownloadProgress(totalRead, total));
                                    lastReportedPercent = pct;
                                }
                            }
                            else
                            {
                                progress.Report(new DownloadProgress(totalRead, 0));
                            }
                        }
                    }
                } // <-- fileStream + contentStream are now disposed, handle released

                // Verify the downloaded bytes match the advertised Content-Length. A partial
                // response that ends cleanly (bad proxy, dropped connection that the stream
                // didn't surface as an error) would otherwise hand off a truncated exe and
                // we'd App.Terminate this process before the new one fails to start.
                if (total > 0 && totalRead != total)
                {
                    string reason =
                        $"Download was interrupted — got {totalRead:N0} bytes, expected {total:N0}. " +
                        "Your connection may have dropped mid-download. Try again on a stable network.";
                    App.Logger.WriteLine(LOG_IDENT, $"Download size mismatch for {asset.Name}: expected {total}, got {totalRead}. Aborting relaunch.");
                    try { File.Delete(downloadLocation); } catch { /* leave the truncated file, next attempt will overwrite */ }
                    return UpgradeResult.Fail(reason);
                }

                App.Logger.WriteLine(LOG_IDENT, $"Starting {release.TagName} from {downloadLocation}");

                var psi = new ProcessStartInfo { FileName = downloadLocation };
                psi.ArgumentList.Add("-upgrade");
                foreach (var arg in extraArgs)
                    psi.ArgumentList.Add(arg);

                // Persist settings before we hand off so the new process sees the latest state.
                App.Settings.Save();

                // Handoff lock: the new exe's Installer.HandleUpgrade waits on "AutoUpdater"
                // (5s) before copying itself over the installed exe. We hold the mutex until
                // this process exits — the OS abandons it then, which is exactly the "old exe
                // is gone" signal the waiter needs. Rooted in a static so the GC can't close
                // the handle early and defeat the wait. Deliberately never disposed.
                _upgradeHandoffLock = new InterProcessLock("AutoUpdater");

                Process.Start(psi);
                return UpgradeResult.Success();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return UpgradeResult.Fail("Update was cancelled.");
            }
            catch (TaskCanceledException ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Timeout", ex);
                return UpgradeResult.Fail("Download timed out after 30 seconds. Check your connection and try again.");
            }
            catch (HttpRequestException ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Http", ex);
                return UpgradeResult.Fail(ClassifyHttpFailure(ex));
            }
            catch (IOException ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::IO", ex);
                long? freeBytes = TryGetFreeDiskSpace(downloadLocation);
                string diskHint = freeBytes is not null and < 250 * 1024 * 1024
                    ? $" Only {freeBytes / (1024 * 1024)} MB free on the destination drive — the installer is ~170 MB. Clear some space and retry."
                    : " Check disk space and whether antivirus is blocking writes to TempUpdates.";
                return UpgradeResult.Fail($"Couldn't write the installer to disk. {ex.GetType().Name}: {ex.Message}.{diskHint}");
            }
            catch (UnauthorizedAccessException ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Access", ex);
                return UpgradeResult.Fail($"Access denied writing to {Paths.TempUpdates}. Antivirus or folder permissions may be blocking it.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::DownloadAndRelaunchAsync", ex);
                return UpgradeResult.Fail($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // Map common transport failures to language pointing at the cause. Mirrors the
        // classification we do in WeaoClient for consistency.
        private static string ClassifyHttpFailure(HttpRequestException ex)
        {
            var inner = ex.InnerException;
            if (inner is AuthenticationException)
            {
                return "TLS handshake with the update server failed. Antivirus HTTPS inspection or missing Windows TLS updates is the usual culprit.";
            }

            // TLS stream corrupted mid-flight (IOException "...corrupted frame...", wrapped as
            // "The SSL connection could not be established") — a middlebox rewriting the connection,
            // i.e. antivirus HTTPS/SSL scanning or a filtering proxy/VPN. Mirrors WeaoClient so a
            // failed self-update points at the real cause instead of a vague network error.
            if (IsTlsStreamCorruption(ex))
            {
                return "The secure connection to the update server was corrupted before it finished (a TLS frame came back malformed). " +
                       "This is almost always antivirus HTTPS/SSL scanning, or a filtering proxy or VPN. " +
                       "Add BeastStrap to your antivirus's exclusions or turn off its HTTPS/SSL scanning, then try again.";
            }

            if (inner is SocketException sock)
            {
                return sock.SocketErrorCode switch
                {
                    SocketError.HostNotFound =>
                        "Couldn't resolve github.com. Your DNS server may be blocking it — try switching DNS to 1.1.1.1 or 8.8.8.8.",
                    SocketError.ConnectionRefused or SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                        "Couldn't reach the update server. A firewall or VPN may be blocking outbound HTTPS to github.com.",
                    SocketError.TimedOut =>
                        "Connection to the update server timed out. Network is slow or being filtered silently.",
                    _ => $"Network error contacting the update server (socket: {sock.SocketErrorCode}). Check your connection and retry."
                };
            }
            string msg = (inner?.Message ?? ex.Message).Trim();
            return string.IsNullOrEmpty(msg)
                ? "Network error contacting the update server."
                : $"Network error contacting the update server: {msg}.";
        }

        // Walk the inner-exception chain for the signature of a corrupted TLS stream (AV HTTPS
        // inspection / filtering proxy). SslStream surfaces it as an IOException mentioning the
        // TLS "frame"; HttpClient wraps it as "The SSL connection could not be established".
        // A clean handshake/cert failure is an AuthenticationException, handled before this.
        private static bool IsTlsStreamCorruption(Exception ex)
        {
            for (Exception? e = ex; e is not null; e = e.InnerException)
            {
                if (e is IOException && e.Message.Contains("frame", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (e.Message.Contains("SSL connection could not be established", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static long? TryGetFreeDiskSpace(string anyPathOnDrive)
        {
            try
            {
                if (string.IsNullOrEmpty(anyPathOnDrive))
                    return null;
                string? root = Path.GetPathRoot(Path.GetFullPath(anyPathOnDrive));
                if (string.IsNullOrEmpty(root))
                    return null;
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch
            {
                return null;
            }
        }
    }
}
