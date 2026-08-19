using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace BeastStrap.Utility
{
    internal static class Filesystem
    {
        // Free bytes on the volume holding `path`, or long.MaxValue when we can't work out which
        // volume that is.
        //
        // The "can't tell" case USED to return -1, and both callers compare the result numerically
        // against a required byte count — so -1 read as "no space at all" and hard-failed the
        // launch with ERROR_INSTALL_FAILURE. DriveInfo.GetDrives() only enumerates drive letters,
        // so a UNC path never matched, and portable mode can legitimately run from one. Failing
        // open is right here: the check is an early courtesy, and the download itself reports a
        // real out-of-space error anyway.
        internal static long GetFreeDiskSpace(string path)
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                // https://github.com/bloxstraplabs/bloxstrap/issues/1648#issuecomment-2192571030
                if (!path.ToUpperInvariant().StartsWith(drive.Name))
                    continue;

                try
                {
                    return drive.AvailableFreeSpace;
                }
                catch (Exception ex)
                {
                    // A mapped drive that's since been disconnected throws IOException here.
                    App.Logger.WriteException("Filesystem::GetFreeDiskSpace", ex);
                    return long.MaxValue;
                }
            }

            App.Logger.WriteLine("Filesystem::GetFreeDiskSpace", $"Couldn't map '{path}' to a drive — skipping the free-space check.");
            return long.MaxValue;
        }

        internal static void AssertReadOnly(string filePath)
        {
            var fileInfo = new FileInfo(filePath);

            if (!fileInfo.Exists || !fileInfo.IsReadOnly)
                return;

            fileInfo.IsReadOnly = false;
            App.Logger.WriteLine("Filesystem::AssertReadOnly", $"The following file was set as read-only: {filePath}");
        }

        internal static void AssertReadOnlyDirectory(string directoryPath)
        {
            var directory = new DirectoryInfo(directoryPath) { Attributes = FileAttributes.Normal };

            foreach (var info in directory.GetFileSystemInfos("*", SearchOption.AllDirectories))
                info.Attributes = FileAttributes.Normal;

            App.Logger.WriteLine("Filesystem::AssertReadOnlyDirectory", $"The following directory was set as read-only: {directoryPath}");
        }
    }
}
