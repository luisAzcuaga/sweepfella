using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Sweepfella
{
    public sealed class Category
    {
        public string Group;
        public string Name;
        public string Description;
        public string AppName;                 // program worth closing before cleaning
        public string[] Processes = new string[0];
        public bool RequiresAdmin;
        public bool DefaultChecked;
        public bool UsesAgeFilter;             // honors "keep files from the last 24 hours"
        public bool IsRecycleBin;
        public readonly List<string> Dirs = new List<string>();          // contents are emptied, the folder itself is kept
        public readonly List<string[]> Patterns = new List<string[]>();  // { folder, pattern }, top level only

        public Category Dir(string path)
        {
            if (!string.IsNullOrEmpty(path)) Dirs.Add(path);
            return this;
        }

        public Category Files(string dir, string pattern)
        {
            if (!string.IsNullOrEmpty(dir)) Patterns.Add(new[] { dir, pattern });
            return this;
        }
    }

    public sealed class Result
    {
        public long Bytes;
        public int Files;
        public int Failed;
    }

    public static class Catalog
    {

        static readonly string[] ChromiumProfileCaches = { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnWebGPUCache", "DawnGraphiteCache" };
        static readonly string[] ChromiumRootCaches = { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" };

        public static List<Category> Build()
        {
            string local = Env.Local, roaming = Env.Roaming, win = Env.Windows, data = Env.ProgramData;
            string system = L.T("cat.group.system");
            var list = new List<Category>();

            list.Add(new Category
            {
                Group = system, Name = L.T("cat.user_temp.name"), DefaultChecked = true, UsesAgeFilter = true,
                Description = L.T("cat.user_temp.desc"),
            }.Dir(Path.GetTempPath()));

            list.Add(new Category
            {
                Group = system, Name = L.T("cat.win_temp.name"), DefaultChecked = true, UsesAgeFilter = true, RequiresAdmin = true,
                Description = L.T("cat.win_temp.desc"),
            }.Dir(Env.Combine(win, "Temp")));

            list.Add(new Category
            {
                Group = system, Name = L.T("cat.recycle.name"), DefaultChecked = true, IsRecycleBin = true,
                Description = L.T("cat.recycle.desc"),
            });

            list.Add(new Category
            {
                Group = system, Name = L.T("cat.wer_user.name"), DefaultChecked = true,
                Description = L.T("cat.wer_user.desc"),
            }.Dir(Env.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"))
             .Dir(Env.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue"))
             .Dir(Env.Combine(local, "CrashDumps")));

            list.Add(new Category
            {
                Group = system, Name = L.T("cat.wer_system.name"), DefaultChecked = true, RequiresAdmin = true,
                Description = L.T("cat.wer_system.desc"),
            }.Dir(Env.Combine(data, "Microsoft", "Windows", "WER", "ReportArchive"))
             .Dir(Env.Combine(data, "Microsoft", "Windows", "WER", "ReportQueue"))
             .Dir(Env.Combine(data, "Microsoft", "Windows", "WER", "Temp"))
             .Dir(Env.Combine(win, "Minidump"))
             .Dir(Env.Combine(win, "LiveKernelReports"))
             .Files(win, "MEMORY.DMP"));

            list.Add(new Category
            {
                Group = system, Name = L.T("cat.thumbs.name"),
                Description = L.T("cat.thumbs.desc"),
            }.Files(Env.Combine(local, "Microsoft", "Windows", "Explorer"), "thumbcache_*.db"));

            list.Add(new Category
            {
                Group = system, Name = L.T("cat.shaders.name"),
                Description = L.T("cat.shaders.desc"),
            }.Dir(Env.Combine(local, "D3DSCache"))
             .Dir(Env.Combine(local, "NVIDIA", "DXCache"))
             .Dir(Env.Combine(local, "NVIDIA", "GLCache"))
             .Dir(Env.Combine(local, "AMD", "DxCache"))
             .Dir(Env.Combine(local, "AMD", "DxcCache"))
             .Dir(Env.Combine(local, "AMD", "VkCache"))
             .Dir(Env.Combine(local, "AMD", "GLCache"))
             .Dir(Env.Combine(local, "Intel", "ShaderCache")));

            AddChromium(list, "Google Chrome", "chrome", Env.Combine(local, "Google", "Chrome", "User Data"));
            AddChromium(list, "Microsoft Edge", "msedge", Env.Combine(local, "Microsoft", "Edge", "User Data"));
            AddChromium(list, "Brave", "brave", Env.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"));
            AddChromium(list, "Vivaldi", "vivaldi", Env.Combine(local, "Vivaldi", "User Data"));
            AddChromium(list, "Opera", "opera", Env.Combine(local, "Opera Software", "Opera Stable"), Env.Combine(roaming, "Opera Software", "Opera Stable"));
            AddChromium(list, "Opera GX", "opera", Env.Combine(local, "Opera Software", "Opera GX Stable"), Env.Combine(roaming, "Opera Software", "Opera GX Stable"));
            AddFirefox(list, Env.Combine(local, "Mozilla", "Firefox", "Profiles"));

            return list;
        }

        static void AddChromium(List<Category> list, string app, string process, params string[] bases)
        {
            var c = new Category
            {
                Group = L.T("cat.group.browsers"), Name = L.T("cat.browser.name", app), AppName = app, Processes = new[] { process }, DefaultChecked = true,
                Description = L.T("cat.browser.desc", app),
            };
            foreach (var b in bases)
            {
                if (b == null || !Directory.Exists(b)) continue;
                var profiles = new List<string> { b };
                profiles.AddRange(Env.SubDirs(b).Where(d => File.Exists(Path.Combine(d, "Preferences"))));
                foreach (var p in profiles)
                    foreach (var sub in ChromiumProfileCaches) AddIfExists(c, Path.Combine(p, sub));
                foreach (var sub in ChromiumRootCaches) AddIfExists(c, Path.Combine(b, sub));
            }
            if (c.Dirs.Count > 0) list.Add(c);
        }

        static void AddFirefox(List<Category> list, string profilesDir)
        {
            var c = new Category
            {
                Group = L.T("cat.group.browsers"), Name = L.T("cat.browser.name", "Mozilla Firefox"), AppName = "Mozilla Firefox", Processes = new[] { "firefox" }, DefaultChecked = true,
                Description = L.T("cat.firefox.desc"),
            };
            foreach (var p in Env.SubDirs(profilesDir))
                foreach (var sub in new[] { "cache2", "startupCache", "thumbnails" }) AddIfExists(c, Path.Combine(p, sub));
            if (c.Dirs.Count > 0) list.Add(c);
        }

        static void AddIfExists(Category c, string dir)
        {
            if (Directory.Exists(dir)) c.Dir(dir);
        }
    }

    public static class Cleaner
    {
        /// <summary>Analyzes (clean = false) or deletes (clean = true) a category's files.</summary>
        public static Result Run(Category c, bool clean, DateTime cutoffUtc, CancellationToken ct)
        {
            var r = new Result();
            if (c.IsRecycleBin)
            {
                Shell.RunSta(() => RecycleBin(clean, r));
                return r;
            }

            DateTime cutoff = c.UsesAgeFilter ? cutoffUtc : DateTime.MinValue;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in c.Dirs)
            {
                if (!PathGuard.IsSafeToEmpty(dir) || !seen.Add(Env.Normalize(dir))) continue;
                var info = new DirectoryInfo(dir);
                if (!info.Exists || Env.IsReparsePoint(info)) continue;
                WalkDir(info, true, clean, cutoff, r, ct);
            }

            foreach (var pattern in c.Patterns)
            {
                if (!Path.IsPathRooted(pattern[0]) || !Directory.Exists(pattern[0])) continue;
                FileInfo[] files;
                try { files = new DirectoryInfo(pattern[0]).GetFiles(pattern[1], SearchOption.TopDirectoryOnly); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Env.IsReparsePoint(f)) HandleFile(f, clean, cutoff, r);
                }
            }
            return r;
        }

        static DateTime Touched(FileSystemInfo info)
        {
            // Creation time matters: installers extract files with old timestamps that are still in use.
            return info.LastWriteTimeUtc > info.CreationTimeUtc ? info.LastWriteTimeUtc : info.CreationTimeUtc;
        }

        static void WalkDir(DirectoryInfo dir, bool isRoot, bool clean, DateTime cutoff, Result r, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            FileSystemInfo[] entries;
            DateTime touched;
            try
            {
                touched = Touched(dir);
                entries = dir.GetFileSystemInfos();
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }

            foreach (var entry in entries)
            {
                if (Env.IsReparsePoint(entry)) continue;   // never follow symlinks or junctions
                var sub = entry as DirectoryInfo;
                if (sub != null) WalkDir(sub, false, clean, cutoff, r, ct);
                else HandleFile((FileInfo)entry, clean, cutoff, r);
            }

            if (clean && !isRoot && (cutoff == DateTime.MinValue || touched <= cutoff))
            {
                try { dir.Delete(false); }   // only if it ended up empty
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        static void HandleFile(FileInfo f, bool clean, DateTime cutoff, Result r)
        {
            long length;
            try
            {
                if (cutoff != DateTime.MinValue && Touched(f) > cutoff) return;
                length = f.Length;
            }
            catch (IOException) { return; }

            if (!clean)
            {
                r.Files++;
                r.Bytes += length;
                return;
            }
            try
            {
                if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
                f.Delete();
                r.Files++;
                r.Bytes += length;
            }
            catch (IOException) { r.Failed++; }
            catch (UnauthorizedAccessException) { r.Failed++; }
        }

        static void RecycleBin(bool clean, Result r)
        {
            var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(Native.SHQUERYRBINFO)) };
            if (Native.SHQueryRecycleBin(null, ref info) != 0) return;
            if (!clean || info.i64NumItems == 0)
            {
                if (!clean) { r.Bytes = info.i64Size; r.Files = (int)info.i64NumItems; }
                return;
            }
            int hr = Native.SHEmptyRecycleBin(IntPtr.Zero, null, Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);
            if (hr == 0) { r.Bytes = info.i64Size; r.Files = (int)info.i64NumItems; }
            else r.Failed = (int)info.i64NumItems;
        }
    }
}
