namespace BeastStrap.Models.APIs.GitHub
{
    public class GithubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = null!;

        [JsonPropertyName("name")]
        public string Name { get; set; } = null!;

        [JsonPropertyName("body")]
        public string Body { get; set; } = null!;

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = null!;

        [JsonPropertyName("published_at")]
        public string PublishedAt { get; set; } = null!;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = null!;

        [JsonPropertyName("assets")]
        public List<GithubReleaseAsset>? Assets { get; set; }
    }
}
