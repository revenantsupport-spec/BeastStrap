namespace BeastStrap
{
    // https://stackoverflow.com/a/53873141/11852173

    public class Logger
    {
        // One lock guards both the on-disk write and the in-memory History, so log lines are
        // written in order, History is never corrupted by a concurrent Add, and a snapshot
        // (AsDocument) never trips over a mid-write. Writes are synchronous and flushed per
        // line: log volume is low (a launch is a few dozen lines) and a complete, current
        // on-disk log is what actually matters when diagnosing a crash.
        private readonly object _sync = new();
        private FileStream? _filestream;

        public readonly List<string> History = new();
        public bool Initialized = false;
        public bool NoWriteMode = false;
        public string? FileLocation;

        public string AsDocument
        {
            get { lock (_sync) return String.Join('\n', History); }
        }

        public void Initialize(bool useTempDir = false)
        {
            const string LOG_IDENT = "Logger::Initialize";

            string directory = useTempDir ? Path.Combine(Paths.TempLogs) : Path.Combine(Paths.Base, "Logs");

            // Milliseconds AND the PID. The name used to be second-resolution only, and because a
            // collision leaves Initialized false, App.OnStartup treats that as "possible duplicate
            // launch" and kills the process — so any two BeastStrap processes that started in the
            // same UTC second cost one of them its life. Multi Instance spawns launchers and
            // watchers at arbitrary times, and AccountLauncher's minimum 2-second bulk delay exists
            // solely to work around this. The PID makes a collision impossible rather than unlikely.
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'");
            string filename = $"{App.ProjectName}_{timestamp}_{Environment.ProcessId}.log";
            string location = Path.Combine(directory, filename);

            WriteLine(LOG_IDENT, $"Initializing at {location}");

            if (Initialized)
            {
                WriteLine(LOG_IDENT, "Failed to initialize because logger is already initialized");
                return;
            }

            Directory.CreateDirectory(directory);

            if (File.Exists(location))
            {
                WriteLine(LOG_IDENT, "Failed to initialize because log file already exists");
                return;
            }

            try
            {
                _filestream = File.Open(location, FileMode.Create, FileAccess.Write, FileShare.Read);
            }
            catch (IOException)
            {
                WriteLine(LOG_IDENT, "Failed to initialize because log file already exists");
                return;
            }
            catch (UnauthorizedAccessException)
            {
                if (NoWriteMode)
                    return;

                WriteLine(LOG_IDENT, $"Failed to initialize because BeastStrap cannot write to {directory}");

                Frontend.ShowMessageBox(
                    String.Format(Strings.Logger_NoWriteMode, directory), 
                    System.Windows.MessageBoxImage.Warning, 
                    System.Windows.MessageBoxButton.OK
                );

                NoWriteMode = true;

                return;
            }
            

            // Flip Initialized and flush the buffered History under the same lock. If this were
            // set outside the lock, a WriteLine racing in this window would write its line
            // individually AND have it re-written in the bulk flush below — a duplicated line.
            lock (_sync)
            {
                Initialized = true;

                // Flush anything logged before the file existed (buffered in History) in one block.
                if (History.Count > 0)
                    WriteToLogLocked(string.Join("\r\n", History));
            }

            WriteLine(LOG_IDENT, "Finished initializing!");

            FileLocation = location;

            // delete older logs if there are more than 15
            if (Paths.Initialized && Directory.Exists(Paths.Logs))
            {
                const int maxLogs = 15;
                FileInfo[] logs = new DirectoryInfo(Paths.Logs).GetFiles();

                if (logs.Length <= maxLogs)
                    return;

                foreach (FileInfo log in logs.OrderByDescending(log => log.LastWriteTimeUtc).Skip(maxLogs))
                {
                    WriteLine(LOG_IDENT, $"Cleaning up old log file '{log.Name}'");

                    try
                    {
                       log.Delete();
                    }
                    catch (Exception ex)
                    {
                        WriteLine(LOG_IDENT, "Failed to delete log!");
                        WriteException(LOG_IDENT, ex);
                    }
                }
            }
        }

        private void WriteLine(string message)
        {
            string timestamp = DateTime.UtcNow.ToString("s") + "Z";
            string outcon = $"{timestamp} {message}";
            string outlog = outcon.Replace(Paths.UserProfile, "%UserProfile%", StringComparison.InvariantCultureIgnoreCase);

            Debug.WriteLine(outcon);

            // Add to History and write to disk atomically so the two never interleave and the
            // on-disk order matches History exactly.
            lock (_sync)
            {
                History.Add(outlog);
                WriteToLogLocked(outlog);
            }
        }

        public void WriteLine(string identifier, string message) => WriteLine($"[{identifier}] {message}");

        public void WriteException(string identifier, Exception ex)
        {
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

            string hresult = "0x" + ex.HResult.ToString("X8");

            WriteLine($"[{identifier}] ({hresult}) {ex}");

            Thread.CurrentThread.CurrentUICulture = Locale.CurrentCulture;
        }

        // Caller must hold _sync. Writes the line and flushes it straight to the OS so the
        // on-disk log is complete and current even if the process dies right after — the whole
        // point of a crash log. A failed write is swallowed (logged to the debugger only): the
        // logger must never take the app down, and it can't recurse into itself to report.
        private void WriteToLogLocked(string message)
        {
            if (!Initialized || _filestream is null)
                return;

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes($"{message}\r\n");
                _filestream.Write(bytes, 0, bytes.Length);
                _filestream.Flush();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Logger] Failed to write to log: {ex.Message}");
            }
        }
    }
}
