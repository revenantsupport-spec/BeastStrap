namespace BeastStrap.AppData
{
    internal interface IAppData
    {
        string ProductName { get; }

        string BinaryType { get; }

        string RegistryName { get; }

        string ProcessName { get; }

        string ExecutableName { get; }

        string Directory { get; }

        string ExecutablePath { get; }

        // When set to a non-empty path, Directory returns this verbatim instead of
        // building one from Paths.Versions + DistributionState.VersionGuid. Used by
        // the Versions Manager (v420.20+) to point AppData at a per-profile install
        // dir without rewriting DistributionState (which still holds the actual
        // Roblox version hash).
        string? InstallDirectoryOverride { get; set; }

        JsonManager<DistributionState> DistributionStateManager { get; }

        DistributionState DistributionState { get; }

        List<string> ModManifest { get; }

        IReadOnlyDictionary<string, string> PackageDirectoryMap { get; set; }
    }
}
