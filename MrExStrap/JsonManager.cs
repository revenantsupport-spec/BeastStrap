namespace BeastStrap
{
    public class JsonManager<T> where T : class, new()
    {
        protected T _prop = new();

        public virtual T Prop
        {
            get => _prop;
            set => _prop = value;
        }

        /// <summary>
        /// The file hash when last retrieved from disk
        /// </summary>
        public string? LastFileHash { get; private set; }

        public bool Loaded { get; protected set; } = false;

        /// <summary>
        /// True when the last <see cref="Load"/> threw — i.e. a file exists on disk but we could not
        /// read it, so <see cref="Prop"/> holds defaults rather than the user's real data.
        /// </summary>
        /// <remarks>
        /// Anything destructive that keys off the loaded contents MUST check this. Treating "we
        /// failed to read the settings" as "the user has no profiles" is how a locked file turns
        /// into a mass delete — see the parked-profile sweep in Bootstrapper.
        /// </remarks>
        public bool LoadFailed { get; protected set; } = false;

        public virtual string ClassName { get; }

        public virtual string FileName => $"{ClassName}.json";

        public virtual string FileLocation => Path.Combine(Paths.Base, FileName);

        public bool IsSaved => File.Exists(FileLocation);

        public virtual string LOG_IDENT_CLASS => $"JsonManager<{ClassName}>";

        public JsonManager(string? className = null)
        {
            ClassName = string.IsNullOrEmpty(className) ? typeof(T).Name : className;
        }

        public virtual bool Load(bool alertFailure = true)
        {
            string LOG_IDENT = $"{LOG_IDENT_CLASS}::Load";

            App.Logger.WriteLine(LOG_IDENT, $"Loading from {FileLocation}...");

            try
            {
                if (File.Exists(FileLocation))
                {
                    string contents = File.ReadAllText(FileLocation);

                    T? settings = JsonSerializer.Deserialize<T>(contents);

                    if (settings is null)
                        throw new ArgumentNullException("Deserialization returned null");

                    _prop = settings;
                    Loaded = true;
                    LoadFailed = false;
                    LastFileHash = MD5Hash.FromString(contents);

                    App.Logger.WriteLine(LOG_IDENT, "Loaded successfully!");

                    return true;
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Could not find {FileLocation}.");

                    // Reset rather than leaving whatever the last Load() put here. FastFlagManager
                    // re-points FileLocation per profile, so a profile with no file on disk (every
                    // brand-new one) used to keep the PREVIOUS profile's flags in Prop — and the
                    // next Save() then wrote that entire flag set into the new profile's file.
                    _prop = new T();
                    LastFileHash = null;
                    Loaded = true;
                    // Genuinely absent is not a failure — a fresh install has no file yet, and
                    // defaults are the correct contents to carry forward.
                    LoadFailed = false;

                    return false;
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Failed to load!");
                App.Logger.WriteException(LOG_IDENT, ex);

                // Back up unconditionally, NOT just when we're allowed to show a dialog. The silent
                // path is the dangerous one: SaveMerged reloads with alertFailure:false from
                // background processes, so the failures nobody ever sees are exactly the ones that
                // most need a recoverable copy on disk.
                try
                {
                    if (File.Exists(FileLocation))
                        File.Copy(FileLocation, FileLocation + ".bak", true);
                }
                catch (Exception copyEx)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Failed to create backup file: {FileLocation}.bak");
                    App.Logger.WriteException(LOG_IDENT, copyEx);
                }

                if (alertFailure)
                {
                    string message = "";

                    if (ClassName == nameof(Settings))
                        message = Strings.JsonManager_SettingsLoadFailed;
                    else if (ClassName == nameof(FastFlagManager))
                        message = Strings.JsonManager_FastFlagsLoadFailed;

                    if (!String.IsNullOrEmpty(message))
                        Frontend.ShowMessageBox($"{message}\n\n{ex.Message}", System.Windows.MessageBoxImage.Warning);
                }

                // Reset so a failed read can't leave the PREVIOUS file's contents sitting in Prop —
                // FastFlagManager re-points FileLocation per profile, so profile A's flags would
                // otherwise be written into profile B's file on the next save. Same reasoning as the
                // not-found branch above.
                _prop = new T();
                LastFileHash = null;

                // A failed READ must never become a WRITE.
                //
                // This used to be "Loaded = true; Save();", which serialised the in-memory object —
                // still a factory-default instance on a first load — straight over the file it had
                // just failed to read. One transient lock from antivirus, OneDrive, the Search
                // indexer or a concurrent BeastStrap process was enough to reset Settings.json to
                // defaults. That then collapsed the profile list to the seeded built-in, and the
                // parked-profile sweep in Bootstrapper deleted every other profile's install —
                // gigabytes, unrecoverable, from a file that was perfectly intact on disk.
                //
                // It was doubly wrong under SaveMerged, which calls Load() inside the per-file lock
                // and then writes again itself, so the defaults were committed twice.
                //
                // Callers see false and LoadFailed; the process runs on defaults in memory, but
                // nothing is persisted until something legitimately saves.
                LoadFailed = true;
                Loaded = true;

                return false;
            }
        }

        public virtual void Save() => SaveInternal(null);

        /// <summary>
        /// Applies <paramref name="mutate"/> and saves, re-reading the file first if another
        /// process has written it since we last touched it.
        /// </summary>
        /// <remarks>
        /// Plain <see cref="Save"/> writes whatever this process has in memory, which is correct
        /// for the settings UI (the user is looking at it) but wrong for a long-lived background
        /// process. The tray launcher loads Settings once at boot and only reloads when its menu
        /// is opened, so its 30-minute update poll could write a boot-time snapshot straight over
        /// everything the user had changed in the settings window since — silently, because the
        /// per-file lock below serialises writers but does nothing about a lost update.
        ///
        /// Reloading inside the lock closes that: nobody else can write between the re-read and
        /// our own write, so the mutation lands on top of current state instead of replacing it.
        /// Use this for any save that only touches a few bookkeeping fields.
        /// </remarks>
        public void SaveMerged(Action<T> mutate)
        {
            ArgumentNullException.ThrowIfNull(mutate);
            SaveInternal(mutate);
        }

        private void SaveInternal(Action<T>? mutate)
        {
            string LOG_IDENT = $"{LOG_IDENT_CLASS}::Save";

            App.Logger.WriteLine(LOG_IDENT, $"Saving to {FileLocation}...");

            Directory.CreateDirectory(Path.GetDirectoryName(FileLocation)!);

            try
            {
                // The tray, a launching bootstrapper, and the settings UI can all write this
                // file at once (different processes of the same install). Serialize writers on
                // a per-file named mutex so they don't interleave. Best-effort: if we can't get
                // the lock in time we still write, because the atomic swap below prevents a
                // torn file regardless — the lock only avoids a lost update. Inside the try so
                // even a mutex-creation failure hits the graceful handler below.
                using var ipl = new InterProcessLock("Json-" + MD5Hash.FromString(FileLocation), TimeSpan.FromSeconds(3));

                if (mutate is not null)
                {
                    // Only reload when there is actually a file to reload FROM. HasFileOnDiskChanged
                    // also reports true when the file has been DELETED since we read it (a cleanup
                    // tool, the uninstaller), and Load() on a missing file deliberately resets Prop
                    // to a fresh instance. Reloading there would turn a one-field bookkeeping update
                    // into a full wipe of the user's settings or state, written straight back to disk.
                    //
                    // Never alert either. This runs on a background timer in the tray process, where
                    // a modal load-failure box would sit behind everything with nobody to dismiss it.
                    if (HasFileOnDiskChanged() && File.Exists(FileLocation))
                    {
                        App.Logger.WriteLine(LOG_IDENT, "File changed on disk since we last read it — reloading before applying our change.");
                        Load(alertFailure: false);
                    }

                    mutate(Prop);
                }

                string contents = JsonSerializer.Serialize(Prop, new JsonSerializerOptions { WriteIndented = true });

                // Write to a unique temp file, then atomically swap it into place. A crash or
                // a concurrent reader never sees a half-written settings.json — the classic
                // cause of "settings reset themselves" / parse-fail-on-next-launch reports.
                string tempPath = FileLocation + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                try
                {
                    File.WriteAllText(tempPath, contents);
                    File.Move(tempPath, FileLocation, overwrite: true);
                }
                catch
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    throw;
                }

                LastFileHash = MD5Hash.FromString(contents);

                // Reap any leftover temp files from a prior write that was killed between
                // WriteAllText and Move. Self-healing, so no separate startup sweep is needed —
                // orphans can't outlive the next successful save.
                SweepOrphanedTempFiles();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
            {
                App.Logger.WriteLine(LOG_IDENT, "Failed to save");
                App.Logger.WriteException(LOG_IDENT, ex);

                string errorMessage = string.Format(Resources.Strings.Bootstrapper_JsonManagerSaveFailed, ClassName, ex.Message);
                Frontend.ShowMessageBox(errorMessage, System.Windows.MessageBoxImage.Warning);

                return;
            }

            App.Logger.WriteLine(LOG_IDENT, "Save complete!");
        }

        // Deletes stray "<FileName>.<hex>.tmp" siblings left by a Save() that was killed
        // mid-write (between WriteAllText and the atomic Move). Best-effort and quiet — a
        // temp still being written by a concurrent Save is held open and simply skipped.
        private void SweepOrphanedTempFiles()
        {
            try
            {
                string? dir = Path.GetDirectoryName(FileLocation);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    return;

                foreach (string temp in Directory.EnumerateFiles(dir, FileName + ".*.tmp"))
                {
                    try { File.Delete(temp); } catch { /* in use by a live write, or already gone */ }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException($"{LOG_IDENT_CLASS}::SweepOrphanedTempFiles", ex);
            }
        }

        public virtual void Delete()
        {
            string LOG_IDENT = $"{LOG_IDENT_CLASS}::Delete";

            try
            {
                if (File.Exists(FileLocation))
                {
                    File.Delete(FileLocation);

                    Loaded = false;
                    App.Logger.WriteLine(LOG_IDENT, "Delete complete!");
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, "File does not exist on disk");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                App.Logger.WriteLine(LOG_IDENT, "Failed to delete");
                App.Logger.WriteException(LOG_IDENT, ex);

                // should we notify?
            }
        }

        /// <summary>
        /// Hash of <see cref="Prop"/> as it would be serialised right now. Snapshot one of these
        /// and compare later to detect edits without touching the disk.
        /// </summary>
        public string ComputeCurrentHash()
        {
            try
            {
                return MD5Hash.FromString(JsonSerializer.Serialize(Prop, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                App.Logger.WriteException($"{LOG_IDENT_CLASS}::ComputeCurrentHash", ex);
                return "";
            }
        }

        /// <summary>
        /// Is the file on disk different to the one deserialised during this session?
        /// </summary>
        public bool HasFileOnDiskChanged()
        {
            // check if a file has been created since launch
            if (string.IsNullOrEmpty(LastFileHash) && File.Exists(FileLocation))
                return true;

            // No file at all. MD5Hash.FromFile opens it unconditionally, so this used to throw
            // FileNotFoundException straight into the launch path (Bootstrapper's mods pass calls
            // this on PlayerState, which does not exist on a first run). "Changed" here means
            // "the file went away since we read one".
            if (!File.Exists(FileLocation))
                return !string.IsNullOrEmpty(LastFileHash);

            return LastFileHash != MD5Hash.FromFile(FileLocation);
        }
    }

    /// <summary>
    /// <see cref="JsonManager{T}"/> that will automatically load in the JSON if it has not been already
    /// </summary>
    /// <typeparam name="T">Class</typeparam>
    public class LazyJsonManager<T> : JsonManager<T> where T : class, new()
    {
        public override T Prop
        {
            get
            {
                if (!Loaded)
                    Load();

                return _prop;
            }
            set
            {
                _prop = value;
                Loaded = true;
            }
        }

        public LazyJsonManager(string? className)
            : base(className)
        {
        }
    }
}
