namespace BeastStrap.Models.APIs
{
    public class IPInfoResponse
    {
        // ipinfo.io omits fields for some IPs (e.g. no region/city on a bogon or a bare
        // datacenter IP). Default to "" so formatting a partial response never NREs.
        [JsonPropertyName("city")]
        public string City { get; set; } = "";

        [JsonPropertyName("country")]
        public string Country { get; set; } = "";

        [JsonPropertyName("region")]
        public string Region { get; set; } = "";
    }
}
