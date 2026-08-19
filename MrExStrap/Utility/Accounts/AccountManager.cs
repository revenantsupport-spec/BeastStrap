namespace BeastStrap.Utility.Accounts
{
    // Store operations over App.Accounts (Accounts.json) plus the "turn a pasted/captured cookie
    // into a saved account" flow. Keeps the cookie encryption + Roblox validation in one place so
    // the dialog and the view-model don't each reimplement it.
    public static class AccountManager
    {
        private const string LOG_IDENT = "AccountManager";

        public static IReadOnlyList<RobloxAccount> All => App.Accounts.Prop.Accounts;

        // Validate a cookie, fetch display info + avatar, DPAPI-encrypt the cookie, and return a
        // ready-to-save account. Returns null if the cookie is invalid/expired or the network
        // failed. Does NOT add it to the store.
        public static async Task<RobloxAccount?> BuildFromCookieAsync(string cookie)
        {
            // Strip ALL whitespace, not just the ends. A .ROBLOSECURITY is a single unbroken
            // token, but people paste them out of wrapped text files and Discord code blocks with
            // newlines in the middle. Trim() left those in, the Cookie header was then rejected as
            // malformed and never attached, Roblox answered 401, and the user was told their
            // cookie was invalid — so they re-copied the same broken cookie forever.
            cookie = new string((cookie ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());

            if (string.IsNullOrEmpty(cookie))
                return null;

            var user = await RobloxAuth.ValidateAsync(cookie);
            if (user is null)
                return null;

            string? avatar = await RobloxAuth.GetHeadshotUrlAsync(user.Id);

            return new RobloxAccount
            {
                UserId = user.Id,
                Username = user.Name,
                DisplayName = user.DisplayName,
                AvatarUrl = avatar,
                EncryptedCookieB64 = SecureStore.Protect(cookie),
                LastValidatedUtc = DateTime.UtcNow
            };
        }

        // Add (or, if the same Roblox user already exists, refresh) an account and persist.
        public static void Add(RobloxAccount account)
        {
            var list = App.Accounts.Prop.Accounts;

            if (account.UserId != 0)
            {
                var existing = list.FirstOrDefault(a => a.UserId == account.UserId);
                if (existing != null)
                {
                    // Refresh in place: keep the original id + any alias the user set.
                    account.Id = existing.Id;
                    if (string.IsNullOrEmpty(account.Alias))
                        account.Alias = existing.Alias;
                    list.Remove(existing);
                }
            }

            list.Add(account);
            App.Accounts.Save();
            App.Logger.WriteLine(LOG_IDENT, $"Saved account {account.DisplayLabel} (userId {account.UserId}). Total: {list.Count}.");
        }

        public static void Remove(string id)
        {
            var list = App.Accounts.Prop.Accounts;
            var item = list.FirstOrDefault(a => a.Id == id);
            if (item != null)
            {
                list.Remove(item);
                App.Accounts.Save();
                App.Logger.WriteLine(LOG_IDENT, $"Removed account {item.DisplayLabel}. Total: {list.Count}.");
            }
        }
    }
}
