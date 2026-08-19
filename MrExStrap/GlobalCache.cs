using System.Collections.Concurrent;

namespace BeastStrap
{
    public static class GlobalCache
    {
        // Concurrent because two unrelated paths write it: ActivityData.QueryServerLocation (under
        // a per-INSTANCE semaphore, which gives no mutual exclusion against anything else) and
        // RobloxDatacenters.ResolveRegionAsync (under nothing at all), both on thread-pool
        // continuations, both reachable for the same IP off a single game join.
        public static readonly ConcurrentDictionary<string, string?> ServerLocation = new();
    }
}
