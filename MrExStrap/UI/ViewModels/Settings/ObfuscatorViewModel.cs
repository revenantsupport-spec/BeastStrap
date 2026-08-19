using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using BeastStrap.Utility;

namespace BeastStrap.UI.ViewModels.Settings
{
    public class ObfuscatorViewModel : ScriptToolViewModel
    {
        // A friendly label shown in the dropdown, plus the exact id LeakD's /obfuscate expects.
        public class ObfPreset
        {
            public string Id { get; }
            public string Label { get; }
            public ObfPreset(string id, string label) { Id = id; Label = label; }
            public override string ToString() => Label;
        }

        public class ObfEngine
        {
            public string Id { get; }
            public string Label { get; }
            public ObfEngine(string id, string label) { Id = id; Label = label; }
            public override string ToString() => Label;
        }

        public List<ObfEngine> Engines { get; } = new()
        {
            new("leakd", "LeakD (Prometheus)"),
            new("luaobfuscator", "LuaObfuscator.com"),
        };

        private ObfEngine _selectedEngine;
        public ObfEngine SelectedEngine
        {
            get => _selectedEngine;
            set
            {
                _selectedEngine = value;
                OnPropertyChanged(nameof(SelectedEngine));
                OnPropertyChanged(nameof(IsLeakd));
                OnPropertyChanged(nameof(LeakdOptionsVisibility));
                OnPropertyChanged(nameof(LuaObfuscatorOptionsVisibility));
            }
        }

        public bool IsLeakd => _selectedEngine?.Id != "luaobfuscator";
        public Visibility LeakdOptionsVisibility => IsLeakd ? Visibility.Visible : Visibility.Collapsed;
        public Visibility LuaObfuscatorOptionsVisibility => IsLeakd ? Visibility.Collapsed : Visibility.Visible;

        // Presets accepted by LeakD's /obfuscate (a Prometheus fork) — the target Lua environment.
        public List<ObfPreset> Presets { get; } = new()
        {
            new("RobloxExecutor", "Roblox Executor"),
            new("RobloxStudio", "Roblox Studio"),
            new("Lua51", "Lua 5.1"),
            new("Lua52", "Lua 5.2"),
            new("Lua53", "Lua 5.3"),
            new("Lua54", "Lua 5.4"),
        };

        private ObfPreset _selectedPreset;
        public ObfPreset SelectedPreset
        {
            get => _selectedPreset;
            set { _selectedPreset = value ?? Presets[0]; OnPropertyChanged(nameof(SelectedPreset)); }
        }

        // luaobfuscator.com API key — persisted in local settings only (never shipped). Blank = disabled.
        public string ApiKey
        {
            get => App.Settings.Prop.LuaObfuscatorApiKey;
            set { App.Settings.Prop.LuaObfuscatorApiKey = value ?? ""; OnPropertyChanged(nameof(ApiKey)); }
        }

        public ObfuscatorViewModel()
        {
            _selectedEngine = Engines[0];
            _selectedPreset = Presets[0];
        }

        public ICommand ObfuscateCommand => new AsyncRelayCommand(Obfuscate);
        public ICommand BeautifyCommand => new AsyncRelayCommand(Beautify);

        private Task Obfuscate()
        {
            if (!IsLeakd)
                return RunAsync("Obfuscating via luaobfuscator.com…", () => LuaObfuscatorClient.ObfuscateAsync(ScriptInput, ApiKey));

            return RunAsync("Obfuscating…", () => LeakdClient.ObfuscateAsync(ScriptInput, SelectedPreset?.Id ?? "RobloxExecutor"));
        }

        private Task Beautify() => RunAsync("Beautifying…", () => LeakdClient.BeautifyAsync(ScriptInput));

        protected override string BuildSuccessStatus(LeakdClient.LeakdResult r)
        {
            var parts = new List<string>();
            if (r.OutputSizeKb.HasValue) parts.Add($"{r.OutputSizeKb.Value:0.#} KB out");
            if (r.Ratio.HasValue) parts.Add($"{r.Ratio.Value:0.#}× size");
            return parts.Count > 0 ? "Done · " + string.Join(" · ", parts) : "Done.";
        }
    }
}
