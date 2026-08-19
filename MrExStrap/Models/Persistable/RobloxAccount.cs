using System.Text.Json.Serialization;

namespace BeastStrap.Models.Persistable
{
    // One saved Roblox account for the Multi Instance tab.
    //
    // The .ROBLOSECURITY cookie is a FULL session token, so it is never stored in plaintext —
    // EncryptedCookieB64 holds a Windows DPAPI (CurrentUser) blob, base64-encoded, that only
    // this Windows user on this machine can decrypt (see Utility.Accounts.SecureStore). The rest
    // is cached display info, refreshed when the account is added or re-validated.
    public class RobloxAccount
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public long UserId { get; set; }
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";

        // Optional friendly label the user can set; falls back to display name / username.
        public string Alias { get; set; } = "";

        // Headshot thumbnail URL (thumbnails.roblox.com). Cached for the tile.
        public string? AvatarUrl { get; set; }

        // DPAPI-protected .ROBLOSECURITY, base64. Never logged, never included in exports.
        public string EncryptedCookieB64 { get; set; } = "";

        public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? LastValidatedUtc { get; set; }

        // Optional Versions Manager profile this account launches with, stored as VersionProfile.Id.
        // Empty (the default) means "use whatever profile is active globally" — the normal launch.
        // Applied as a per-launch override only; it never changes the global active profile.
        public string VersionProfileId { get; set; } = "";

        // Best label to show in the UI. Pure logic, not persisted.
        [JsonIgnore]
        public string DisplayLabel =>
            !string.IsNullOrWhiteSpace(Alias) ? Alias
            : !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName
            : !string.IsNullOrWhiteSpace(Username) ? Username
            : UserId.ToString();
    }
}
