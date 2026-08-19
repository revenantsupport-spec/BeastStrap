using System.Security.Cryptography;

namespace BeastStrap.Utility.Accounts
{
    // DPAPI wrapper for account secrets (.ROBLOSECURITY cookies).
    //
    // Protect/Unprotect use DataProtectionScope.CurrentUser, so the ciphertext can only be
    // decrypted by the same Windows user on the same machine. Copying Accounts.json to another
    // PC or user account renders the cookies useless. The plaintext cookie never leaves this
    // class except to the in-memory caller — it is never logged.
    public static class SecureStore
    {
        private const string LOG_IDENT = "SecureStore";

        // Extra entropy mixed into the DPAPI blob. Not a secret (it ships in the binary); it just
        // scopes the protection so blobs from other apps can't be cross-decrypted.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BeastStrap.Accounts.v1");

        public static string Protect(string plaintext)
        {
            byte[] data = Encoding.UTF8.GetBytes(plaintext ?? "");
            byte[] encrypted = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        // Returns null if the blob is empty, malformed, or was produced by a different user/machine.
        public static string? Unprotect(string? protectedB64)
        {
            if (string.IsNullOrEmpty(protectedB64))
                return null;

            try
            {
                byte[] encrypted = Convert.FromBase64String(protectedB64);
                byte[] data = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception ex)
            {
                // Never log the value — just the failure type.
                App.Logger.WriteLine(LOG_IDENT, $"Failed to unprotect a stored cookie ({ex.GetType().Name}).");
                return null;
            }
        }
    }
}
