/*
 * Roblox Studio Mod Manager (ProjectSrc/Utility/PackageManifest.cs)
 * MIT License
 * Copyright (c) 2015-present MaximumADHD
*/

namespace BeastStrap.Models.Manifest
{
    public class PackageManifest : List<Package>
    {
        public PackageManifest(string data)
        {
            using var reader = new StringReader(data);
            string? version = reader.ReadLine();

            if (version != "v0")
                throw new NotSupportedException($"Unexpected package manifest version: {version} (expected v0!)");

            while (true)
            {
                string? fileName = reader.ReadLine();
                string? signature = reader.ReadLine();

                string? rawPackedSize = reader.ReadLine();
                string? rawSize = reader.ReadLine();

                if (string.IsNullOrEmpty(fileName) ||
                    string.IsNullOrEmpty(signature) ||
                    string.IsNullOrEmpty(rawPackedSize) ||
                    string.IsNullOrEmpty(rawSize))
                    break;

                // Standalone launcher/installer executables are listed in the manifest but have no
                // entry in the package directory map, so ExtractPackage can never place them —
                // fetching one is pure waste (~13 MB of a ~216 MB install, 6% of every download).
                //
                // Roblox RENAMED this package from RobloxPlayerLauncher.exe to
                // RobloxPlayerInstaller.exe. The old exact match then filtered nothing at all and
                // we silently paid for it on every install. Match both names so a rollback on
                // Roblox's side doesn't reintroduce the cost.
                //
                // `continue`, NOT `break`: this entry only happens to be last in the manifest
                // today. `break` meant that if Roblox ever reordered it, every package after it
                // would be dropped from the list and we would install a truncated, broken client.
                if (fileName is "RobloxPlayerLauncher.exe" or "RobloxPlayerInstaller.exe")
                    continue;

                int packedSize = int.Parse(rawPackedSize);
                int size = int.Parse(rawSize);

                Add(new Package
                {
                    Name = fileName,
                    Signature = signature,
                    PackedSize = packedSize,
                    Size = size
                });
            }
        }
    }
}
