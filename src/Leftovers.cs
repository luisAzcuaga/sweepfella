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
        public long Bytes;
        public int Files;
        public DateTime LastChange;

        public bool RecentlyUsed { get { return (DateTime.Now - LastChange).TotalDays < 30; } }
    }

    sealed class ScanLocation
    {
        public string Path, Label;
        public bool RequiresAdmin;
        public ScanLocation(string path, string label, bool admin) { Path = path; Label = label; RequiresAdmin = admin; }
    }

    /// <summary>
    /// Finds top-level folders in AppData, ProgramData and Program Files that don't belong to any
    /// installed program, service, process, shortcut or driver.
    /// When in doubt a folder counts as "in use": missing a leftover beats suggesting to delete something in use.
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
            "dotnet", "PackageManagement", "WindowsPowerShell", "Sweepfella", "dbg", "Downloaded Installations", "NuGet", "IconCache",
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
                    if (!PathGuard.IsSafeToEmpty(dir) || evidence.Explains(dir)) continue;

                    if (progress != null) progress.Report(dir);
                    var item = new Leftover { Path = dir, Name = name, Location = loc.Label, RequiresAdmin = loc.RequiresAdmin, LastChange = info.LastWriteTime };
                    Measure(info, item, ct);
                    result.Add(item);
                }
            }
            return result;
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

        /// <summary>Sends the folder to the Recycle Bin (it can be restored).</summary>
        public static void MoveToRecycleBin(string dir)
        {
            if (!PathGuard.IsSafeToEmpty(dir)) throw new InvalidOperationException(L.T("leftovers.err.protected", dir));
            Shell.RunSta(() => Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                dir, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));
        }

        // ---- Folders the user chose to ignore (persisted) ----

        static string IgnoreFile { get { return Path.Combine(Env.Roaming, "Sweepfella", "ignored.txt"); } }

        public static HashSet<string> LoadIgnored()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(IgnoreFile))
                    foreach (var line in File.ReadAllLines(IgnoreFile, Encoding.UTF8))
                        if (line.Trim().Length > 0) set.Add(line.Trim());
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return set;
        }

        public static void SaveIgnored(HashSet<string> set)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(IgnoreFile));
            File.WriteAllLines(IgnoreFile, set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase), Encoding.UTF8);
        }
    }

    /// <summary>Everything that indicates something is still installed: paths and names.</summary>
    sealed class Evidence
    {
        readonly List<string> paths = new List<string>();
        readonly HashSet<string> names = new HashSet<string>();

        public static Evidence Collect(List<InstalledProgram> programs, IEnumerable<string> scanRoots)
        {
            var ev = new Evidence();
            foreach (var p in programs)
            {
                ev.AddName(p.Name);
                ev.AddName(p.Publisher);
                ev.AddPath(p.InstallLocation);
                ev.AddFile(p.DisplayIcon);
                ev.AddFile(p.UninstallString);
            }
            ev.CollectServices();
            ev.CollectProcesses();
            ev.CollectStartMenu();
            ev.CollectRegistryCommands();
            ev.CollectDriverProviders();
            ev.CollectSteamGames();

            // An evidence path equal to (or above) a scanned folder would "explain" everything in it.
            var roots = scanRoots.Select(Env.Normalize).ToList();
            ev.paths.RemoveAll(p => roots.Any(r => r.Equals(p, StringComparison.OrdinalIgnoreCase) || r.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)));
            return ev;
        }

        /// <summary>True if something installed accounts for the folder.</summary>
        public bool Explains(string dir)
        {
            string full = Env.Normalize(dir);
            foreach (var p in paths)
                if (p.Equals(full, StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)) return true;

            string n = LeftoverFinder.Normalize(Path.GetFileName(full));
            if (n.Length == 0) return true;
            if (n.Length < 4) return names.Contains(n);
            foreach (var t in names)
            {
                if (t == n) return true;
                if (t.Length >= 4 && (t.Contains(n) || n.Contains(t))) return true;
                if (t.Length == 3 && n.StartsWith(t)) return true;   // "amd" → AMD_Common, AMDIdentifyWindow
            }
            return false;
        }

        static readonly Regex Words = new Regex(@"[A-Za-z0-9]+");

        /// <summary>Adds the full name and its first word ("DBeaver 26.0.1 (current user)" → "dbeaver").</summary>
        void AddName(string name)
        {
            string n = LeftoverFinder.Normalize(name);
            if (n.Length >= 3) names.Add(n);
            var first = Words.Match(name ?? "");
            if (first.Success && first.Value.Length >= 4 && !char.IsDigit(first.Value[0])) names.Add(first.Value.ToLowerInvariant());
        }

        void AddPath(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                dir = Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"'));
                if (Path.IsPathRooted(dir)) paths.Add(Env.Normalize(dir));
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
        }

        void AddFile(string command)
        {
            string exe = Env.ExecutableFromCommand(command);
            if (string.IsNullOrEmpty(exe) || !Path.IsPathRooted(exe)) return;
            try { AddPath(Path.GetDirectoryName(exe)); }
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
                        AddName(name);
                        AddName(k.GetValue("DisplayName") as string);
                        AddFile(image);
                    }
                }
            }
        }

        void CollectProcesses()
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    AddName(p.ProcessName);
                    try { AddFile("\"" + p.MainModule.FileName + "\""); }
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
                        AddName(Path.GetFileNameWithoutExtension(lnk));
                        AddName(Path.GetFileName(Path.GetDirectoryName(lnk)));
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
                                if (s != null) AddFile(s.GetValue("") as string);
                    }
                    else
                    {
                        foreach (var v in k.GetValueNames())
                        {
                            AddName(v);
                            AddFile(k.GetValue(v) as string);
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
                                AddName(provider);
                                if (provider.StartsWith("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase)) { names.Add("amd"); names.Add("ati"); }
                            }
                        }
                    }
                }
            }
        }

        void CollectSteamGames()
        {
            foreach (var common in UsageScanner.SteamLibraries())
                foreach (var game in Env.SubDirs(common))
                    AddName(Path.GetFileName(game));
        }
    }
}
