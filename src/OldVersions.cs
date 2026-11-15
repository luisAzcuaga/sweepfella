using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sweepfella
{
    /// <summary>
    /// Finds copies of older Sweepfella versions (and their release zips) so they can be cleaned up.
    /// Only versions older than the running one are ever returned: an old copy never removes a newer one.
    /// </summary>
    static class OldVersions
    {
        const string Product = "Sweepfella";
        const int SearchDepth = 3;   // e.g. Downloads\Sweepfella-0.2.0\Sweepfella\Sweepfella.exe
        static readonly Regex ZipName = new Regex(@"^Sweepfella[-_ ]v?(\d+\.\d+(?:\.\d+)?)(?: \(\d+\))?\.zip$", RegexOptions.IgnoreCase);

        static Version Current
        {
            get { var v = Updates.Current; return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }
        }

        static string Self
        {
            get { return Env.Normalize(typeof(OldVersions).Assembly.Location); }
        }

        public static IEnumerable<string> Find()
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = new[]
            {
                Tuple.Create(Env.Combine(Env.UserProfile, "Downloads"), SearchDepth),
                Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), SearchDepth),
                Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), SearchDepth),
                Tuple.Create(Path.GetDirectoryName(Self), 0),
            };
            foreach (var root in roots)
                if (!string.IsNullOrEmpty(root.Item1) && Directory.Exists(root.Item1))
                    Walk(new DirectoryInfo(root.Item1), root.Item2, found);
            return found;
        }

        static void Walk(DirectoryInfo dir, int depth, HashSet<string> found)
        {
            try
            {
                foreach (var f in dir.EnumerateFiles())
                {
                    string ext = f.Extension.ToLowerInvariant();
                    if (ext == ".exe" && IsOlderCopy(f.FullName)) found.Add(f.FullName);
                    else if (ext == ".zip")
                    {
                        var m = ZipName.Match(f.Name);
                        Version v;
                        if (m.Success && Version.TryParse(m.Groups[1].Value, out v) && Normalize(v) < Current) found.Add(f.FullName);
                    }
                }
                if (depth <= 0) return;
                foreach (var sub in dir.EnumerateDirectories())
                    if (!Env.IsReparsePoint(sub)) Walk(sub, depth - 1, found);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }

        static bool IsSweepfella(string exe)
        {
            try { return string.Equals(FileVersionInfo.GetVersionInfo(exe).ProductName, Product, StringComparison.Ordinal); }
            catch (IOException) { return false; }
        }

        static bool IsOlderCopy(string exe)
        {
            if (string.Equals(Env.Normalize(exe), Self, StringComparison.OrdinalIgnoreCase) || !IsSweepfella(exe)) return false;
            Version v;
            try { if (!Version.TryParse(FileVersionInfo.GetVersionInfo(exe).FileVersion, out v)) return false; }
            catch (IOException) { return false; }
            return Normalize(v) < Current;
        }

        static Version Normalize(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        /// <summary>
        /// After an old executable is deleted, removes what it left next to it once no other copy remains there:
        /// its Sweepfella.ini, the LICENSE.txt from the release zip, and the folder itself if it ends up empty.
        /// </summary>
        public static void CleanUpAfter(string deleted)
        {
            if (!deleted.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            string dir = Path.GetDirectoryName(deleted);
            try
            {
                if (Directory.EnumerateFiles(dir, "*.exe").Any(IsSweepfella)) return;   // another copy still lives here
                TryDelete(Path.Combine(dir, Settings.FileName));
                string license = Path.Combine(dir, "LICENSE.txt");
                if (File.Exists(license))
                {
                    string text = File.ReadAllText(license);
                    if (text.Contains("MIT License") && text.Contains("Luis Azcuaga")) TryDelete(license);
                }
                // Remove now-empty folders named after Sweepfella (the zip's "Sweepfella" folder and its parent, if any).
                while (dir != null && Path.GetFileName(dir).StartsWith(Product, StringComparison.OrdinalIgnoreCase)
                       && PathGuard.IsSafeToEmpty(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
