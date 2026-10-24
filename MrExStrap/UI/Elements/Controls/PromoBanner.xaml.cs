using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BeastStrap.UI.Elements.Controls
{
    /// <summary>
    /// Rotating promo banner. Cycles through a fixed set of links with a gentle crossfade.
    /// </summary>
    /// <remarks>
    /// All slots are compiled in — nothing is fetched at runtime. See the note in the XAML for why
    /// that is load-bearing rather than laziness.
    /// </remarks>
    public partial class PromoBanner
    {
        private sealed record Slot(string Title, string Subtitle, string Url, string Logo);

        // Add or reorder freely. One entry is fine; rotation just stops being rotation.
        //
        // Logos are the sites' own favicons, downloaded once and embedded as Resources (see the
        // csproj). Deliberately NOT fetched at runtime.
        private static readonly Slot[] Slots =
        {
            new("Looking for an executor? Try getexecutors.com",
                "Executors and externals, all in one place.",
                "https://getexecutors.com/",
                "pack://application:,,,/Resources/PromoGetExecutors.png"),

            new("Need scripts? robloxscripts.com",
                "Scripts, guides and the community Discord.",
                "https://robloxscripts.com/",
                "pack://application:,,,/Resources/PromoRobloxScripts.png"),

            new("Managing lots of accounts? zeropoint.to",
                "Roblox account tools and automation in one place.",
                "https://zeropoint.to/?ref=SIRMEME",
                "pack://application:,,,/Resources/PromoZeroPoint.png"),

            new("Running a farm? Try farmsync.cloud",
                "Devices, accounts and automation, all from one dashboard.",
                "https://farmsync.cloud/",
                "pack://application:,,,/Resources/PromoFarmSync.png"),

            // V3rmillion's own "V", the very file its creator theme uses, rather than a second copy of it.
            // The forum blocks direct downloads of its logo, and has no favicon of its own.
            new("Missed V3rmillion? It's back at v3rm.net",
                "The scripting and exploiting forum, Roblox board and all.",
                "https://v3rm.net/",
                "pack://application:,,,/Resources/Creators/v3rm-avatar.png"),
        };

        // Decoded once and frozen: rotation would otherwise re-decode the same PNG every 12 seconds,
        // and a frozen ImageSource is safe to hand to the UI thread from anywhere.
        private static readonly Dictionary<string, ImageSource> LogoCache = new();

        private static readonly TimeSpan RotateEvery = TimeSpan.FromSeconds(12);
        private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(220));

        private readonly DispatcherTimer? _timer;
        private int _index;

        public PromoBanner()
        {
            InitializeComponent();

            if (Slots.Length == 0)
            {
                Visibility = Visibility.Collapsed;
                return;
            }

            // Start somewhere random so the same slot isn't always the one people see first — with a
            // 12s dwell most users would otherwise only ever see slot 0.
            _index = Random.Shared.Next(Slots.Length);

            // Add the click binding BEFORE the first Apply — Apply sets the target URL by walking
            // Root.InputBindings, so doing this the other way round leaves CommandParameter null and
            // clicking the card does nothing until the first rotation 12 seconds later.
            Root.InputBindings.Add(new MouseBinding(
                ViewModels.GlobalViewModel.OpenWebpageCommand,
                new MouseGesture(MouseAction.LeftClick)));

            Apply(Slots[_index]);

            if (Slots.Length < 2)
                return;

            _timer = new DispatcherTimer { Interval = RotateEvery };
            _timer.Tick += (_, _) => Advance();

            // Start and stop with the control. A DispatcherTimer holds a reference to its target and
            // keeps ticking after a page is navigated away from, and this app runs with
            // ShutdownMode=OnExplicitShutdown where nothing cleans that up for us. Loaded/Unloaded
            // can both fire more than once during navigation; Start/Stop are idempotent.
            Loaded += (_, _) => _timer.Start();
            Unloaded += (_, _) => _timer.Stop();
        }

        private void Advance()
        {
            _index = (_index + 1) % Slots.Length;

            // Fade out, swap underneath, fade back in.
            var fadeOut = new DoubleAnimation(1, 0, FadeDuration);
            fadeOut.Completed += (_, _) =>
            {
                Apply(Slots[_index]);
                Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeDuration));
            };

            Root.BeginAnimation(OpacityProperty, fadeOut);
        }

        private static ImageSource? LoadLogo(string pack)
        {
            if (LogoCache.TryGetValue(pack, out var cached))
                return cached;

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(pack, UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();

                LogoCache[pack] = image;
                return image;
            }
            catch (Exception ex)
            {
                // A missing resource must not take the page down with it — the banner just shows
                // text, which is still perfectly usable.
                App.Logger.WriteException("PromoBanner::LoadLogo", ex);
                return null;
            }
        }

        private void Apply(Slot slot)
        {
            SlotIcon.Source = LoadLogo(slot.Logo);
            SlotTitle.Text = slot.Title;
            SlotSubtitle.Text = slot.Subtitle;
            SlotButton.CommandParameter = slot.Url;
            Root.ToolTip = slot.Url;

            // The whole card is clickable, so its click target has to move with the slot too.
            foreach (var binding in Root.InputBindings)
            {
                if (binding is MouseBinding mouse)
                    mouse.CommandParameter = slot.Url;
            }
        }
    }
}
