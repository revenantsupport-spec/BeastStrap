public class GithubReleaseAsset
{
    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = null!;

    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;

    // GitHub returns this on release assets. Summed across every release to render
    // the "installs" figure on the Home dashboard.
    [JsonPropertyName("download_count")]
    public long DownloadCount { get; set; }
}