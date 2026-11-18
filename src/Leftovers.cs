using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace Sweepfella
{
    public sealed class Leftover
    {
        public string Path;
        public string Name;
        public string Location;      // readable label, e.g. "AppData\Roaming"
        public bool RequiresAdmin;
        public long Bytes = -1;      // -1 = not measured (folders in use aren't, to keep the search fast)
        public int Files;
        public DateTime LastChange;
        public Ownership Owner;      // null for leftovers; who uses it otherwise
        public bool SteamOrphan;     // a folder in a Steam library whose game is no longer installed

        public bool InUse { get { return Owner != null; } }
        public bool RecentlyUsed { get { return (DateTime.Now - LastChange).TotalDays < 30; } }
    }

    /// <summary>What <see cref="LeftoverFinder.Investigate"/> found inside a folder.</summary>
    public sealed class Clues
    {
        public readonly List<string> Running = new List<string>();     // processes running from inside the folder
        public readonly List<string> Sources = new List<string>();     // "Company — Product" of the executables inside
        public readonly List<KeyValuePair<string, long>> Contents = new List<KeyValuePair<string, long>>();   // top-level entries by size
        public string LatestFile;                                      // relative path of the most recently changed file
        public DateTime LatestChange;
        public bool OnlyCaches;                                        // nothing but caches and logs
        public readonly List<string> UnityGames = new List<string>();  // games made with Unity that keep data here
        public bool WebView;                                           // holds a WebView2 (embedded Edge) profile
        public bool Truncated;                                         // too many files: sizes are partial
        public long TotalBytes;
        public int TotalFiles;
    }

    sealed class ScanLocation
    {
        public string Path, Label;
        public bool RequiresAdmin;
        public bool Steam;   // a Steam library's steamapps\common
        public ScanLocation(string path, string label, bool admin) { Path = path; Label = label; RequiresAdmin = admin; }
    }

    /// <summary>
    /// Finds top-level folders in AppData, ProgramData and Program Files that don't belong to any
    /// installed program, service, process, shortcut or driver.
    /// When in doubt a folder counts as "in use": missing a leftover beats suggesting to delete something in use.
    /// Folders in use are returned too, with their <see cref="Leftover.Owner"/>, so the UI can explain them.
    /// </summary>
    public static class LeftoverFinder
    {
        // Windows or shared folders that are never suggested.
        static readonly HashSet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Packages", "Temp", "Programs", "Comms", "ConnectedDevicesPlatform", "CrashDumps", "D3DSCache", "PeerDistRepub",
            "PlaceholderTileLogoFolder", "Publishers", "VirtualStore", "IsolatedStorage", "History", "Application Data",
            "Temporary Internet Files", "ElevatedDiagnostics", "Diagnostics", "SquirrelTemp", "Package Cache", "regid.1991-06.com.microsoft",
            "ssh", "USOPrivate", "USOShared", "SoftwareDistribution", "Desktop", "Documents", "Start Menu", "Templates", "Favorites",
            "Common Files", "Internet Explorer", "ModifiableWindowsApps", "Reference Assemblies", "MSBuild", "Uninstall Information",
            "dotnet", "PackageManagement", "WindowsPowerShell", "dbg", "Downloaded Installations", "NuGet", "IconCache",
            "Steamworks Shared",   // Steam's shared redistributables, no manifest of its own
            "GroupPolicy", "VoiceAccess", "speech", "CEF", "comgr", "ToastNotificationManagerCompat", "Unity",   // LocalLow\Unity is shared by every Unity game
        };

        static readonly Regex NonAlnum = new Regex(@"[^a-z0-9]", RegexOptions.Compiled);

        public static string Normalize(string name)
        {
            return NonAlnum.Replace((name ?? "").ToLowerInvariant(), "");
        }

        static IEnumerable<ScanLocation> Locations()
        {
            yield return new ScanLocation(Env.Roaming, @"AppData\Roaming", false);
            yield return new ScanLocation(Env.Local, @"AppData\Local", false);
            yield return new ScanLocation(Env.LocalLow, @"AppData\LocalLow", false);
            yield return new ScanLocation(Env.Combine(Env.Local, "Programs"), @"AppData\Local\Programs", false);
            yield return new ScanLocation(Env.ProgramData, "ProgramData", true);
            yield return new ScanLocation(Env.ProgramFiles, L.T("loc.program_files"), true);
            if (!string.Equals(Env.ProgramFiles, Env.ProgramFilesX86, StringComparison.OrdinalIgnoreCase))
                yield return new ScanLocation(Env.ProgramFilesX86, L.T("loc.program_files_x86"), true);
            foreach (var common in UsageScanner.SteamLibraries())
                yield return new ScanLocation(common, L.T("loc.steam_library"), false) { Steam = true };
        }

        public static List<Leftover> Find(ICollection<string> userIgnored, IProgress<string> progress, CancellationToken ct)
        {
            var locations = Locations().Where(l => !string.IsNullOrEmpty(l.Path) && Directory.Exists(l.Path)).ToList();
            if (progress != null) progress.Report(L.T("leftovers.collecting"));
            var evidence = Evidence.Collect(ProgramRegistry.Read(true), locations.Select(l => l.Path));

            var result = new List<Leftover>();
            foreach (var loc in locations)
            {
                foreach (var dir in Env.SubDirs(loc.Path))
                {
                    ct.ThrowIfCancellationRequested();
                    string name = System.IO.Path.GetFileName(dir);
                    if (Ignored.Contains(name) || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".")
                        || name.StartsWith("{")) continue;
                    if (userIgnored != null && userIgnored.Contains(dir)) continue;
                    DirectoryInfo info;
                    try
                    {
                        info = new DirectoryInfo(dir);
                        if (Env.IsReparsePoint(info)) continue;
                    }
                    catch (IOException) { continue; }
                    if (!PathGuard.IsSafeToEmpty(dir)) continue;

                    var item = new Leftover
                    {
                        Path = dir, Name = name, Location = loc.Label, RequiresAdmin = loc.RequiresAdmin,
                        LastChange = info.LastWriteTime, Owner = evidence.OwnerOf(dir),
                    };
                    // A name match alone doesn't make an empty folder "in use": there's nothing in it to break.
                    if (item.InUse && !item.Owner.Certain && !ContainsAnyFile(info)) item.Owner = null;
                    item.SteamOrphan = loc.Steam && !item.InUse;
                    if (!item.InUse)
                    {
                        if (progress != null) progress.Report(dir);
                        item.Bytes = 0;
                        Measure(info, item, ct);
                    }
                    result.Add(item);
                }
            }
            return result;
        }

        static bool ContainsAnyFile(DirectoryInfo dir)
        {
            try
            {
                foreach (var e in dir.EnumerateFileSystemInfos())
                {
                    if (Env.IsReparsePoint(e)) continue;
                    var sub = e as DirectoryInfo;
                    if (sub == null || ContainsAnyFile(sub)) return true;
                }
            }
            catch (IOException) { return true; }               // unreadable: assume it has content
            catch (UnauthorizedAccessException) { return true; }
            return false;
        }

        static void Measure(DirectoryInfo dir, Leftover item, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            FileSystemInfo[] entries;
            try { entries = dir.GetFileSystemInfos(); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            foreach (var e in entries)
            {
                if (Env.IsReparsePoint(e)) continue;
                try { if (e.LastWriteTime > item.LastChange) item.LastChange = e.LastWriteTime; }
                catch (IOException) { }
                var sub = e as DirectoryInfo;
                if (sub != null) { Measure(sub, item, ct); continue; }
                try { item.Bytes += ((FileInfo)e).Length; item.Files++; }
                catch (IOException) { }
            }
        }

        // ---- Clues about who owns a folder ----

        // Folder names that only hold regenerable data: "cache", "GPUCache", "packed-db-cache", "logs", "crashpad"...
        static readonly Regex CacheName = new Regex(@"cache|^logs?$|^te?mp$|crash|^blob_storage$", RegexOptions.IgnoreCase);

        const int MaxFilesToInspect = 100000;
        const int MaxVersionInfos = 60;

        /// <summary>Looks inside a folder for hints about who created it and whether it's still in use.</summary>
        public static Clues Investigate(string dir, CancellationToken ct)
        {
            var clues = new Clues();
            var info = new DirectoryInfo(dir);
            FileSystemInfo[] top;
            try { top = info.GetFileSystemInfos().Where(e => !Env.IsReparsePoint(e)).ToArray(); }
            catch (IOException) { return clues; }
            catch (UnauthorizedAccessException) { return clues; }

            var walk = new ClueWalk { Root = Env.Normalize(dir), Clues = clues, Token = ct };
            foreach (var entry in top)
            {
                long before = walk.Bytes;
                var sub = entry as DirectoryInfo;
                if (sub != null) walk.Dir(sub);
                else walk.File((FileInfo)entry);
                clues.Contents.Add(new KeyValuePair<string, long>(entry.Name, walk.Bytes - before));
            }
            clues.Contents.Sort((a, b) => b.Value.CompareTo(a.Value));
            clues.TotalBytes = walk.Bytes;
            clues.TotalFiles = walk.Files;
            clues.OnlyCaches = top.Length > 0 && top.All(e => e is DirectoryInfo
                ? CacheName.IsMatch(e.Name)
                : e.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase));

            // Publishers, most frequent first; Microsoft only counts when nothing else shows up (bundled runtimes).
            var ranked = walk.Sources.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
            var thirdParty = ranked.Where(s => !s.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)).ToList();
            clues.Sources.AddRange((thirdParty.Count > 0 ? thirdParty : ranked).Take(3));

            string prefix = walk.Root + "\\";
            int self;
            using (var current = Process.GetCurrentProcess()) self = current.Id;
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id == self) continue;
                    try
                    {
                        if (p.MainModule.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !clues.Running.Contains(p.ProcessName))
                            clues.Running.Add(p.ProcessName);
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            }
            return clues;
        }

        sealed class ClueWalk
        {
            public string Root;
            public Clues Clues;
            public CancellationToken Token;
            public long Bytes;
            public int Files, VersionInfos;
            public readonly Dictionary<string, int> Sources = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public void Dir(DirectoryInfo dir)
            {
                Token.ThrowIfCancellationRequested();
                if (Files >= MaxFilesToInspect) { Clues.Truncated = true; return; }
                FileSystemInfo[] entries;
                try { entries = dir.GetFileSystemInfos(); }
                catch (IOException) { return; }
                catch (UnauthorizedAccessException) { return; }
                foreach (var e in entries)
                {
                    if (Env.IsReparsePoint(e)) continue;
                    var sub = e as DirectoryInfo;
                    if (sub != null)
                    {
                        if (sub.Name.Equals("EBWebView", StringComparison.OrdinalIgnoreCase)) Clues.WebView = true;
                        Dir(sub);
                    }
                    else File((FileInfo)e);
                }
            }

            public void File(FileInfo f)
            {
                Files++;
                try
                {
                    Bytes += f.Length;
                    if (f.LastWriteTime > Clues.LatestChange)
                    {
                        Clues.LatestChange = f.LastWriteTime;
                        Clues.LatestFile = f.FullName.Length > Root.Length ? f.FullName.Substring(Root.Length + 1) : f.Name;
                    }
                }
                catch (IOException) { return; }

                // Unity games write Player.log next to their settings and saves: LocalLow\<Company>\<Game>\Player.log
                if (f.Name.Equals("Player.log", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("Player-prev.log", StringComparison.OrdinalIgnoreCase))
                {
                    string game = f.DirectoryName.Length > Root.Length ? f.Directory.Name : Path.GetFileName(Root);
                    if (!Clues.UnityGames.Contains(game)) Clues.UnityGames.Add(game);
                }

                string ext = f.Extension.ToLowerInvariant();
                if ((ext != ".exe" && ext != ".dll") || VersionInfos >= MaxVersionInfos) return;
                if (Knowledge.VcFamilyOfDll(f.Name) != null || f.Name.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase)) return;
                VersionInfos++;
                try
                {
                    var v = FileVersionInfo.GetVersionInfo(f.FullName);
                    string company = (v.CompanyName ?? "").Trim();
                    string product = (v.ProductName ?? "").Trim();
                    if (company.Length == 0 && product.Length == 0) return;
                    string key = company.Length == 0 ? product
                               : product.Length == 0 || product.Equals(company, StringComparison.OrdinalIgnoreCase) ? company
                               : company + " — " + product;
                    int n;
                    Sources[key] = Sources.TryGetValue(key, out n) ? n + 1 : 1;
                }
                catch (IOException) { }
            }
        }

        /// <summary>
        /// Sends the folder to the Recycle Bin, or deletes it permanently when the user was told it can't go there
        /// (see <see cref="RecycleBin.Predict"/>). <paramref name="owner"/> is the window Windows' own prompts belong to.
        /// </summary>
        public static void Remove(string dir, bool permanently, IntPtr owner)
        {
            if (!PathGuard.IsSafeToEmpty(dir)) throw new InvalidOperationException(L.T("leftovers.err.protected", dir));
            if (permanently) RecycleBin.DeletePermanently(dir, owner);
            else RecycleBin.Move(dir, owner);
        }

        // ---- Folders the user chose to ignore (persisted) ----

        public static HashSet<string> LoadIgnored()
        {
            return new HashSet<string>(Settings.GetList("ignored"), StringComparer.OrdinalIgnoreCase);
        }

        public static void SaveIgnored(HashSet<string> set)
        {
            Settings.SetList("ignored", set);
        }
    }

    /// <summary>How sure we are that a folder belongs to something installed, and why.</summary>
    public enum OwnerKind { Folder, Running, Service, Startup, Registered, Bundled, PathTool, Name, Publisher, Shortcut, Driver }

    /// <summary>Who a folder belongs to, and the kind of evidence behind it.</summary>
    public sealed class Ownership
    {
        public string Owner;     // program, service or process name
        public OwnerKind Kind;
        public string Detail;    // e.g. the bundled executable ("uv.exe")

        public Ownership(string owner, OwnerKind kind, string detail = null)
        {
            Owner = owner;
            Kind = kind;
            Detail = detail;
        }

        /// <summary>Name-based matches are heuristics; everything else points at concrete files or registrations.</summary>
        public bool Certain
        {
            get { return Kind != OwnerKind.Name && Kind != OwnerKind.Publisher && Kind != OwnerKind.Shortcut && Kind != OwnerKind.Driver; }
        }

        public string Describe()
        {
            return L.T("leftovers.owner." + Kind.ToString().ToLowerInvariant(), Owner, Detail);
        }
    }

    /// <summary>Everything that indicates something is still installed (paths, names, executables) and who it belongs to.</summary>
    sealed class Evidence
    {
        readonly List<KeyValuePair<string, Ownership>> paths = new List<KeyValuePair<string, Ownership>>();
        readonly Dictionary<string, Ownership> names = new Dictionary<string, Ownership>(StringComparer.Ordinal);
        readonly Dictionary<string, Ownership> firstWords = new Dictionary<string, Ownership>(StringComparer.Ordinal);
        readonly Dictionary<string, Ownership> executables = new Dictionary<string, Ownership>(StringComparer.Ordinal);

        public static Evidence Collect(List<InstalledProgram> programs, IEnumerable<string> scanRoots)
        {
            var ev = new Evidence();
            foreach (var p in programs)
            {
                ev.AddName(p.Name, new Ownership(p.ShortName, OwnerKind.Name));
                ev.AddName(p.Publisher, new Ownership(p.Publisher, OwnerKind.Publisher));
                var folder = new Ownership(p.ShortName, OwnerKind.Folder);
                ev.AddPath(p.InstallLocation, folder);
                ev.AddFile(p.DisplayIcon, folder);
                ev.AddFile(p.UninstallString, folder);
            }
            ev.CollectServices();
            ev.CollectProcesses();
            ev.CollectStartMenu();
            ev.CollectRegistryCommands();
            ev.CollectDriverProviders();
            ev.CollectSteamGames();

            // An evidence path equal to (or above) a scanned folder would "explain" everything in it.
            var roots = scanRoots.Select(Env.Normalize).ToList();
            Func<string, bool> coversScanRoot = p => roots.Any(r => r.Equals(p, StringComparison.OrdinalIgnoreCase) || r.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase));
            ev.paths.RemoveAll(kv => coversScanRoot(kv.Key));
            ev.CollectExecutables(programs, coversScanRoot);
            return ev;
        }

        /// <summary>Who accounts for the folder, or null if nothing does (a leftover).</summary>
        public Ownership OwnerOf(string dir)
        {
            string full = Env.Normalize(dir);
            string name = Path.GetFileName(full);
            string n = LeftoverFinder.Normalize(name);

            // Several programs can point at the same folder (a game installed inside Steam's folder points at Steam's too).
            // Best match: the folder itself, then a folder that contains it, then something inside it; ties go to the
            // owner whose name looks like the folder's (Steam ↔ "Steam"), then to the closest path.
            var best = paths
                .Select(kv => new
                {
                    kv.Key, kv.Value,
                    Rank = kv.Key.Equals(full, StringComparison.OrdinalIgnoreCase) ? 0
                         : full.StartsWith(kv.Key + "\\", StringComparison.OrdinalIgnoreCase) ? 1
                         : kv.Key.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase) ? 2 : -1,
                })
                .Where(m => m.Rank >= 0)
                .OrderBy(m => m.Rank)
                .ThenBy(m => SimilarName(n, m.Value.Owner) ? 0 : 1)
                .ThenBy(m => Math.Abs(m.Key.Length - full.Length))
                .FirstOrDefault();
            if (best != null) return best.Value;

            if (n.Length == 0) return new Ownership(name, OwnerKind.Name);
            Ownership owner;
            if (executables.TryGetValue(n, out owner)) return owner;
            if (names.TryGetValue(n, out owner)) return owner;
            if (n.Length < 4) return null;
            foreach (var kv in names)
            {
                string t = kv.Key;
                if (t.Length >= 4 && (t.Contains(n) || n.Contains(t))) return kv.Value;
                if (t.Length == 3 && n.StartsWith(t)) return kv.Value;   // "amd" → AMD_Common, AMDIdentifyWindow
            }
            // A program's first word only counts at the start of the folder name: "DBeaverData" ↔ "DBeaver 26.0.1",
            // but not "Black Tree Gaming Ltd" ↔ "Gaming Services".
            foreach (var kv in firstWords)
                if (n.StartsWith(kv.Key)) return kv.Value;
            return null;
        }

        // ---- Executables shipped by installed programs or on the PATH ----
        // A folder named like one of them (AppData\Local\uv ↔ Comfy Desktop\...\uv.exe) belongs to that tool.

        // Names too generic to say who owns a folder.
        static readonly HashSet<string> GenericExecutables = new HashSet<string>(StringComparer.Ordinal)
        {
            "setup", "install", "installer", "uninstall", "uninst", "unins000", "unins001", "update", "updater",
            "launcher", "helper", "app", "main", "start", "run", "bin", "tools", "service", "server", "client",
        };

        const int ExecutableScanDepth = 6;

        void CollectExecutables(List<InstalledProgram> programs, Func<string, bool> coversScanRoot)
        {
            string windows = Env.Normalize(Env.Windows);
            Func<string, bool> usable = dir =>
            {
                if (string.IsNullOrEmpty(dir) || !Path.IsPathRooted(dir) || !Directory.Exists(dir)) return false;
                string full = Env.Normalize(dir);
                return !full.StartsWith(windows, StringComparison.OrdinalIgnoreCase) && !coversScanRoot(full);
            };

            // Program folders, searched a few levels deep (tools are often bundled in resources\..., bin\...).
            // Deepest folders first, so a game inside a Steam library is credited to the game, not to Steam.
            var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in programs)
                foreach (var folder in new[] { p.InstallLocation, SafeDirectoryOf(Env.ExecutableFromCommand(p.DisplayIcon)), SafeDirectoryOf(Env.ExecutableFromCommand(p.UninstallString)) })
                {
                    if (!usable(folder)) continue;
                    string key = Env.Normalize(folder);
                    // When several programs claim a folder, keep the one named like it (Steam's folder → Steam).
                    if (!roots.ContainsKey(key) || SimilarName(LeftoverFinder.Normalize(Path.GetFileName(key)), p.ShortName)) roots[key] = p.ShortName;
                }
            // Steam games aren't always registered as programs: each installed game folder owns its own executables.
            foreach (var game in UsageScanner.InstalledSteamGames())
                if (usable(game) && !roots.ContainsKey(game)) roots[game] = Path.GetFileName(game);
            foreach (var root in roots.OrderByDescending(kv => kv.Key.Length))
                AddExecutables(new DirectoryInfo(root.Key), ExecutableScanDepth, root.Value, OwnerKind.Bundled);

            // Command-line tools on the PATH (top level only).
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                string expanded = Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"'));
                if (usable(expanded)) AddExecutables(new DirectoryInfo(expanded), 0, Env.Normalize(expanded), OwnerKind.PathTool);
            }
        }

        void AddExecutables(DirectoryInfo dir, int depth, string owner, OwnerKind kind)
        {
            try
            {
                foreach (var f in dir.EnumerateFiles("*.exe"))
                {
                    string n = LeftoverFinder.Normalize(Path.GetFileNameWithoutExtension(f.Name));
                    if (n.Length >= 2 && !GenericExecutables.Contains(n) && !executables.ContainsKey(n))
                        executables[n] = new Ownership(owner, kind, f.Name);
                }
                if (depth <= 0) return;
                foreach (var sub in dir.EnumerateDirectories())
                    if (!Env.IsReparsePoint(sub)) AddExecutables(sub, depth - 1, owner, kind);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }

        static string SafeDirectoryOf(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            try { return Path.GetDirectoryName(file); }
            catch (ArgumentException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        static bool SimilarName(string normalizedFolder, string owner)
        {
            string o = LeftoverFinder.Normalize(owner);
            return normalizedFolder.Length >= 3 && o.Length >= 3 && (o.Contains(normalizedFolder) || normalizedFolder.Contains(o));
        }

        static readonly Regex Words = new Regex(@"[A-Za-z0-9]+");

        // First words too common to identify anything ("Gaming Services", "Visual Studio", "Intel(R) ...").
        static readonly HashSet<string> GenericWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "game", "games", "gaming", "play", "studio", "studios", "software", "systems", "digital", "entertainment",
            "interactive", "technologies", "technology", "service", "services", "update", "driver", "drivers", "tools",
            "runtime", "microsoft", "windows", "visual", "intel", "realtek", "online", "cloud", "client", "desktop",
            "media", "audio", "video", "data", "user", "common", "shared", "local", "global", "system", "network",
        };

        /// <summary>Adds the full name and its first word ("DBeaver 26.0.1 (current user)" → "dbeaver"). The first owner of a name wins.</summary>
        void AddName(string name, Ownership owner)
        {
            string n = LeftoverFinder.Normalize(name);
            if (n.Length >= 3 && !names.ContainsKey(n)) names[n] = owner;
            var first = Words.Match(name ?? "");
            if (first.Success && first.Value.Length >= 4 && !char.IsDigit(first.Value[0]))
            {
                string word = first.Value.ToLowerInvariant();
                if (!GenericWords.Contains(word) && !firstWords.ContainsKey(word)) firstWords[word] = owner;
            }
        }

        void AddPath(string dir, Ownership owner)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                dir = Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"'));
                if (Path.IsPathRooted(dir)) paths.Add(new KeyValuePair<string, Ownership>(Env.Normalize(dir), owner));
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
        }

        void AddFile(string command, Ownership owner)
        {
            string exe = Env.ExecutableFromCommand(command);
            if (string.IsNullOrEmpty(exe) || !Path.IsPathRooted(exe)) return;
            try { AddPath(Path.GetDirectoryName(exe), owner); }
            catch (ArgumentException) { }
            catch (PathTooLongException) { }
        }

        void CollectServices()
        {
            using (var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
            {
                if (services == null) return;
                foreach (var name in services.GetSubKeyNames())
                {
                    using (var k = services.OpenSubKey(name))
                    {
                        if (k == null) continue;
                        string image = k.GetValue("ImagePath") as string;
                        // System drivers tell us nothing; third-party services do.
                        if (image == null || image.IndexOf(@"\Windows\", StringComparison.OrdinalIgnoreCase) >= 0
                            || image.StartsWith(@"\SystemRoot", StringComparison.OrdinalIgnoreCase)
                            || image.StartsWith("system32", StringComparison.OrdinalIgnoreCase)) continue;
                        string display = k.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(display) || display.StartsWith("@")) display = name;   // "@oem.inf,-100" resource strings
                        var owner = new Ownership(display, OwnerKind.Service);
                        AddName(name, owner);
                        AddName(display, owner);
                        AddFile(image, owner);
                    }
                }
            }
        }

        void CollectProcesses()
        {
            int self;
            using (var current = Process.GetCurrentProcess()) self = current.Id;
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id == self) continue;   // Sweepfella itself doesn't vouch for a "Sweepfella" folder
                    var owner = new Ownership(p.ProcessName, OwnerKind.Running);
                    AddName(p.ProcessName, owner);
                    try { AddFile("\"" + p.MainModule.FileName + "\"", owner); }
                    catch (System.ComponentModel.Win32Exception) { }   // protected processes or another architecture
                    catch (InvalidOperationException) { }
                }
            }
        }

        void CollectStartMenu()
        {
            var menus = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            };
            foreach (var menu in menus.Where(m => !string.IsNullOrEmpty(m) && Directory.Exists(m)))
            {
                try
                {
                    foreach (var lnk in Directory.EnumerateFiles(menu, "*.lnk", SearchOption.AllDirectories))
                    {
                        string shortcut = Path.GetFileNameWithoutExtension(lnk);
                        string folder = Path.GetFileName(Path.GetDirectoryName(lnk));
                        AddName(shortcut, new Ownership(shortcut, OwnerKind.Shortcut));
                        AddName(folder, new Ownership(folder, OwnerKind.Shortcut));
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        void CollectRegistryCommands()
        {
            var keys = new[]
            {
                Tuple.Create(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", true),
                Tuple.Create(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", true),
                Tuple.Create(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false),
                Tuple.Create(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false),
                Tuple.Create(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", false),
            };
            foreach (var t in keys)
            {
                using (var k = t.Item1.OpenSubKey(t.Item2))
                {
                    if (k == null) continue;
                    if (t.Item3)
                    {
                        foreach (var sub in k.GetSubKeyNames())
                            using (var s = k.OpenSubKey(sub))
                                if (s != null) AddFile(s.GetValue("") as string, new Ownership(sub, OwnerKind.Registered));
                    }
                    else
                    {
                        foreach (var v in k.GetValueNames())
                        {
                            var owner = new Ownership(v, OwnerKind.Startup);
                            AddName(v, owner);
                            AddFile(k.GetValue(v) as string, owner);
                        }
                    }
                }
            }
        }

        void CollectDriverProviders()
        {
            using (var classes = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class"))
            {
                if (classes == null) return;
                foreach (var cls in classes.GetSubKeyNames())
                {
                    using (var c = classes.OpenSubKey(cls))
                    {
                        if (c == null) continue;
                        string[] instances;
                        try { instances = c.GetSubKeyNames(); }
                        catch (System.Security.SecurityException) { continue; }
                        foreach (var inst in instances)
                        {
                            RegistryKey k;
                            try { k = c.OpenSubKey(inst); }
                            catch (System.Security.SecurityException) { continue; }
                            if (k == null) continue;
                            using (k)
                            {
                                string provider = k.GetValue("ProviderName") as string;
                                if (provider == null || provider.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                                var owner = new Ownership(provider, OwnerKind.Driver);
                                AddName(provider, owner);
                                if (provider.StartsWith("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (!names.ContainsKey("amd")) names["amd"] = owner;
                                    if (!names.ContainsKey("ati")) names["ati"] = owner;
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Installed Steam games only: a library folder without a manifest is a leftover, not evidence.</summary>
        void CollectSteamGames()
        {
            foreach (var game in UsageScanner.InstalledSteamGames())
            {
                string name = Path.GetFileName(game);
                AddPath(game, new Ownership(name, OwnerKind.Folder));
                AddName(name, new Ownership(name, OwnerKind.Name));
            }
        }
    }
}
