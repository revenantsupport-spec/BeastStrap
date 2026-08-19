using BeastStrap.Models.APIs;

namespace BeastStrap.Utility
{
    // Resolves a Roblox server IP to a human-readable region.
    //
    // Roblox's game servers live in 128.116.0.0/16, carved into one /24 per datacenter, so the third
    // octet of a 128.116.x.y address identifies the datacenter. This map is curated from the community
    // list (https://devforum.roblox.com/t/roblox-server-region-a-list-of-roblox-ip-ranges-and-its-location...).
    // For any IP not in the map (or outside the 128.116 block) we fall back to ipinfo.io city level, so
    // the readout degrades gracefully and the map can be expanded over time without code changes elsewhere.
    public static class RobloxDatacenters
    {
        // third octet of 128.116.X.0 -> datacenter region label
        private static readonly Dictionary<int, string> DatacenterMap = new()
        {
            { 1,   "Los Angeles, US" },
            { 22,  "Atlanta, US" },
            { 33,  "London, UK" },
            { 45,  "Miami, US" },
            { 48,  "Chicago, US" },
            { 55,  "Tokyo, JP" },
            { 63,  "Los Angeles, US" },
            { 95,  "Dallas, US" },
            { 99,  "Atlanta, US" },
            { 101, "Chicago, US" },
            { 115, "Seattle, US" },
            { 116, "Los Angeles, US" },
            { 119, "London, UK" },
            { 120, "Tokyo, JP" },
        };

        // Precise datacenter region for a Roblox 128.116.x IP, or null if it isn't a known Roblox DC IP.
        public static string? LookupDatacenter(string? ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return null;

            string[] parts = ip.Split('.');
            if (parts.Length != 4 || parts[0] != "128" || parts[1] != "116")
                return null;

            return int.TryParse(parts[2], out int octet) && DatacenterMap.TryGetValue(octet, out string? region)
                ? region
                : null;
        }

        // Best region label for an IP: the precise datacenter when known, else a cached ipinfo.io
        // city/region/country lookup. Returns null only when everything fails.
        public static async Task<string?> ResolveRegionAsync(string? ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return null;

            string? datacenter = LookupDatacenter(ip);
            if (datacenter is not null)
                return datacenter;

            if (GlobalCache.ServerLocation.TryGetValue(ip, out string? cached) && !string.IsNullOrEmpty(cached))
                return cached;

            try
            {
                var ipInfo = await Http.GetJson<IPInfoResponse>($"https://ipinfo.io/{ip}/json");
                if (ipInfo is not null && !string.IsNullOrEmpty(ipInfo.City))
                {
                    string location = ipInfo.City == ipInfo.Region
                        ? $"{ipInfo.Region}, {ipInfo.Country}"
                        : $"{ipInfo.City}, {ipInfo.Region}, {ipInfo.Country}";
                    GlobalCache.ServerLocation[ip] = location;
                    return location;
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RobloxDatacenters::ResolveRegionAsync", ex);
            }

            return null;
        }
    }
}
