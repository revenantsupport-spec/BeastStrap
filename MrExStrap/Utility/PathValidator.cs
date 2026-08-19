using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeastStrap.Utility
{
    internal static class PathValidator
    {
        public enum ValidationResult
        {
            Ok,
            IllegalCharacter,
            ReservedFileName,
            ReservedDirectoryName
        }

        private static readonly string[] _reservedNames = new string[]
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9"
        };

        private static readonly char[] _directorySeperatorDelimiters = new char[]
        {
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        };

        private static readonly char[] _invalidPathChars = GetInvalidPathChars();

        public static char[] GetInvalidPathChars()
        {
            char[] invalids = new char[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
            char[] otherInvalids = Path.GetInvalidPathChars();

            char[] result = new char[invalids.Length + otherInvalids.Length];
            invalids.CopyTo(result, 0);
            otherInvalids.CopyTo(result, invalids.Length);

            return result;
        }

        public static ValidationResult IsFileNameValid(string fileName)
        {
            if (fileName.IndexOfAny(_invalidPathChars) != -1)
                return ValidationResult.IllegalCharacter;

            // "." and ".." are made entirely of legal characters and are not reserved names, so
            // they used to sail through — and a custom theme named ".." made CreateCustomTheme
            // run Directory.Delete(CustomThemes\.., recursive) i.e. a recursive delete of the
            // install root. Trailing dots and spaces are rejected too: Win32 silently strips them,
            // so "foo." and "foo" resolve to the same directory and a delete can hit the wrong one.
            string trimmed = fileName.Trim();
            if (trimmed.Length == 0 || trimmed.All(c => c == '.'))
                return ValidationResult.IllegalCharacter;

            if (fileName != trimmed || fileName.EndsWith('.'))
                return ValidationResult.IllegalCharacter;

            string fileNameNoExt = Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant();
            if (_reservedNames.Contains(fileNameNoExt))
                return ValidationResult.ReservedFileName;

            return ValidationResult.Ok;
        }

        /// <summary>
        /// True when <paramref name="candidate"/> resolves to something inside <paramref name="root"/>.
        /// Belt-and-braces for anywhere a user-supplied name is combined into a path that is later
        /// deleted — Path.Combine does not normalise, but Win32 does.
        /// </summary>
        public static bool IsContainedIn(string candidate, string root)
        {
            try
            {
                string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return Path.GetFullPath(candidate).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static ValidationResult IsPathValid(string path)
        {
            string? pathRoot = Path.GetPathRoot(path);
            string pathNoRoot = pathRoot != null ? path[pathRoot.Length..] : path;

            string[] pathParts = pathNoRoot.Split(_directorySeperatorDelimiters);

            foreach (var part in pathParts)
            {
                if (part.IndexOfAny(_invalidPathChars) != -1)
                    return ValidationResult.IllegalCharacter;

                if (_reservedNames.Contains(part))
                    return ValidationResult.ReservedDirectoryName;
            }

            string fileName = Path.GetFileName(path);
            if (fileName.IndexOfAny(_invalidPathChars) != -1)
                return ValidationResult.IllegalCharacter;

            string fileNameNoExt = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
            if (_reservedNames.Contains(fileNameNoExt))
                return ValidationResult.ReservedFileName;

            return ValidationResult.Ok;
        }
    }
}
