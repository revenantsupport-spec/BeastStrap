using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace BeastStrap.Utility
{
    // A best-effort database of real Roblox FastFlag names, pulled live from Roblox's own client
    // settings endpoint. Used to (a) mark whether a flag in the editor is a known/real flag and (b)
    // back the "browse flags" search dialog. Loaded once per session, cached.
    public static class KnownFlags
    {
        private const string LOG_IDENT = "KnownFlags";

        // Built locally in LoadAsync and published exactly once, never mutated after —
        // so Search/IsKnown can read it from any thread without locking. Concurrent
        // loads (editor page + browse dialog racing) are serialized by _loadGate.
        private static volatile HashSet<string>? _names;
        private static readonly SemaphoreSlim _loadGate = new(1, 1);

        public static bool Loaded => _names is not null;

        public static IReadOnlyCollection<string> Names => _names ?? (IReadOnlyCollection<string>)Array.Empty<string>();

        public static bool IsKnown(string? name)
        {
            var names = _names;
            return names is not null && !string.IsNullOrEmpty(name) && names.Contains(name);
        }

        public static async Task LoadAsync()
        {
            if (_names is not null)
                return;

            await _loadGate.WaitAsync();
            try
            {
                if (_names is not null)
                    return;

                using var resp = await App.HttpClient.GetAsync(
                    "https://clientsettings.roblox.com/v2/settings/application/PCDesktopClient");

                if (!resp.IsSuccessStatusCode)
                    return;

                string json = await resp.Content.ReadAsStringAsync();

                var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("applicationSettings", out var settings)
                    && settings.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in settings.EnumerateObject())
                        loaded.Add(prop.Name);
                }

                if (loaded.Count > 0)
                    _names = loaded;

                App.Logger.WriteLine(LOG_IDENT, $"Loaded {loaded.Count} known flags");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::Load", ex);
            }
            finally
            {
                _loadGate.Release();
            }
        }

        // Filtered, sorted, capped list for the browse dialog.
        public static List<string> Search(string query, int limit = 500)
        {
            var names = _names;
            if (names is null)
                return new List<string>();

            IEnumerable<string> source = names;

            if (!string.IsNullOrWhiteSpace(query))
                source = source.Where(n => n.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));

            return source.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(limit).ToList();
        }
    }
}
