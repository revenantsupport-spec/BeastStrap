using System.Windows;

using BeastStrap.Models.SettingTasks.Base;

namespace BeastStrap.Models.SettingTasks
{
    public class EmojiModPresetTask : EnumBaseTask<EmojiType>
    {
        private string _filePath => Path.Combine(Paths.Modifications, @"content\fonts\TwemojiMozilla.ttf");

        private IEnumerable<KeyValuePair<EmojiType, string>>? QueryCurrentValue()
        {
            if (!File.Exists(_filePath))
                return null;

            using var fileStream = File.OpenRead(_filePath);
            string hash = MD5Hash.Stringify(App.MD5Provider.ComputeHash(fileStream));

            return EmojiTypeEx.Hashes.Where(x => x.Value == hash);
        }

        public EmojiModPresetTask() : base("ModPreset", "EmojiFont")
        {
            var query = QueryCurrentValue();

            if (query is not null)
                OriginalState = query.FirstOrDefault().Key;
        }

        public override async void Execute()
        {
            const string LOG_IDENT = "EmojiModPresetTask::Execute";

            var query = QueryCurrentValue();

            if (NewState != EmojiType.Default && (query is null || query.FirstOrDefault().Key != NewState))
            {
                // Download to a temp file and swap it in only once it's complete. Writing straight
                // into _filePath with FileMode.Create truncated the user's existing emoji font the
                // instant the request started, so any mid-stream failure left a corrupt font in
                // the mods folder — and the cleanup branch below could never remove it, because a
                // half-written file matches no known hash and QueryCurrentValue returns an empty
                // (not null) sequence.
                string tempPath = _filePath + ".tmp";

                try
                {
                    using var response = await App.HttpClient.GetAsync(NewState.GetUrl());

                    response.EnsureSuccessStatusCode();

                    Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

                    await using (var fileStream = new FileStream(tempPath, FileMode.Create))
                        await response.Content.CopyToAsync(fileStream);

                    Filesystem.AssertReadOnly(_filePath);
                    File.Move(tempPath, _filePath, overwrite: true);

                    OriginalState = NewState;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LOG_IDENT, ex);

                    try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                    catch (Exception cleanupEx) { App.Logger.WriteException(LOG_IDENT + "::Cleanup", cleanupEx); }

                    Frontend.ShowConnectivityDialog(
                        String.Format(Strings.Dialog_Connectivity_UnableToConnect, "GitHub"),
                        $"{Strings.Menu_Mods_Presets_EmojiType_Error}\n\n{Strings.Dialog_Connectivity_TryAgainLater}",
                        MessageBoxImage.Warning,
                        ex
                    );
                }
            }
            else if (File.Exists(_filePath))
            {
                // Was `query is not null && query.Any()`, which is false for a font whose hash we
                // don't recognise — so a corrupt or hand-replaced file could never be cleared by
                // switching back to Default. Presence on disk is the right condition: this path
                // only runs when the user asked for Default.
                Filesystem.AssertReadOnly(_filePath);
                File.Delete(_filePath);

                OriginalState = NewState;
            }
        }
    }
}
