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
        public string Id;                      // stable, language-independent (remembers the user's selection)
        public string Group;
        public string Name;
        public string Description;
        public string AppName;                 // program worth closing before cleaning
        public string[] Processes = new string[0];
        public bool RequiresAdmin;
        public bool DefaultChecked;
        public bool UsesAgeFilter;             // honors "keep files from the last 24 hours"
        public bool IsRecycleBin;
        public bool IsRestorePoints;           // System Restore points but the newest; Windows doesn't report their size
        public long SizeUpTo = -1;             // restore points: the most cleaning could free (all shadow storage), from the last look
        public bool RemoveEmptyDirs;           // also delete each Dirs folder itself once it ends up empty
        public readonly List<string> Dirs = new List<string>();          // contents are emptied, the folder itself is kept
        public readonly List<string> EmptyDirs = new List<string>();     // deleted only if already empty, contents never touched
        public readonly List<string[]> Patterns = new List<string[]>();  // { folder, pattern }, top level only
        public Func<IEnumerable<string>> FileSource;  // files found by custom logic (e.g. older Sweepfella versions)
        public Action<string> AfterDelete;            // called with each file deleted from FileSource

        public Category Dir(string path)
        {
            if (!string.IsNullOrEmpty(path)) Dirs.Add(path);
            return this;
        }

        public Category IfEmpty(string path)
        {
            if (!string.IsNullOrEmpty(path)) EmptyDirs.Add(path);
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
                Id = "user_temp", Group = system, Name = L.T("cat.user_temp.name"), DefaultChecked = true, UsesAgeFilter = true,
                Description = L.T("cat.user_temp.desc"),
            }.Dir(Path.GetTempPath()));

            list.Add(new Category
            {
                Id = "win_temp", Group = system, Name = L.T("cat.win_temp.name"), DefaultChecked = true, UsesAgeFilter = true, RequiresAdmin = true,
                Description = L.T("cat.win_temp.desc"),
            }.Dir(Env.Combine(win, "Temp")));

            list.Add(new Category
            {
                Id = "recycle", Group = system, Name = L.T("cat.recycle.name"), DefaultChecked = true, IsRecycleBin = true,
                Description = L.T("cat.recycle.desc"),
            });

            list.Add(new Category
            {
                Id = "wer_user", Group = system, Name = L.T("cat.wer_user.name"), DefaultChecked = true,
                Description = L.T("cat.wer_user.desc"),
            }.Dir(Env.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"))
             .Dir(Env.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue"))
             .Dir(Env.Combine(local, "CrashDumps")));

            list.Add(new Category
            {
                Id = "wer_system", Group = system, Name = L.T("cat.wer_system.name"), DefaultChecked = true, RequiresAdmin = true,
                Description = L.T("cat.wer_system.desc"),
            }.Dir(Env.Combine(data, "Microsoft", "Windows", "WER", "ReportArchive"))
             .Dir(Env.Combine(data, "Microsoft", "Windows", "WER", "ReportQueue"))
             .Dir(Env.Combine(data, "Microsoft", "Windows", "WER", "Temp"))
             .Dir(Env.Combine(win, "Minidump"))
             .Dir(Env.Combine(win, "LiveKernelReports"))
             .Files(win, "MEMORY.DMP"));

            list.Add(new Category
            {
                Id = "old_versions", Group = system, Name = L.T("cat.old_versions.name"), DefaultChecked = true,
                Description = L.T("cat.old_versions.desc"),
                FileSource = OldVersions.Find, AfterDelete = OldVersions.CleanUpAfter,
            });

            list.Add(new Category
            {
                Id = "thumbs", Group = system, Name = L.T("cat.thumbs.name"),
                Description = L.T("cat.thumbs.desc"),
            }.Files(Env.Combine(local, "Microsoft", "Windows", "Explorer"), "thumbcache_*.db"));

            list.Add(new Category
            {
                Id = "shaders", Group = system, Name = L.T("cat.shaders.name"),
                Description = L.T("cat.shaders.desc"),
            }.Dir(Env.Combine(local, "D3DSCache"))
             .Dir(Env.Combine(local, "NVIDIA", "DXCache"))
             .Dir(Env.Combine(local, "NVIDIA", "GLCache"))
             .Dir(Env.Combine(local, "AMD", "DxCache"))
             .Dir(Env.Combine(local, "AMD", "DxcCache"))
             .Dir(Env.Combine(local, "AMD", "VkCache"))
             .Dir(Env.Combine(local, "AMD", "GLCache"))
             .Dir(Env.Combine(local, "Intel", "ShaderCache"))
             .Dir(Env.Combine(Path.GetPathRoot(win), ".cache", "AMD", "DxcCache")));   // seen written there by AMD's driver

            // Driver packages unpack here and never clean up after themselves. The root ACL lets any user
            // modify them, so no admin is needed; the age filter skips an install that's still running.
            // The vendors' tools recreate these folders when they need them, so empty ones go too.
            string drive = Path.GetPathRoot(win);
            list.Add(new Category
            {
                Id = "driver_installers", Group = system, Name = L.T("cat.driver_installers.name"), UsesAgeFilter = true, RemoveEmptyDirs = true,
                Description = L.T("cat.driver_installers.desc", Env.Combine(drive, "AMD"), Env.Combine(drive, "NVIDIA"), Env.Combine(drive, "Intel"), Env.Combine(drive, "MSI")),
            }.Dir(Env.Combine(drive, "AMD"))
             .Dir(Env.Combine(drive, "NVIDIA"))
             .Dir(Env.Combine(drive, "Intel"))
             .IfEmpty(Env.Combine(drive, "MSI")));

            // Known Windows SDK installer bug: the 32-bit Application Verifier DLLs meant for SysWOW64 land in
            // the drive root. They carry a High integrity label, so deleting them needs admin.
            list.Add(new Category
            {
                Id = "sdk_strays", Group = system, Name = L.T("cat.sdk_strays.name"), DefaultChecked = true, RequiresAdmin = true,
                Description = L.T("cat.sdk_strays.desc", drive),
            }.Files(drive, "appverifUI.dll")
             .Files(drive, "vfcompat.dll"));

            // A safety net, so never on by default; the newest point always stays.
            list.Add(new Category
            {
                Id = "restore_points", Group = system, Name = L.T("cat.restore_points.name"), RequiresAdmin = true, UsesAgeFilter = true, IsRestorePoints = true,
                Description = L.T("cat.restore_points.desc"),
            });

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
                Id = "browser." + app.Replace(" ", "").ToLowerInvariant(), Group = L.T("cat.group.browsers"), Name = L.T("cat.browser.name", app), AppName = app, Processes = new[] { process }, DefaultChecked = true,
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
                Id = "browser.firefox", Group = L.T("cat.group.browsers"), Name = L.T("cat.browser.name", "Mozilla Firefox"), AppName = "Mozilla Firefox", Processes = new[] { "firefox" }, DefaultChecked = true,
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

    /// <summary>One thing a category would delete: a top-level file or folder, or a Recycle Bin item.</summary>
    public sealed class Entry
    {
        public string Path;        // what Run's only set refers to
        public string Name;
        public string Location;    // containing folder (for the Recycle Bin, where it was deleted from)
        public DateTime? Deleted;  // Recycle Bin items only
        public string Summary;     // shown when selected, instead of the path (restore points)
        public long Bytes;
        public int Files;
    }

    public static class Cleaner
    {
        /// <summary>Analyzes (clean = false) or deletes (clean = true) a category's files.</summary>
        public static Result Run(Category c, bool clean, DateTime cutoffUtc, CancellationToken ct)
        {
            return Run(c, clean, cutoffUtc, null, ct);
        }

        /// <summary>
        /// Same, but when only is given, handles just the entries whose Path is in it (as listed by List), so
        /// files that appeared after the user picked what to delete are left alone.
        /// </summary>
        public static Result Run(Category c, bool clean, DateTime cutoffUtc, ICollection<string> only, CancellationToken ct)
        {
            var r = new Result();
            if (c.IsRecycleBin)
            {
                if (only == null) Shell.RunSta(() => EmptyRecycleBin(clean, r));
                else RecycleBinItems(clean, only, r, ct);
                return r;
            }
            if (c.IsRestorePoints)
            {
                RestorePointItems(c, clean, c.UsesAgeFilter ? cutoffUtc : DateTime.MinValue, only, r, ct);
                return r;
            }
            Visit(c, clean, cutoffUtc, only, ct, (e, part) =>
            {
                r.Files += part.Files;
                r.Bytes += part.Bytes;
                r.Failed += part.Failed;
            });
            return r;
        }

        /// <summary>What Run would delete, one entry per top-level file or folder, largest first.</summary>
        public static List<Entry> List(Category c, DateTime cutoffUtc, CancellationToken ct)
        {
            var list = new List<Entry>();
            if (c.IsRecycleBin) list.AddRange(RecycleBin.Items().Select(i => i.ToEntry()));
            else if (c.IsRestorePoints)
            {
                list.AddRange(RestorePoints.Old(c.UsesAgeFilter ? cutoffUtc : DateTime.MinValue).Select(RestorePointEntry));
                c.SizeUpTo = list.Count > 0 ? RestorePoints.StorageUsed() : 0;
                return list;   // oldest first, like Windows lists them
            }
            else Visit(c, false, cutoffUtc, null, ct, (e, part) => { if (e != null) list.Add(e); });
            return list.OrderByDescending(e => e.Bytes).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Walks every top-level file and folder a category covers, deleting them when clean is set, and reports
        /// each with what it held. The entry is null for things that wouldn't be listed (nothing to delete there).
        /// </summary>
        static void Visit(Category c, bool clean, DateTime cutoffUtc, ICollection<string> only, CancellationToken ct, Action<Entry, Result> report)
        {
            DateTime cutoff = c.UsesAgeFilter ? cutoffUtc : DateTime.MinValue;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in c.Dirs)
            {
                if (!PathGuard.IsSafeToEmpty(dir) || !seen.Add(Env.Normalize(dir))) continue;
                var root = new DirectoryInfo(dir);
                if (!root.Exists || Env.IsReparsePoint(root)) continue;
                DateTime rootTouched;
                FileSystemInfo[] entries;
                try
                {
                    rootTouched = Touched(root);
                    entries = root.GetFileSystemInfos();
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Env.IsReparsePoint(entry) || !Allowed(only, entry.FullName)) continue;   // never follow symlinks or junctions
                    var part = new Result();
                    var sub = entry as DirectoryInfo;
                    bool listed;
                    if (sub != null)
                    {
                        bool emptyAndOld = !clean && IsOld(sub, cutoff) && !HasAnyFile(sub);   // checked first: cleaning would remove it
                        WalkDir(sub, true, clean, cutoff, part, ct);
                        listed = part.Files > 0 || emptyAndOld;
                    }
                    else listed = HandleFile((FileInfo)entry, clean, cutoff, part) || part.Files > 0;
                    report(listed ? MakeEntry(entry, root.FullName, part) : null, part);
                }
                if (clean && c.RemoveEmptyDirs && (cutoff == DateTime.MinValue || rootTouched <= cutoff)) TryDeleteEmpty(root);
            }

            foreach (var dir in c.EmptyDirs)
            {
                if (!PathGuard.IsSafeToEmpty(dir) || !Allowed(only, dir)) continue;
                var info = new DirectoryInfo(dir);
                try
                {
                    if (!info.Exists || Env.IsReparsePoint(info) || !IsOld(info, cutoff)) continue;
                    if (clean) TryDeleteEmpty(info);   // fails, harmlessly, unless it's empty
                    else if (!HasAnyEntry(info)) report(MakeEntry(info, info.Parent == null ? "" : info.Parent.FullName, new Result()), new Result());
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
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
                    if (Env.IsReparsePoint(f) || !Allowed(only, f.FullName)) continue;
                    var part = new Result();
                    HandleFile(f, clean, cutoff, part);
                    report(part.Files > 0 ? MakeEntry(f, pattern[0], part) : null, part);
                }
            }

            if (c.FileSource != null)
                foreach (var path in c.FileSource())
                {
                    ct.ThrowIfCancellationRequested();
                    var f = new FileInfo(path);
                    if (!f.Exists || !Allowed(only, f.FullName)) continue;
                    var part = new Result();
                    if (HandleFile(f, clean, cutoff, part) && clean && c.AfterDelete != null) c.AfterDelete(path);
                    report(part.Files > 0 ? MakeEntry(f, f.DirectoryName, part) : null, part);
                }
        }

        static bool Allowed(ICollection<string> only, string path)
        {
            return only == null || only.Contains(path);
        }

        static Entry MakeEntry(FileSystemInfo info, string location, Result part)
        {
            return new Entry { Path = info.FullName, Name = info.Name, Location = location, Bytes = part.Bytes, Files = part.Files };
        }

        static bool IsOld(FileSystemInfo info, DateTime cutoff)
        {
            return cutoff == DateTime.MinValue || Touched(info) <= cutoff;
        }

        static void TryDeleteEmpty(DirectoryInfo dir)
        {
            try { dir.Delete(false); }   // only if it's empty
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static bool HasAnyEntry(DirectoryInfo dir)
        {
            using (var e = dir.EnumerateFileSystemInfos().GetEnumerator()) return e.MoveNext();
        }

        /// <summary>True if any file lies under dir (empty subfolders don't count; reparse points are left alone).</summary>
        static bool HasAnyFile(DirectoryInfo dir)
        {
            try
            {
                foreach (var e in dir.EnumerateFileSystemInfos())
                {
                    if (Env.IsReparsePoint(e)) return true;   // can't be deleted by the walk, so the folder stays
                    var sub = e as DirectoryInfo;
                    if (sub == null || HasAnyFile(sub)) return true;
                }
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
            return false;
        }

        /// <summary>Deletes (or counts) the Recycle Bin items in only, one by one.</summary>
        static void RecycleBinItems(bool clean, ICollection<string> only, Result r, CancellationToken ct)
        {
            foreach (var item in RecycleBin.Items())
            {
                ct.ThrowIfCancellationRequested();
                if (!only.Contains(item.DataPath)) continue;
                if (!clean)
                {
                    r.Files++;
                    r.Bytes += item.Size;
                }
                else if (RecycleBin.Destroy(item))
                {
                    r.Files++;
                    r.Bytes += item.Size;
                }
                else r.Failed++;
            }
            if (clean) RecycleBin.RefreshIcon();
        }

        static Entry RestorePointEntry(RestorePoints.Point p)
        {
            string name = p.Description.Length > 0 ? p.Description : L.T("cat.restore_points.unnamed");
            return new Entry
            {
                Path = p.Key, Name = name, Files = 1,
                Location = L.T("cat.restore_points.created", p.Created.ToString("g"), Ui.Ago(p.Created)),
                Summary = L.T("cat.restore_points.entry", name, p.Created.ToString("g"), Ui.Ago(p.Created)),
            };
        }

        /// <summary>
        /// Counts (or deletes) the restore points a cleanup may remove. Space freed is the drop in shadow storage,
        /// the only figure Windows gives; analyzing records the current total as the most that could be freed.
        /// </summary>
        static void RestorePointItems(Category c, bool clean, DateTime cutoffUtc, ICollection<string> only, Result r, CancellationToken ct)
        {
            var old = RestorePoints.Old(cutoffUtc).Where(p => Allowed(only, p.Key)).ToList();
            long before = old.Count > 0 ? RestorePoints.StorageUsed() : 0;
            if (!clean)
            {
                r.Files = old.Count;
                c.SizeUpTo = before;
                return;
            }
            foreach (var p in old)
            {
                ct.ThrowIfCancellationRequested();
                if (RestorePoints.Remove(p)) r.Files++;
                else r.Failed++;
            }
            if (r.Files == 0) return;
            long after = RestorePoints.StorageUsed();
            if (before >= 0 && after >= 0) r.Bytes = Math.Max(0, before - after);
            c.SizeUpTo = after;
        }

        /// <summary>Deletes a whole file or folder tree (used for Recycle Bin items). True if nothing is left.</summary>
        internal static bool DeleteTree(FileSystemInfo info)
        {
            var r = new Result();
            var dir = info as DirectoryInfo;
            if (dir != null)
            {
                WalkDir(dir, true, true, DateTime.MinValue, r, CancellationToken.None);
                dir.Refresh();
                return !dir.Exists;
            }
            return HandleFile((FileInfo)info, true, DateTime.MinValue, r);
        }

        static DateTime Touched(FileSystemInfo info)
        {
            // Creation time matters: installers extract files with old timestamps that are still in use.
            return info.LastWriteTimeUtc > info.CreationTimeUtc ? info.LastWriteTimeUtc : info.CreationTimeUtc;
        }

        static void WalkDir(DirectoryInfo dir, bool deleteIfEmpty, bool clean, DateTime cutoff, Result r, CancellationToken ct)
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
                if (sub != null) WalkDir(sub, true, clean, cutoff, r, ct);
                else HandleFile((FileInfo)entry, clean, cutoff, r);
            }

            if (clean && deleteIfEmpty && (cutoff == DateTime.MinValue || touched <= cutoff))
            {
                try { dir.Delete(false); }   // only if it ended up empty
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>Counts (or deletes) one file. Returns true if it was deleted.</summary>
        static bool HandleFile(FileInfo f, bool clean, DateTime cutoff, Result r)
        {
            long length;
            try
            {
                if (cutoff != DateTime.MinValue && Touched(f) > cutoff) return false;
                length = f.Length;
            }
            catch (IOException) { return false; }

            if (!clean)
            {
                r.Files++;
                r.Bytes += length;
                return false;
            }
            try
            {
                if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
                f.Delete();
                r.Files++;
                r.Bytes += length;
                return true;
            }
            catch (IOException) { r.Failed++; }
            catch (UnauthorizedAccessException) { r.Failed++; }
            return false;
        }

        static void EmptyRecycleBin(bool clean, Result r)
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
