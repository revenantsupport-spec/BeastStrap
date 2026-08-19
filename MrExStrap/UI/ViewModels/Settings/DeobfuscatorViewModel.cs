using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using BeastStrap.Utility;

namespace BeastStrap.UI.ViewModels.Settings
{
    public class DeobfuscatorViewModel : ScriptToolViewModel
    {
        public class DeobMethod
        {
            public string Id { get; }
            public string Name { get; }
            public DeobMethod(string id, string name) { Id = id; Name = name; }
            public override string ToString() => Name;
        }

        // Every deobfuscator LeakD exposes. LuaObfuscator routes through the proxy (handled in LeakdClient).
        public List<DeobMethod> Methods { get; } = new()
        {
            new("moonsec", "MoonSec V3"),
            new("prometheus", "Prometheus"),
            new("ironbrew2", "Ironbrew2"),
            new("ironveil", "Ironveil"),
            new("hercules", "Hercules"),
            new("luaobfuscator", "LuaObfuscator"),
        };

        private DeobMethod _selectedMethod;
        public DeobMethod SelectedMethod
        {
            get => _selectedMethod;
            set { _selectedMethod = value; OnPropertyChanged(nameof(SelectedMethod)); }
        }

        public DeobfuscatorViewModel()
        {
            _selectedMethod = Methods[0];
        }

        public ICommand DetectCommand => new AsyncRelayCommand(Detect);
        public ICommand DeobfuscateCommand => new AsyncRelayCommand(Deobfuscate);

        private Task Deobfuscate() =>
            RunAsync($"Deobfuscating with {SelectedMethod?.Name}…",
                     () => LeakdClient.DeobfuscateAsync(ScriptInput, SelectedMethod?.Id ?? "moonsec"));

        // Detect doesn't produce output — it identifies the obfuscator and auto-selects the matching
        // deobfuscator so the user can run it with one more click.
        private async Task Detect()
        {
            if (!HasInput)
            {
                IsError = true;
                StatusText = "Paste a script into the box first.";
                return;
            }

            IsBusy = true;
            IsError = false;
            StatusText = "Detecting…";
            try
            {
                var r = await LeakdClient.DetectAsync(ScriptInput);
                if (r.Success && !string.IsNullOrEmpty(r.DetectedName))
                {
                    string conf = r.Confidence.HasValue ? $" ({r.Confidence}% match)" : "";
                    var match = Methods.FirstOrDefault(m => MatchesDetected(m.Id, r.DetectedName!));
                    if (match != null)
                    {
                        SelectedMethod = match;
                        StatusText = $"Detected {r.DetectedName}{conf} — picked {match.Name}. Hit Deobfuscate.";
                    }
                    else
                    {
                        StatusText = $"Detected {r.DetectedName}{conf} — no matching deobfuscator here.";
                    }
                    IsError = false;
                }
                else
                {
                    IsError = true;
                    StatusText = r.Error ?? "Couldn't identify the obfuscator.";
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        // Loose match between the detector's name ("Ironbrew2", "MoonSec", …) and a method id.
        private static bool MatchesDetected(string methodId, string detected)
        {
            string d = new string(detected.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            string id = methodId.ToLowerInvariant();
            return d.Length > 0 && (d.Contains(id) || id.Contains(d));
        }

        protected override string BuildSuccessStatus(LeakdClient.LeakdResult r) =>
            r.OutputSizeKb.HasValue ? $"Done · {r.OutputSizeKb.Value:0.#} KB out" : "Done.";
    }
}
