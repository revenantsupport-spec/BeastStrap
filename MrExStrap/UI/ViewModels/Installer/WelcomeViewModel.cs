namespace BeastStrap.UI.ViewModels.Installer
{
    public class WelcomeViewModel : NotifyPropertyChangedViewModel
    {
        // formatting is done here instead of in xaml, it's just a bit easier
        //
        // TWO arguments, not one. Upstream Bloxstrap had two official URLs and every translation
        // still says "... are {0} and {1}" — all 34 of them. When the fork rewrote the English
        // string down to a single {0} it left those untouched, so String.Format with one argument
        // threw FormatException for every non-English user and took the welcome page with it.
        // Passing both restores the sentence AND states the official sources, already translated.
        //
        // {0} is the website, {1} is where the source and releases live.
        public string MainText => String.Format(
            Strings.Installer_Welcome_MainText,
            $"[{App.ProjectDownloadLink.Replace("https://", "")}]({App.ProjectDownloadLink})",
            $"[{App.ProjectName}]({App.ProjectHost}/{App.ProjectRepository})"
        );

        public string VersionNotice { get; private set; } = "";

        public bool CanContinue { get; set; } = false;

        public event EventHandler? CanContinueEvent;

        // called by codebehind on page load
        public async void DoChecks()
        {
            var releaseInfo = await App.GetLatestRelease();

            if (releaseInfo is not null)
            {
                try
                {
                    if (Utilities.CompareVersions(App.Version, releaseInfo.TagName) == VersionComparison.LessThan)
                    {
                        VersionNotice = String.Format(Strings.Installer_Welcome_UpdateNotice, App.Version, releaseInfo.TagName.Replace("v", ""));
                        OnPropertyChanged(nameof(VersionNotice));
                    }
                }
                catch (Exception ex)
                {
                    // GitHub can return placeholder tags like "untagged-<sha>" when a release
                    // isn't attached to a real git tag. Don't let that crash the installer —
                    // just skip the "update available" notice and let setup proceed.
                    App.Logger.WriteException("WelcomeViewModel::DoChecks", ex);
                }
            }

            CanContinue = true;
            OnPropertyChanged(nameof(CanContinue));

            CanContinueEvent?.Invoke(this, new EventArgs());
        }
    }
}
