using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

using BeastStrap.UI.Elements.Bootstrapper.Base;
using BeastStrap.UI.ViewModels.Bootstrapper;

namespace BeastStrap.UI.Elements.Bootstrapper
{
    public partial class TerminalDialog : IBootstrapperDialog
    {
        private readonly BootstrapperDialogViewModel _viewModel;

        public global::BeastStrap.Bootstrapper? Bootstrapper { get; set; }

        private bool _isClosing;
        private bool _aborted;
        private readonly List<DispatcherTimer> _timers = new();
        private DispatcherTimer? _progressTimer;
        private double _primaryPct;
        private double _secondaryPct;
        private readonly Random _rng = new();

        // --- terminal line spec matching HTML LINES ---
        private sealed record LineSpec(string Cls, string Text, int Delay, int Speed);

        // Sped up 2.6x for fast CMD feel — matches real bootstrapper's sub-second steps.
        private static readonly LineSpec[] BootLines = new LineSpec[]
        {
            new("info",   "Microsoft Windows [Version 10.0.26100.2161]", 70, 0),
            new("info",   "(c) Microsoft Corporation. All rights reserved.", 30, 0),
            new("",       "", 40, 0),
            new("cmd",    "C:\\Users\\User> beaststrap.exe --launch --verbose --game roblox", 90, 6),
            new("",       "", 30, 0),
            new("info",   "Initializing BeastStrap Runtime Environment v2.4.1...", 90, 8),
            new("ok",     "  [  OK  ]  Core modules loaded", 55, 0),
            new("ok",     "  [  OK  ]  Memory allocator ready", 30, 0),
            new("ok",     "  [  OK  ]  Filesystem watcher active", 30, 0),
            new("info",   "Reading config: C:\\BeastStrap\\config.json", 60, 7),
            new("ok",     "  [  OK  ]  Config parsed. Channel: zDefaultChannel", 40, 0),
            new("ok",     "  [  OK  ]  Theme: Terminal v2.0", 25, 0),
            new("",       "", 30, 0),
            new("info",   "Checking for BeastStrap updates...", 110, 7),
            new("ok",     "  [  OK  ]  BeastStrap v2.4.1 is up to date", 160, 0),
            new("",       "", 25, 0),
            new("info",   "Locating Roblox installation...", 80, 8),
            new("ok",     "  [  OK  ]  Found: C:\\Users\\User\\AppData\\Local\\Roblox\\Versions\\", 100, 0),
            new("ok",     "  [  OK  ]  Target: version-00f3d18b37954c10", 40, 0),
            new("ok",     "  [  OK  ]  Client build: 0.638.1.6381236", 25, 0),
            new("",       "", 25, 0),
            new("info",   "Running integrity check on 312 client files...", 80, 7),
            new("dim",    "  Checking RobloxPlayerBeta.exe         [SHA256: a3f8c2...]  PASS", 55, 0),
            new("dim",    "  Checking RobloxCrashHandler.exe       [SHA256: 91b7d4...]  PASS", 25, 0),
            new("dim",    "  Checking content/textures/sky/*.dds   [312 files]          PASS", 25, 0),
            new("ok",     "  [  OK  ]  Integrity check passed. No corruption detected.", 60, 0),
            new("",       "", 25, 0),
            new("info",   "Contacting Roblox authentication servers...", 100, 7),
            new("dim",    "  Connecting to auth.roblox.com:443 over TLS 1.3...", 80, 0),
            new("ok",     "  [  OK  ]  TLS handshake complete. Cert valid.", 130, 0),
            new("ok",     "  [  OK  ]  Auth token acquired. Session: 8f3k2d...91xZ", 55, 0),
            new("ok",     "  [  OK  ]  UserId resolved. Account verified.", 35, 0),
            new("",       "", 25, 0),
            new("info",   "Fetching game manifest for PlaceId: 292439477...", 100, 7),
            new("ok",     "  [  OK  ]  Manifest received. 14 packages queued.", 130, 0),
            new("dim",    "  Package 01/14  RobloxApp.zip              12.4 MB", 25, 0),
            new("dim",    "  Package 02/14  content-textures2.zip      38.1 MB", 20, 0),
            new("dim",    "  Package 03/14  content-sounds.zip         24.7 MB", 20, 0),
            new("dim",    "  Package 04/14  shaders.zip                 8.9 MB", 20, 0),
            new("dim",    "  ...10 more packages pending...", 20, 0),
            new("",       "", 25, 0),
            new("warn",   "Downloading packages from cdn.rbxcdn.com...", 80, 6),
            new("bright", "PROGRESS_START", 60, 0),
            new("dim",    "  Extracting RobloxApp.zip...", 100, 0),
            new("dim",    "  Extracting content-textures2.zip...", 150, 0),
            new("dim",    "  Extracting content-sounds.zip...", 120, 0),
            new("dim",    "  Patching client registry entries...", 100, 0),
            new("ok",     "  [  OK  ]  All packages extracted and verified.", 100, 0),
            new("",       "", 25, 0),
            new("info",   "Applying custom modifications...", 80, 7),
            new("ok",     "  [  OK  ]  FPS unlocker applied", 55, 0),
            new("ok",     "  [  OK  ]  Custom shaders injected", 35, 0),
            new("ok",     "  [  OK  ]  Client flags patched", 35, 0),
            new("",       "", 25, 0),
            new("info",   "Preparing launch environment...", 80, 7),
            new("ok",     "  [  OK  ]  GPU: NVIDIA GeForce RTX 4070  READY", 55, 0),
            new("ok",     "  [  OK  ]  DirectX 12 context acquired", 35, 0),
            new("ok",     "  [  OK  ]  Audio subsystem initialized", 35, 0),
            new("ok",     "  [  OK  ]  Input devices registered", 35, 0),
            new("",       "", 25, 0),
            new("warn",   "Spawning RobloxPlayerBeta.exe...", 120, 6),
            new("dim",    "  PID: 18472   Priority: HIGH   Affinity: ALL CORES", 100, 0),
            new("ok",     "  [  OK  ]  Process spawned. Monitoring handshake...", 80, 0),
            new("ok",     "  [  OK  ]  Roblox client connected to BeastStrap pipe.", 130, 0),
            new("",       "", 25, 0),
            new("ok",     "  [  OK  ]  Joining game server...", 100, 0),
            new("ok",     "  [  OK  ]  Server: 45.63.12.88:49152   Ping: 23ms", 100, 0),
            new("ok",     "  [  OK  ]  Data model loaded.", 100, 0),
            new("ok",     "  [  OK  ]  Character spawned.", 80, 0),
            new("",       "", 25, 0),
            new("bright", "  ROBLOX IS LIVE. ENJOY YOUR GAME.", 80, 5),
            new("",       "", 25, 0),
            new("cmd",    "C:\\Users\\User> _", 60, 0),
        };

        #region IBootstrapperDialog passthrough

        public string Message
        {
            get => _viewModel.Message;
            set
            {
                _viewModel.Message = value;
                _viewModel.OnPropertyChanged(nameof(_viewModel.Message));
                // Mirror bootstrapper messages into the terminal as they arrive.
                // Avoid duplicating the fake boot sequence if this IS one of those lines.
                AppendBootstrapperLine(value);
            }
        }

        public ProgressBarStyle ProgressStyle
        {
            get => _viewModel.ProgressIndeterminate ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            set
            {
                _viewModel.ProgressIndeterminate = (value == ProgressBarStyle.Marquee);
                _viewModel.OnPropertyChanged(nameof(_viewModel.ProgressIndeterminate));
            }
        }

        public int ProgressMaximum
        {
            get => _viewModel.ProgressMaximum;
            set
            {
                _viewModel.ProgressMaximum = value;
                _viewModel.OnPropertyChanged(nameof(_viewModel.ProgressMaximum));
                PrimaryProgress.Maximum = value > 0 ? value : 100;
                SecondaryProgress.Maximum = value > 0 ? value : 100;
            }
        }

        public int ProgressValue
        {
            get => _viewModel.ProgressValue;
            set
            {
                _viewModel.ProgressValue = value;
                _viewModel.OnPropertyChanged(nameof(_viewModel.ProgressValue));
                // Drive primary bar from real bootstrapper progress unless we are in fake-prog mode.
                if (!_isFakeProgressRunning)
                {
                    PrimaryProgress.Value = value;
                    PrimaryPct.Text = $"{(PrimaryProgress.Maximum > 0 ? (int)(value / PrimaryProgress.Maximum * 100) : 0)}%";
                    // show bars as soon as real progress starts
                    if (ProgressSection.Opacity < 1 && value > 0)
                    {
                        ProgressSection.Opacity = 1;
                        BottomBar.Opacity = 1;
                    }
                }
            }
        }

        public TaskbarItemProgressState TaskbarProgressState
        {
            get => _viewModel.TaskbarProgressState;
            set { _viewModel.TaskbarProgressState = value; _viewModel.OnPropertyChanged(nameof(_viewModel.TaskbarProgressState)); }
        }

        public double TaskbarProgressValue
        {
            get => _viewModel.TaskbarProgressValue;
            set { _viewModel.TaskbarProgressValue = value; _viewModel.OnPropertyChanged(nameof(_viewModel.TaskbarProgressValue)); }
        }

        public bool CancelEnabled
        {
            get => _viewModel.CancelEnabled;
            set
            {
                _viewModel.CancelEnabled = value;
                _viewModel.OnPropertyChanged(nameof(_viewModel.CancelEnabled));
                _viewModel.OnPropertyChanged(nameof(_viewModel.CancelButtonVisibility));
                Dispatcher.Invoke(() => AbortBtn.IsEnabled = value);
            }
        }

        #endregion

        private bool _isFakeProgressRunning;

        public TerminalDialog()
        {
            InitializeComponent();

            _viewModel = new BootstrapperDialogViewModel(this);
            DataContext = _viewModel;
            Title = App.Settings.Prop.BootstrapperTitle;
            try { Icon = App.Settings.Prop.BootstrapperIcon.GetIcon().GetImageSource(); } catch { }

            Loaded += TerminalDialog_Loaded;
        }

        private void TerminalDialog_Loaded(object sender, RoutedEventArgs e)
        {
            // Start the scripted terminal boot animation shortly after the window appears.
            // Real bootstrapper messages will continue to append after this intro.
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            t.Tick += (s, args) => { t.Stop(); var _task = RunBootSequenceAsync(); };
            t.Start();
        }

        private async System.Threading.Tasks.Task RunBootSequenceAsync()
        {
            if (_aborted) return;

            int accumulated = 0;
            foreach (var spec in BootLines)
            {
                if (_aborted) break;

                accumulated += spec.Delay;
                await System.Threading.Tasks.Task.Delay(spec.Delay);

                if (_aborted) break;

                if (spec.Text == "PROGRESS_START")
                {
                    // Reveal progress section and start fake fill (60% / 80% over 1s, then 100% in 0.5s).
                    Dispatcher.Invoke(() =>
                    {
                        ProgressSection.Opacity = 1;
                        BottomBar.Opacity = 1;
                    });
                    _isFakeProgressRunning = true;
                    AnimateProgress(60, 80, 1000, () => AnimateProgress(100, 100, 500, null));
                    continue;
                }

                await AppendLineAsync(spec);
            }

            if (!_aborted)
            {
                Dispatcher.Invoke(() =>
                {
                    ReplayBtn.Visibility = Visibility.Visible;
                    AnimateProgress(100, 100, 300, null);
                });
            }
        }

        private System.Windows.Media.Brush BrushFor(string cls) => cls switch
        {
            "dim"    => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#666666")),
            "bright" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF")),
            "warn"   => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFCC00")),
            "ok"     => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00CC44")),
            "err"    => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4444")),
            "info"   => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#AAAAAA")),
            "cmd"    => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF")),
            "faint"  => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#444444")),
            _        => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC")),
        };

        private async System.Threading.Tasks.Task AppendLineAsync(LineSpec spec)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource();
            Dispatcher.Invoke(() =>
            {
                var tb = new TextBlock
                {
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 12,
                    LineHeight = 18.6,
                    TextWrapping = TextWrapping.NoWrap,
                    Foreground = BrushFor(spec.Cls),
                    Opacity = 1,
                    Text = "",
                };
                if (spec.Cls == "bright")
                    tb.FontWeight = FontWeights.Bold;

                TerminalPanel.Children.Add(tb);
                TerminalScroll.ScrollToEnd();

                if (spec.Speed == 0 || string.IsNullOrEmpty(spec.Text))
                {
                    tb.Text = spec.Text;
                    TerminalScroll.ScrollToEnd();
                    tcs.SetResult();
                    return;
                }

                int i = 0;
                DispatcherTimer? timer = null;
                timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(spec.Speed * 0.7) };
                timer.Tick += (s, _) =>
                {
                    if (_aborted) { timer.Stop(); tcs.TrySetResult(); return; }
                    if (i <= spec.Text.Length)
                    {
                        tb.Text = spec.Text.Substring(0, i);
                        // jitter
                        timer.Interval = TimeSpan.FromMilliseconds(spec.Speed * (0.6 + _rng.NextDouble() * 0.8));
                        TerminalScroll.ScrollToEnd();
                        i++;
                    }
                    else
                    {
                        timer.Stop();
                        _timers.Remove(timer);
                        tcs.SetResult();
                    }
                };
                _timers.Add(timer);
                timer.Start();
            });
            await tcs.Task;
        }

        // Append a live bootstrapper message as a terminal line (called from the Message setter).
        private void AppendBootstrapperLine(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            // Deduplicate: if the intro sequence already printed this exact line, skip.
            // We keep it simple and just append unless it's the initial "Please wait..." placeholder.
            if (message == "Please wait...") return;

            string cls = "info";
            string lower = message.ToLowerInvariant();
            if (lower.Contains("ok") || lower.Contains("success") || lower.Contains("verified") || lower.Contains("complete")) cls = "ok";
            else if (lower.Contains("warn") || lower.Contains("downloading") || lower.Contains("spawning")) cls = "warn";
            else if (lower.Contains("error") || lower.Contains("fail") || lower.Contains("abort")) cls = "err";
            else if (lower.Contains("checking") || lower.Contains("found") || lower.Contains("manifest") || lower.Contains("extract")) cls = "dim";

            Dispatcher.BeginInvoke(() =>
            {
                var tb = new TextBlock
                {
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 12,
                    Foreground = BrushFor(cls),
                    TextWrapping = TextWrapping.Wrap,
                    Text = message,
                    Margin = new Thickness(0,1,0,1)
                };
                TerminalPanel.Children.Add(tb);
                TerminalScroll.ScrollToEnd();
            });
        }

        private void AnimateProgress(double targetPrimary, double targetSecondary, int durationMs, Action? done)
        {
            const int steps = 60;
            int interval = Math.Max(8, durationMs / steps);
            double pStep = (targetPrimary - _primaryPct) / steps;
            double sStep = (targetSecondary - _secondaryPct) / steps;
            int step = 0;

            _progressTimer?.Stop();
            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(interval) };
            _progressTimer.Tick += (s, _) =>
            {
                if (_aborted) { _progressTimer.Stop(); return; }
                step++;
                _primaryPct = Math.Min(_primaryPct + pStep + _rng.NextDouble() * pStep * 0.3, targetPrimary);
                _secondaryPct = Math.Min(_secondaryPct + sStep + _rng.NextDouble() * sStep * 0.3, targetSecondary);

                PrimaryProgress.Value = _primaryPct;
                SecondaryProgress.Value = _secondaryPct;
                PrimaryPct.Text = $"{(int)Math.Round(_primaryPct)}%";
                SecondaryPct.Text = $"{(int)Math.Round(_secondaryPct)}%";

                if (step >= steps)
                {
                    _progressTimer.Stop();
                    _isFakeProgressRunning = false;
                    done?.Invoke();
                }
            };
            _progressTimer.Start();
        }

        private void UiWindow_Closing(object sender, CancelEventArgs e)
        {
            if (!_isClosing)
                Bootstrapper?.Cancel();
        }

        private void AbortBtn_Click(object sender, RoutedEventArgs e)
        {
            _aborted = true;
            _timers.ForEach(t => t.Stop());
            _timers.Clear();
            _progressTimer?.Stop();

            var tb = new TextBlock
            {
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
                Foreground = BrushFor("err"),
                Text = "  [ABORT]  Launch cancelled by user. Cleaning up...",
                Margin = new Thickness(0,6,0,0)
            };
            TerminalPanel.Children.Add(tb);
            var tb2 = new TextBlock
            {
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
                Foreground = BrushFor("dim"),
                Text = "C:\\Users\\User> _"
            };
            TerminalPanel.Children.Add(tb2);
            TerminalScroll.ScrollToEnd();
            ReplayBtn.Visibility = Visibility.Visible;

            Bootstrapper?.Cancel();
        }

        private void ReplayBtn_Click(object sender, RoutedEventArgs e)
        {
            _aborted = false;
            _primaryPct = 0;
            _secondaryPct = 0;
            _timers.ForEach(t => t.Stop());
            _timers.Clear();
            _progressTimer?.Stop();
            PrimaryProgress.Value = 0;
            SecondaryProgress.Value = 0;
            PrimaryPct.Text = "0%";
            SecondaryPct.Text = "0%";
            ProgressSection.Opacity = 0;
            BottomBar.Opacity = 0;
            ReplayBtn.Visibility = Visibility.Collapsed;
            TerminalPanel.Children.Clear();
            _ = RunBootSequenceAsync();
        }

        #region IBootstrapperDialog Methods

        public void ShowBootstrapper() => ShowDialog();

        public void CloseBootstrapper()
        {
            _isClosing = true;
            Dispatcher.BeginInvoke(Close);
        }

        public void ShowSuccess(string message, Action? callback) => BaseFunctions.ShowSuccess(message, callback);

        #endregion
    }
}
