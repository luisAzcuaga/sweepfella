using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace Sweepfella
{
    public sealed class InstalledProgram
    {
        public string Name, Publisher, Version, InstallLocation, UninstallString, DisplayIcon, RegistryKey, RegistryPath;
        public long SizeBytes;
        public DateTime? InstallDate;
        public bool UninstallerMissing;

        static readonly System.Text.RegularExpressions.Regex TrailingVersion =
            new System.Text.RegularExpressions.Regex(@"[\s\-–]*v?\d+(\.\d+){1,3}\s*$");

        /// <summary>Name without the repeated trailing version ("… Redistributable - 10.0.40219" → "… Redistributable").</summary>
        public string ShortName
        {
            get
            {
                string s = TrailingVersion.Replace(Name, "");
                return s.Length >= 3 ? s : Name;
            }
        }

        public string Kind = ProgramKinds.Other;
        public string About = "";
        public RuntimeInfo Runtime;            // only for Visual C++ and .NET runtimes
        public InstalledProgram SupersededBy;  // a newer version of the same family that covers it
        public List<string> UsedBy;            // null = dependencies not scanned yet
        public DateTime? SupportEnded;         // end of support, if already past

        string SupportEndedText
        {
            get { return SupportEnded.Value.ToString(L.T("format.long_date"), L.Culture); }
        }

        public string StatusText
        {
            get
            {
                if (UninstallerMissing) return L.T("status.uninstaller_missing");
                if (SupersededBy != null) return L.T("status.superseded", SupersededBy.Version);
                if (Runtime == null) return "";
                var parts = new List<string>();
                if (SupportEnded != null) parts.Add(L.T("status.unsupported"));
                if (UsedBy != null) parts.Add(UsedBy.Count == 0 ? L.T("status.no_use") : L.T("status.in_use", UsedBy.Count));
                return string.Join(" · ", parts);
            }
        }

        public string Advice
        {
            get
            {
                if (UninstallerMissing) return L.T("advice.uninstaller_missing");
                if (SupersededBy != null) return L.T("advice.superseded", SupersededBy.Name);
                if (Runtime == null) return null;

                var parts = new List<string>();
                if (SupportEnded != null) parts.Add(L.T("advice.unsupported", SupportEndedText));
                if (UsedBy == null) parts.Add(L.T("advice.not_scanned"));
                else if (UsedBy.Count > 0) parts.Add(L.T("advice.used", UsedBy.Count));
                else parts.Add(L.T("advice.unused"));
                return string.Join(" ", parts);
            }
        }
    }

    public sealed class RuntimeInfo
    {
        public string Kind;      // "vc" or "net"
        public string Family;    // vc: 2005..2013 or "14"; net: Microsoft.NETCore.App, etc.
        public string Arch;      // x86, x64, arm64 (vc)
        public int Major;        // net
        public Version Version;

        /// <summary>Usage keys this runtime satisfies.</summary>
        public IEnumerable<string> UsageKeys()
        {
            if (Kind == "vc")
            {
                yield return "vc|" + Family + "|" + Arch;
                yield break;
            }
            yield return "net|" + Family + "|" + Major;
            if (Family == "Microsoft.NETCore.App")
            {
                // Desktop and ASP.NET apps load the base runtime too.
                yield return "net|Microsoft.WindowsDesktop.App|" + Major;
                yield return "net|Microsoft.AspNetCore.App|" + Major;
            }
        }
    }

    /// <summary>Program groups. These are identifiers; <see cref="Title"/> returns the translated heading.</summary>
    public static class ProgramKinds
    {
        public const string VisualCpp = "vc";
        public const string DotNet = "net";
        public const string GameRuntimes = "game_runtimes";
        public const string Drivers = "drivers";
        public const string Games = "games";
        public const string Dev = "dev";
        public const string Other = "other";

        public static readonly string[] Order = { VisualCpp, DotNet, GameRuntimes, Drivers, Games, Dev, Other };

        public static string Title(string kind) { return L.T("kind." + kind); }
    }

    public static class ProgramRegistry
    {
        const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        /// <param name="includeHidden">Include hidden components and updates (useful evidence for the leftover finder).</param>
        public static List<InstalledProgram> Read(bool includeHidden = false)
        {
            var list = new List<InstalledProgram>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ReadHive(RegistryHive.LocalMachine, RegistryView.Registry64, includeHidden, list, seen);
            ReadHive(RegistryHive.LocalMachine, RegistryView.Registry32, includeHidden, list, seen);
            ReadHive(RegistryHive.CurrentUser, RegistryView.Registry64, includeHidden, list, seen);
            Knowledge.Annotate(list);
            return list;
        }

        static void ReadHive(RegistryHive hive, RegistryView view, bool includeHidden, List<InstalledProgram> list, HashSet<string> seen)
        {
            using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
            using (var root = baseKey.OpenSubKey(UninstallPath))
            {
                if (root == null) return;
                string prefix = (hive == RegistryHive.LocalMachine ? "HKLM\\" : "HKCU\\")
                              + (hive == RegistryHive.LocalMachine && view == RegistryView.Registry32 ? UninstallPath.Replace("SOFTWARE\\", "SOFTWARE\\WOW6432Node\\") : UninstallPath);
                foreach (var keyName in root.GetSubKeyNames())
                {
                    using (var k = root.OpenSubKey(keyName))
                    {
                        if (k == null) continue;
                        string name = Str(k, "DisplayName");
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        string release = Str(k, "ReleaseType");
                        bool hidden = Int(k, "SystemComponent") == 1 || Str(k, "ParentKeyName") != null
                                   || (release != null && (release.Contains("Update") || release == "Hotfix"));
                        if (hidden && !includeHidden) continue;

                        string version = Str(k, "DisplayVersion") ?? "";
                        if (!seen.Add(name.Trim() + "|" + version)) continue;

                        var p = new InstalledProgram
                        {
                            Name = name.Trim(),
                            Publisher = Str(k, "Publisher") ?? "",
                            Version = version,
                            InstallLocation = CleanPath(Str(k, "InstallLocation")),
                            UninstallString = Str(k, "UninstallString"),
                            DisplayIcon = Str(k, "DisplayIcon"),
                            RegistryKey = keyName,
                            RegistryPath = prefix + "\\" + keyName,
                            SizeBytes = Math.Max(0, Int(k, "EstimatedSize")) * 1024L,
                            InstallDate = Date(Str(k, "InstallDate")),
                        };
                        string exe = Env.ExecutableFromCommand(p.UninstallString);
                        p.UninstallerMissing = !string.IsNullOrEmpty(exe) && Path.IsPathRooted(exe) && !File.Exists(exe);
                        list.Add(p);
                    }
                }
            }
        }

        static string Str(RegistryKey k, string name)
        {
            var v = k.GetValue(name) as string;
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        static int Int(RegistryKey k, string name)
        {
            object v = k.GetValue(name);
            if (v is int) return (int)v;
            int parsed;
            return v != null && int.TryParse(v.ToString(), out parsed) ? parsed : 0;
        }

        static DateTime? Date(string s)
        {
            DateTime d;
            if (s != null && DateTime.TryParseExact(s.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
            return null;
        }

        static string CleanPath(string s)
        {
            if (s == null) return null;
            s = Environment.ExpandEnvironmentVariables(s.Trim().Trim('"')).TrimEnd('\\');
            return Path.IsPathRooted(s) && s.Length > 3 ? s : null;
        }

        /// <summary>Starts the program's uninstaller. Returns null if it has none.</summary>
        public static Process StartUninstall(InstalledProgram p)
        {
            string file, args;
            Env.SplitCommand(p.UninstallString, out file, out args);
            if (string.IsNullOrEmpty(file)) return null;
            if (Path.GetFileNameWithoutExtension(file).Equals("msiexec", StringComparison.OrdinalIgnoreCase))
                args = Regex.Replace(args, @"(^|\s)/I(?=\{)", "$1/X", RegexOptions.IgnoreCase);   // /I opens "repair"; /X uninstalls
            return Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        }
    }

    /// <summary>Explains what each entry is and how runtime versions relate to each other.</summary>
    public static class Knowledge
    {
        // "Visual C++ 2010 x64 Redistributable", "Visual C++ 2015-2022 Redistributable (x64)" or the newer "Visual C++ v14 Redistributable (x64)".
        static readonly Regex VcName = new Regex(@"Visual C\+\+ (?:v(?<v>14)\b|(?<y>\d{4})(?:-\d{4})?).*(?:Redistributable|Runtime)", RegexOptions.IgnoreCase);
        static readonly Regex NetName = new Regex(@"^Microsoft (Windows Desktop Runtime|\.NET (?:Core )?Runtime|ASP\.NET Core)\b.*?(\d+)\.(\d+)\.(\d+)", RegexOptions.IgnoreCase);
        static readonly Regex NetSdk = new Regex(@"^Microsoft \.NET (?:Core )?SDK", RegexOptions.IgnoreCase);
        static readonly Regex Arch = new Regex(@"\b(x64|x86|arm64|amd64)\b", RegexOptions.IgnoreCase);

        // End of support for (modern) .NET. Versions not listed here (10+) are still supported.
        static readonly Dictionary<int, DateTime> NetEndDates = new Dictionary<int, DateTime>
        {
            { 1, new DateTime(2019, 6, 27) }, { 2, new DateTime(2021, 8, 23) }, { 3, new DateTime(2022, 12, 13) },
            { 5, new DateTime(2022, 5, 10) }, { 6, new DateTime(2024, 11, 12) }, { 7, new DateTime(2024, 5, 14) },
            { 8, new DateTime(2026, 11, 10) }, { 9, new DateTime(2026, 11, 10) },
        };

        sealed class Rule
        {
            public Regex Pattern; public string Kind; public string AboutKey;
            public Rule(string pattern, string kind, string aboutKey)
            {
                Pattern = new Regex(pattern, RegexOptions.IgnoreCase);
                Kind = kind;
                AboutKey = aboutKey;
            }
        }

        static readonly Rule[] Rules =
        {
            new Rule(@"WebView2", ProgramKinds.Other, "about.webview2"),
            new Rule(@"^Microsoft Update Health Tools", ProgramKinds.Other, "about.health_tools"),
            new Rule(@"PC Health Check|Comprobación de estado", ProgramKinds.Other, "about.pc_health"),   // English and Spanish Windows names
            new Rule(@"GameInput", ProgramKinds.GameRuntimes, "about.gameinput"),
            new Rule(@"PhysX", ProgramKinds.GameRuntimes, "about.physx"),
            new Rule(@"OpenAL", ProgramKinds.GameRuntimes, "about.openal"),
            new Rule(@"XNA Framework", ProgramKinds.GameRuntimes, "about.xna"),
            new Rule(@"DirectX", ProgramKinds.GameRuntimes, "about.directx"),
            new Rule(@"Vulkan Run ?Time", ProgramKinds.GameRuntimes, "about.vulkan"),
            new Rule(@"^(AMD|Radeon)|Advanced Micro Devices", ProgramKinds.Drivers, "about.amd"),
            new Rule(@"NVIDIA", ProgramKinds.Drivers, "about.nvidia"),
            new Rule(@"Realtek", ProgramKinds.Drivers, "about.realtek"),
            new Rule(@"^Intel", ProgramKinds.Drivers, "about.intel"),
            new Rule(@"Bonjour", ProgramKinds.Other, "about.bonjour"),
            new Rule(@"^(Steam|Epic Games Launcher|EA app|Origin|Ubisoft Connect|Battle\.net|GOG GALAXY|Riot|Xbox)", ProgramKinds.Games, "about.launcher"),
            new Rule(@"Windows (Software Development Kit|SDK)|Windows Kits|App Certification Kit", ProgramKinds.Dev, "about.winsdk"),
            new Rule(@"^Python \d", ProgramKinds.Dev, "about.python"),
            new Rule(@"\b(Java|JDK|JRE|Temurin|OpenJDK)\b", ProgramKinds.Dev, "about.java"),
            new Rule(@"^(Node\.js|Git\b|Docker|Visual Studio|Microsoft Visual Studio|vs_|Windows Subsystem for Linux|PowerShell)", ProgramKinds.Dev, "about.devtool"),
        };

        public static void Annotate(List<InstalledProgram> programs)
        {
            foreach (var p in programs)
            {
                if (TryVisualCpp(p) || TryDotNet(p)) continue;
                if (p.RegistryKey != null && p.RegistryKey.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase))
                {
                    p.Kind = ProgramKinds.Games;
                    p.About = L.T("about.steam_game");
                    continue;
                }
                var rule = Rules.FirstOrDefault(r => r.Pattern.IsMatch(p.Name));
                if (rule != null) { p.Kind = rule.Kind; p.About = L.T(rule.AboutKey); }
                else if (p.Publisher.Length > 0) p.About = L.T("about.by_publisher", p.Publisher);
            }
            MarkSuperseded(programs);
        }

        static bool TryVisualCpp(InstalledProgram p)
        {
            var m = VcName.Match(p.Name);
            if (!m.Success || p.Name.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) < 0) return false;
            int year = m.Groups["v"].Success ? 2015 : int.Parse(m.Groups["y"].Value);
            string family = year >= 2015 ? "14" : year.ToString();
            p.Kind = ProgramKinds.VisualCpp;
            p.Runtime = new RuntimeInfo { Kind = "vc", Family = family, Arch = ArchOf(p.Name), Version = ParseVersion(p.Version) };
            p.About = (family == "14" ? L.T("about.vc14") : L.T("about.vc_year", year))
                    + " " + L.T("about.vc_arch", p.Runtime.Arch, p.Runtime.Arch == "x86" ? 32 : 64);
            return true;
        }

        static bool TryDotNet(InstalledProgram p)
        {
            if (NetSdk.IsMatch(p.Name))
            {
                p.Kind = ProgramKinds.Dev;
                p.About = L.T("about.net_sdk");
                return true;
            }
            var m = NetName.Match(p.Name);
            if (!m.Success) return false;
            string kind = m.Groups[1].Value.ToLowerInvariant();
            int major = int.Parse(m.Groups[2].Value);
            string family = kind.Contains("desktop") ? "Microsoft.WindowsDesktop.App"
                          : kind.Contains("asp") ? "Microsoft.AspNetCore.App"
                          : "Microsoft.NETCore.App";
            p.Kind = ProgramKinds.DotNet;
            p.Runtime = new RuntimeInfo
            {
                Kind = "net", Family = family, Major = major, Arch = ArchOf(p.Name),
                Version = ParseVersion(m.Groups[2].Value + "." + m.Groups[3].Value + "." + m.Groups[4].Value),
            };
            string what = family == "Microsoft.WindowsDesktop.App" ? L.T("about.net.desktop")
                        : family == "Microsoft.AspNetCore.App" ? L.T("about.net.asp")
                        : L.T("about.net.core");
            p.About = L.T("about.net", what, major, major + 1);
            DateTime end;
            if (NetEndDates.TryGetValue(major, out end) && DateTime.Today >= end) p.SupportEnded = end;
            return true;
        }

        /// <summary>Within the same family and architecture, the newest version supersedes the older ones.</summary>
        static void MarkSuperseded(List<InstalledProgram> programs)
        {
            var groups = programs.Where(p => p.Runtime != null)
                                 .GroupBy(p => p.Runtime.Kind + "|" + p.Runtime.Family + "|" + p.Runtime.Arch + "|" + p.Runtime.Major);
            foreach (var g in groups)
            {
                var ordered = g.OrderByDescending(p => p.Runtime.Version).ToList();
                // In .NET a newer patch of the same major version supersedes older ones too (6.0.31 covers 6.0.25).
                for (int i = 1; i < ordered.Count; i++)
                    if (ordered[i].Runtime.Version < ordered[0].Runtime.Version) ordered[i].SupersededBy = ordered[0];
            }
        }

        static string ArchOf(string name)
        {
            var m = Arch.Match(name);
            if (!m.Success) return "x86";   // 32-bit VC++ 2005/2008 entries don't state their architecture
            string a = m.Groups[1].Value.ToLowerInvariant();
            return a == "amd64" ? "x64" : a;
        }

        static Version ParseVersion(string s)
        {
            Version v;
            var m = Regex.Match(s ?? "", @"\d+(\.\d+){1,3}");
            return m.Success && Version.TryParse(m.Value, out v) ? v : new Version(0, 0);
        }

        /// <summary>Visual C++ family of an imported DLL (msvcr100.dll → 2010), or null if it isn't a runtime DLL.</summary>
        public static string VcFamilyOfDll(string dll)
        {
            var m = VcDll.Match(dll);
            if (!m.Success) return null;
            switch (m.Groups[2].Value)
            {
                case "80": return "2005";
                case "90": return "2008";
                case "100": return "2010";
                case "110": return "2012";
                case "120": return "2013";
                case "140": return "14";
                default: return null;
            }
        }

        static readonly Regex VcDll = new Regex(@"^(msvcr|msvcp|mfcm?|vcomp|vcruntime|concrt|vccorlib)(\d+)\D", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Walks the folders of installed programs and works out which runtimes each one uses:
    /// Visual C++ from the import table of every .exe/.dll, and .NET from *.runtimeconfig.json files.
    /// </summary>
    public sealed class UsageScanner
    {
        static readonly Regex FrameworkRef = new Regex(@"""name""\s*:\s*""(Microsoft\.(?:NETCore|WindowsDesktop|AspNetCore)\.App)""\s*,\s*""version""\s*:\s*""(\d+)\.", RegexOptions.IgnoreCase);

        readonly List<KeyValuePair<string, string>> owners;   // install folder → program name
        readonly Dictionary<string, string> namesByKey;       // normalized name → program name
        public readonly Dictionary<string, HashSet<string>> Usage = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        public int FilesScanned;
        readonly List<string> roots;

        public UsageScanner(List<InstalledProgram> programs)
        {
            owners = programs.Where(p => p.InstallLocation != null && Directory.Exists(p.InstallLocation) && PathGuard.IsSafeToEmpty(p.InstallLocation))
                             .Select(p => new KeyValuePair<string, string>(Env.Normalize(p.InstallLocation) + "\\", p.Name))
                             .OrderByDescending(kv => kv.Key.Length)
                             .ToList();
            roots = BuildRoots(programs);
            namesByKey = new Dictionary<string, string>();
            foreach (var p in programs)
            {
                string key = LeftoverFinder.Normalize(p.Name);
                if (key.Length >= 3 && !namesByKey.ContainsKey(key)) namesByKey.Add(key, p.Name);
            }
        }

        public IList<string> Roots { get { return roots; } }

        static List<string> BuildRoots(List<InstalledProgram> programs)
        {
            var candidates = new List<string> { Env.ProgramFiles, Env.ProgramFilesX86, Env.Combine(Env.Local, "Programs") };
            candidates.AddRange(programs.Select(p => p.InstallLocation));
            candidates.AddRange(SteamLibraries());
            var normalized = candidates.Where(c => !string.IsNullOrEmpty(c) && Directory.Exists(c))
                                       .Select(Env.Normalize)
                                       .Where(c => !c.StartsWith(Env.Normalize(Env.Windows), StringComparison.OrdinalIgnoreCase))
                                       .Distinct(StringComparer.OrdinalIgnoreCase)
                                       .OrderBy(c => c.Length)
                                       .ToList();
            var result = new List<string>();
            foreach (var c in normalized)
                if (!result.Any(r => c.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))) result.Add(c);
            return result;
        }

        /// <summary>The steamapps\\common folder of every Steam library.</summary>
        public static IEnumerable<string> SteamLibraries()
        {
            string steam = null;
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                if (k != null) steam = k.GetValue("SteamPath") as string;
            if (steam == null) yield break;
            string vdf = Path.Combine(steam.Replace('/', '\\'), "steamapps", "libraryfolders.vdf");
            string text;
            try { text = File.ReadAllText(vdf); }
            catch (IOException) { yield break; }
            catch (UnauthorizedAccessException) { yield break; }
            foreach (Match m in Regex.Matches(text, @"""path""\s+""([^""]+)"""))
                yield return Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps", "common");
        }

        /// <summary>
        /// Game folders Steam considers installed: steamapps\common\&lt;installdir&gt; named by an appmanifest_*.acf.
        /// A folder in common without a manifest is what's left of an uninstalled game (mods, tools...).
        /// </summary>
        public static HashSet<string> InstalledSteamGames()
        {
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var common in SteamLibraries())
            {
                string[] manifests;
                try { manifests = Directory.GetFiles(Path.GetDirectoryName(common), "appmanifest_*.acf"); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (var manifest in manifests)
                {
                    try
                    {
                        var m = Regex.Match(File.ReadAllText(manifest), @"""installdir""\s+""([^""]+)""");
                        if (m.Success) installed.Add(Env.Normalize(Path.Combine(common, m.Groups[1].Value.Replace(@"\\", @"\"))));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            return installed;
        }

        public void Run(IProgress<string> progress, CancellationToken ct)
        {
            var lastReport = DateTime.MinValue;
            foreach (var root in roots)
                Walk(new DirectoryInfo(root), root, progress, ref lastReport, ct);
        }

        void Walk(DirectoryInfo dir, string root, IProgress<string> progress, ref DateTime lastReport, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 150)
            {
                progress.Report(dir.FullName);
                lastReport = DateTime.UtcNow;
            }
            FileSystemInfo[] entries;
            try { entries = dir.GetFileSystemInfos(); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }

            // DLLs bundled in the program's own folder: if it ships them, it doesn't depend on the installed runtime.
            var localNames = new HashSet<string>(entries.OfType<FileInfo>().Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var e in entries)
            {
                if (Env.IsReparsePoint(e)) continue;
                var sub = e as DirectoryInfo;
                if (sub != null) { Walk(sub, root, progress, ref lastReport, ct); continue; }
                var f = (FileInfo)e;
                string ext = f.Extension.ToLowerInvariant();
                if (ext == ".exe" || ext == ".dll") ScanPe(f, root, localNames);
                else if (f.Name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)) ScanRuntimeConfig(f, root);
            }
        }

        void ScanPe(FileInfo f, string root, HashSet<string> localNames)
        {
            long length;
            try { length = f.Length; }
            catch (IOException) { return; }
            if (length < 1024) return;
            // The runtime's own DLLs are not "users" of it.
            if (Knowledge.VcFamilyOfDll(f.Name) != null) return;

            FilesScanned++;
            ushort machine;
            var imports = Pe.ReadImports(f.FullName, out machine);
            if (imports == null) return;
            string arch = machine == 0x8664 ? "x64" : machine == 0x14c ? "x86" : machine == 0xAA64 ? "arm64" : null;
            if (arch == null) return;
            foreach (var dll in imports)
            {
                string family = Knowledge.VcFamilyOfDll(dll);
                if (family == null || localNames.Contains(dll)) continue;
                Add("vc|" + family + "|" + arch, OwnerOf(f.FullName, root));
            }
        }

        void ScanRuntimeConfig(FileInfo f, string root)
        {
            string text;
            try
            {
                if (f.Length > 256 * 1024) return;
                text = File.ReadAllText(f.FullName);
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            foreach (Match m in FrameworkRef.Matches(text))
                Add("net|" + m.Groups[1].Value + "|" + m.Groups[2].Value, OwnerOf(f.FullName, root));
        }

        void Add(string key, string owner)
        {
            HashSet<string> set;
            if (!Usage.TryGetValue(key, out set)) Usage[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(owner);
        }

        /// <summary>Installed program a file belongs to, or the top-level folder that contains it.</summary>
        string OwnerOf(string file, string root)
        {
            foreach (var kv in owners)
                if (file.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            var parts = file.Substring(root.Length).TrimStart('\\').Split('\\');
            if (parts.Length < 2) return file;
            // Under "Common Files" the program is the subfolder: Common Files\Adobe, Common Files\Steam...
            int depth = parts[0].Equals("Common Files", StringComparison.OrdinalIgnoreCase) && parts.Length > 2 ? 2 : 1;
            string folder = Path.Combine(root, string.Join("\\", parts.Take(depth)));
            // If the folder is named like an installed program (Steam, Discord...), use that program's name.
            string name;
            return namesByKey.TryGetValue(LeftoverFinder.Normalize(parts[depth - 1]), out name) ? name : folder;
        }

        /// <summary>Assigns each installed runtime the list of programs that use it.</summary>
        public void Apply(List<InstalledProgram> programs)
        {
            foreach (var p in programs.Where(p => p.Runtime != null))
            {
                var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var key in p.Runtime.UsageKeys())
                {
                    HashSet<string> set;
                    if (Usage.TryGetValue(key, out set)) users.UnionWith(set);
                }
                users.Remove(p.Name);
                p.UsedBy = users.OrderBy(u => u, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
        }
    }

    /// <summary>Minimal PE reader: just the import table.</summary>
    static class Pe
    {
        public static List<string> ReadImports(string path, out ushort machine)
        {
            machine = 0;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess))
                using (var br = new BinaryReader(fs))
                {
                    if (fs.Length < 0x40 || br.ReadUInt16() != 0x5A4D) return null;   // "MZ"
                    fs.Position = 0x3C;
                    int pe = br.ReadInt32();
                    if (pe <= 0 || pe > fs.Length - 24) return null;
                    fs.Position = pe;
                    if (br.ReadUInt32() != 0x00004550) return null;                    // "PE\0\0"
                    machine = br.ReadUInt16();
                    int sectionCount = br.ReadUInt16();
                    fs.Position += 12;
                    int optionalSize = br.ReadUInt16();
                    fs.Position += 2;
                    long optional = fs.Position;
                    ushort magic = br.ReadUInt16();
                    int dataDirs = magic == 0x20b ? 112 : magic == 0x10b ? 96 : -1;
                    if (dataDirs < 0 || sectionCount > 96) return null;
                    fs.Position = optional + dataDirs - 4;
                    if (br.ReadUInt32() < 2) return null;                              // no import directory
                    fs.Position = optional + dataDirs + 8;
                    uint importRva = br.ReadUInt32();
                    if (importRva == 0) return new List<string>();

                    var sections = new uint[sectionCount, 3];
                    for (int i = 0; i < sectionCount; i++)
                    {
                        fs.Position = optional + optionalSize + i * 40 + 8;
                        uint virtualSize = br.ReadUInt32();
                        uint virtualAddress = br.ReadUInt32();
                        uint rawSize = br.ReadUInt32();
                        uint rawPointer = br.ReadUInt32();
                        sections[i, 0] = virtualAddress;
                        sections[i, 1] = Math.Max(virtualSize, rawSize);
                        sections[i, 2] = rawPointer;
                    }
                    Func<uint, long> toOffset = rva =>
                    {
                        for (int i = 0; i < sectionCount; i++)
                            if (rva >= sections[i, 0] && rva < sections[i, 0] + sections[i, 1]) return rva - sections[i, 0] + sections[i, 2];
                        return -1;
                    };

                    long descriptor = toOffset(importRva);
                    if (descriptor < 0) return null;
                    var result = new List<string>();
                    for (int i = 0; i < 1024; i++)
                    {
                        long at = descriptor + i * 20L;
                        if (at + 20 > fs.Length) break;
                        fs.Position = at;
                        uint originalThunk = br.ReadUInt32();
                        fs.Position += 8;
                        uint nameRva = br.ReadUInt32();
                        uint thunk = br.ReadUInt32();
                        if (originalThunk == 0 && nameRva == 0 && thunk == 0) break;
                        long nameAt = toOffset(nameRva);
                        if (nameAt < 0 || nameAt >= fs.Length) continue;
                        fs.Position = nameAt;
                        var sb = new StringBuilder();
                        for (int c = 0; c < 128; c++)
                        {
                            int b = fs.ReadByte();
                            if (b <= 0) break;
                            sb.Append((char)b);
                        }
                        result.Add(sb.ToString().ToLowerInvariant());
                    }
                    return result;
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
}
