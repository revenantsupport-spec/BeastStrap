namespace BeastStrap.Models.Persistable
{
    // Backing store for the account manager. Persisted to its OWN file (Accounts.json via
    // JsonManager<AccountsData>("Accounts")) rather than Settings.json, so the cookies — even
    // though DPAPI-encrypted — stay out of Settings.json and out of the diagnostic crash-export
    // bundle (which only ever zips settings/state/fastflags).
    public class AccountsData
    {
        public List<RobloxAccount> Accounts { get; set; } = new();
    }
}
