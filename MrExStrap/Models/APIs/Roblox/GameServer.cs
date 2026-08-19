using System.Text.Json.Serialization;

namespace BeastStrap.Models.APIs.Roblox
{
    // One public server instance from GET /v1/games/{placeId}/servers/Public.
    // The API returns players/ping but NOT the IP/region — region is resolved separately
    // (best-effort) via the gamejoin API + the RobloxDatacenters map.
    public class GameServer
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;   // the server's JobId

        [JsonPropertyName("maxPlayers")]
        public int MaxPlayers { get; set; }

        [JsonPropertyName("playing")]
        public int Playing { get; set; }

        [JsonPropertyName("fps")]
        public double Fps { get; set; }

        [JsonPropertyName("ping")]
        public int Ping { get; set; }
    }

    // Response wrapper for the servers endpoint (data array + paging cursor).
    public class ServerListResponse
    {
        [JsonPropertyName("data")]
        public List<GameServer> Data { get; set; } = new();

        [JsonPropertyName("nextPageCursor")]
        public string? NextPageCursor { get; set; }
    }
}
