using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

using BeastStrap.Integrations.BloxGen;
using BeastStrap.Utility.Accounts;

namespace BeastStrap.UI.ViewModels.Settings
{
    // AltGen tab. Generates Roblox alt accounts through BloxGen using the user's OWN API key
    // (entered here, stored locally). Advertises the maintainer's affiliate link so users sign
    // up through it. No key is ever embedded in the app.
    public class AltGenViewModel : NotifyPropertyChangedViewModel
    {
        public string ApiKey
        {
            get => App.Settings.Prop.BloxGenApiKey;
            set
            {
                App.Settings.Prop.BloxGenApiKey = value?.Trim() ?? "";
                OnPropertyChanged(nameof(ApiKey));
            }
        }

        // A BloxGen account type: Value is sent to the API verbatim; Label shows the type plus the
        // tier it needs (Free / Premium / Ultra), matching BloxGen's role plans, so users know what
        // their role can actually generate.
        public class AltGenType
        {
            public string Value { get; }
            public string Label { get; }
            public AltGenType(string value, string label) { Value = value; Label = label; }
        }

        public AltGenType[] AltTypes { get; } =
        {
            new("alt", "alt — All from 2025 (Free)"),
            new("+30 days old", "+30 days old (Premium)"),
            new("+1 year old", "+1 year old (Premium)"),
            new("5+ years old", "5+ years old (Premium)"),
            new("dump", "dump — Dumps Alts (Ultra)"),
        };

        private string _selectedType = "alt";
        public string SelectedType
        {
            get => _selectedType;
            set { _selectedType = value; OnPropertyChanged(nameof(SelectedType)); }
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(CanGenerate)); }
        }

        public bool CanGenerate => !_isBusy;

        private string _status = "";
        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(HasStatus)); }
        }
        public bool HasStatus => !string.IsNullOrEmpty(_status);

        // Set when BloxGen rejects a generate because the account hasn't accepted the rules yet.
        // Surfaces an "Agree to BloxGen rules" button that opens the maintainer's affiliate link.
        private bool _needsRulesAgreement;
        public bool NeedsRulesAgreement
        {
            get => _needsRulesAgreement;
            set { _needsRulesAgreement = value; OnPropertyChanged(nameof(NeedsRulesAgreement)); }
        }

        // Live cooldown (BloxGen 429): a DispatcherTimer ticks CooldownText down once a second so the
        // user sees a real countdown instead of a frozen "wait N minutes" message.
        private System.Windows.Threading.DispatcherTimer? _cooldownTimer;
        private DateTime _cooldownEndUtc;

        private bool _onCooldown;
        public bool OnCooldown
        {
            get => _onCooldown;
            set { _onCooldown = value; OnPropertyChanged(nameof(OnCooldown)); }
        }

        private string _cooldownText = "";
        public string CooldownText
        {
            get => _cooldownText;
            set { _cooldownText = value; OnPropertyChanged(nameof(CooldownText)); }
        }

        private void StartCooldown(long milliseconds)
        {
            _cooldownEndUtc = DateTime.UtcNow.AddMilliseconds(milliseconds);
            OnCooldown = true;
            UpdateCooldown();

            _cooldownTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _cooldownTimer.Tick -= OnCooldownTick;
            _cooldownTimer.Tick += OnCooldownTick;
            _cooldownTimer.Start();
        }

        private void StopCooldown()
        {
            _cooldownTimer?.Stop();
            OnCooldown = false;
            CooldownText = "";
        }

        private void OnCooldownTick(object? sender, EventArgs e) => UpdateCooldown();

        private void UpdateCooldown()
        {
            var remaining = _cooldownEndUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                _cooldownTimer?.Stop();
                OnCooldown = false;
                CooldownText = "";
                Status = "Cooldown's over — you can generate again.";
                return;
            }

            CooldownText = $"Please wait {(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2} before your next free generation.";
        }

        private string _username = "";
        public string Username
        {
            get => _username;
            set { _username = value; OnPropertyChanged(nameof(Username)); OnPropertyChanged(nameof(HasResult)); }
        }

        private string _password = "";
        public string Password
        {
            get => _password;
            set { _password = value; OnPropertyChanged(nameof(Password)); }
        }

        private string _cookie = "";
        public string Cookie
        {
            get => _cookie;
            set { _cookie = value; OnPropertyChanged(nameof(Cookie)); OnPropertyChanged(nameof(HasResult)); }
        }

        private string _avatarUrl = "";
        public string AvatarUrl
        {
            get => _avatarUrl;
            set { _avatarUrl = value; OnPropertyChanged(nameof(AvatarUrl)); OnPropertyChanged(nameof(HasAvatar)); }
        }
        public bool HasAvatar => !string.IsNullOrEmpty(_avatarUrl);

        private string _userId = "";
        public string UserId
        {
            get => _userId;
            set { _userId = value; OnPropertyChanged(nameof(UserId)); OnPropertyChanged(nameof(HasUserId)); }
        }
        public bool HasUserId => !string.IsNullOrEmpty(_userId);

        public bool HasResult => !string.IsNullOrEmpty(_username) || !string.IsNullOrEmpty(_cookie);

        public ICommand GenerateCommand => new AsyncRelayCommand(GenerateAsync);
        public ICommand SaveKeyCommand => new RelayCommand(SaveKey);
        public ICommand CopyUsernameCommand => new RelayCommand(() => CopyToClipboard(Username, "Username"));
        public ICommand CopyPasswordCommand => new RelayCommand(() => CopyToClipboard(Password, "Password"));
        public ICommand CopyCookieCommand => new RelayCommand(() => CopyToClipboard(Cookie, ".ROBLOSECURITY cookie"));
        public ICommand SaveToMultiInstanceCommand => new AsyncRelayCommand(SaveToMultiInstanceAsync);

        private async Task GenerateAsync()
        {
            const string LOG_IDENT = "AltGenViewModel::GenerateAsync";

            if (_isBusy)
                return;

            if (string.IsNullOrWhiteSpace(App.Settings.Prop.BloxGenApiKey))
            {
                Status = "Enter your BloxGen API key first. Don't have one? Use the \"Get a free key\" button above.";
                return;
            }

            IsBusy = true;
            Status = "Generating…";
            NeedsRulesAgreement = false;

            try
            {
                var result = await BloxGenClient.GenerateAsync(App.Settings.Prop.BloxGenApiKey, SelectedType);

                if (result.Success)
                {
                    StopCooldown();
                    Username = result.Username ?? "";
                    Password = result.Password ?? "";
                    Cookie = result.Cookie ?? "";
                    AvatarUrl = result.AvatarUrl ?? "";
                    UserId = result.Id?.ToString() ?? "";

                    string region = string.IsNullOrEmpty(result.Region) ? "" : $" · region {result.Region}";
                    string cost = result.Cost.HasValue ? $" · cost {result.Cost.Value}" : "";
                    Status = $"Done — generated a '{SelectedType}' account.{region}{cost}";
                }
                else
                {
                    // BloxGen returns "You must accept the rules before generating" until the account
                    // agrees once on the site — surface a one-click button to go do that.
                    NeedsRulesAgreement = (result.Error ?? "").Contains("rules", StringComparison.OrdinalIgnoreCase);

                    if (result.TimeRemaining.HasValue && result.TimeRemaining.Value > 0)
                    {
                        // Cooldown (429): show a live ticking countdown + premium upsell instead of a
                        // frozen "wait N minutes" line.
                        StartCooldown(result.TimeRemaining.Value);
                        Status = "";
                    }
                    else
                    {
                        Status = result.Error ?? "Generation failed.";
                    }

                    if (!string.IsNullOrEmpty(result.RawResponse))
                        App.Logger.WriteLine(LOG_IDENT, $"Raw BloxGen response: {result.RawResponse}");
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                Status = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        // Bridge to the Multi Instance tab: validate the generated cookie, save it as an account
        // (cookie DPAPI-encrypted), so the user can pick it on that tab and launch it — alone or
        // alongside others. Reuses the existing AccountManager so it shows up like any other saved
        // account.
        private async Task SaveToMultiInstanceAsync()
        {
            const string LOG_IDENT = "AltGenViewModel::SaveToMultiInstanceAsync";

            if (_isBusy || string.IsNullOrEmpty(Cookie))
                return;

            IsBusy = true;
            Status = "Saving to Multi Instance…";

            try
            {
                var account = await AccountManager.BuildFromCookieAsync(Cookie);
                if (account is null)
                {
                    Status = "Couldn't save — Roblox didn't accept the generated cookie (it may be rate-limited or already invalid). Try again in a moment.";
                    return;
                }

                AccountManager.Add(account);
                Status = $"Saved {account.DisplayLabel} to the Multi Instance tab. Open that tab to launch it — on its own or ticked alongside others.";
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                Status = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void SaveKey()
        {
            App.Settings.Prop.BloxGenApiKey = (App.Settings.Prop.BloxGenApiKey ?? "").Trim();
            App.Settings.Save();
            OnPropertyChanged(nameof(ApiKey));
            Status = string.IsNullOrEmpty(App.Settings.Prop.BloxGenApiKey)
                ? "Cleared the saved API key."
                : "API key saved — you can generate whenever you like now.";
        }

        private void CopyToClipboard(string value, string label)
        {
            if (string.IsNullOrEmpty(value))
                return;

            try
            {
                Clipboard.SetText(value);
                Status = $"{label} copied to clipboard.";
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("AltGenViewModel::CopyToClipboard", ex);
            }
        }
    }
}
