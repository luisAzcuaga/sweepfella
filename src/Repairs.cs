using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Sweepfella
{
    public enum RepairStatus { Ok, Changed, Unusual }

    /// <summary>The result of one check: a registry setting that malware is known to change.</summary>
    public sealed class RepairFinding
    {
        public string Id;            // strings: repair.<id>.name / .about / .legit
        public string Area;          // group: tools, startup, files, security, network
        public RepairStatus Status;
        public string Subject;       // what it's about when one check yields several findings (an .exe name)
        public readonly List<string> Locations = new List<string>();   // registry paths checked (or found changed)
        public string Found = "";    // what's there now
        public string Expected = ""; // what Windows has by default
        public string Note;          // extra context, e.g. "probably Process Explorer"
    }

    /// <summary>
    /// Read-only checks of registry settings malware commonly changes to keep itself running, hide, or stop the user
    /// from fixing things: blocked tools, startup hijacks, broken .exe handling, disabled protection, a forced proxy.
    /// Each check knows Windows' default, so findings say exactly what differs. Nothing here writes to the registry.
    /// </summary>
    public static class RepairScanner
    {
        public static readonly string[] Areas = { "tools", "startup", "files", "security", "network" };

        const string PoliciesSystem = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
        const string PoliciesExplorer = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
        const string WindowsPoliciesSystem = @"Software\Policies\Microsoft\Windows\System";
        const string Ifeo = @"Software\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        const string SilentExit = @"Software\Microsoft\Windows NT\CurrentVersion\SilentProcessExit";
        const string Winlogon = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
        const string WindowsNtWindows = @"Software\Microsoft\Windows NT\CurrentVersion\Windows";
        const string ShowAll = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\Folder\Hidden\SHOWALL";
        const string Defender = @"Software\Policies\Microsoft\Windows Defender";
        const string SafeBoot = @"SYSTEM\CurrentControlSet\Control\SafeBoot";
        const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        // Debuggers that legitimately take over Task Manager ("Replace Task Manager" in these tools).
        static readonly string[] TaskManagerReplacements = { "procexp", "processhacker", "systeminformer" };

        public static List<RepairFinding> Scan()
        {
            var all = new List<RepairFinding>();
            all.Add(PolicyFlag("task_manager", "tools", PoliciesSystem, "DisableTaskMgr"));
            all.Add(PolicyFlag("regedit", "tools", PoliciesSystem, "DisableRegistryTools"));
            all.Add(PolicyFlag("cmd", "tools", WindowsPoliciesSystem, "DisableCMD"));
            all.Add(PolicyFlag("folder_options", "tools", PoliciesExplorer, "NoFolderOptions"));
            all.AddRange(Debuggers());
            all.AddRange(SilentExitMonitors());
            all.Add(WinlogonShell());
            all.Add(Userinit());
            all.Add(AppInit());
            all.Add(ExeOpenCommand());
            all.Add(ExeAssociation());
            all.Add(ShowHidden());
            all.Add(DefenderPolicies());
            all.Add(SafeMode());
            all.Add(Proxy());
            return all;
        }

        // ---- Blocked tools: policies that switch off Task Manager, Registry Editor, the command prompt... ----

        static RepairFinding PolicyFlag(string id, string area, string path, string value)
        {
            var f = new RepairFinding { Id = id, Area = area, Expected = Missing() };
            var found = new List<string>();
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                string where = PathOf(hive, RegistryView.Registry64, path, value);
                object v = Read(hive, RegistryView.Registry64, path, value);
                if (AsInt(v) != 0) { found.Add(Describe(v) + "  (" + HiveName(hive) + ")"); f.Locations.Add(where); }
            }
            if (found.Count > 0) { f.Status = RepairStatus.Changed; f.Found = string.Join(", ", found); }
            else { f.Found = Missing(); f.Locations.Add(PathOf(RegistryHive.CurrentUser, RegistryView.Registry64, path, value)); f.Locations.Add(PathOf(RegistryHive.LocalMachine, RegistryView.Registry64, path, value)); }
            return f;
        }

        // ---- Startup hijacks ----

        /// <summary>Image File Execution Options\&lt;exe&gt;\Debugger: Windows starts the "debugger" instead of that program.</summary>
        static IEnumerable<RepairFinding> Debuggers()
        {
            var found = new List<RepairFinding>();
            foreach (var view in Views())
                foreach (var exe in SubKeys(RegistryHive.LocalMachine, view, Ifeo))
                {
                    string debugger = Read(RegistryHive.LocalMachine, view, Ifeo + "\\" + exe, "Debugger") as string;
                    if (string.IsNullOrWhiteSpace(debugger)) continue;
                    var f = new RepairFinding
                    {
                        Id = "debugger", Area = "startup", Subject = exe, Status = RepairStatus.Changed,
                        Found = debugger, Expected = Missing(),
                    };
                    f.Locations.Add(PathOf(RegistryHive.LocalMachine, view, Ifeo + "\\" + exe, "Debugger"));
                    string tool = Path.GetFileNameWithoutExtension(Env.ExecutableFromCommand(debugger) ?? "").ToLowerInvariant();
                    if (exe.Equals("taskmgr.exe", StringComparison.OrdinalIgnoreCase) && TaskManagerReplacements.Any(t => tool.StartsWith(t)))
                    {
                        f.Status = RepairStatus.Unusual;
                        f.Note = L.T("repair.debugger.task_manager_tool", Path.GetFileName(Env.ExecutableFromCommand(debugger)));
                    }
                    found.Add(f);
                }
            if (found.Count == 0)
                found.Add(new RepairFinding { Id = "debugger", Area = "startup", Found = L.T("repair.none_found"), Expected = L.T("repair.none"), Locations = { PathOf(RegistryHive.LocalMachine, RegistryView.Registry64, Ifeo, null) } });
            return found;
        }

        /// <summary>SilentProcessExit\&lt;exe&gt;\MonitorProcess: starts another program whenever that one exits.</summary>
        static IEnumerable<RepairFinding> SilentExitMonitors()
        {
            var found = new List<RepairFinding>();
            foreach (var view in Views())
                foreach (var exe in SubKeys(RegistryHive.LocalMachine, view, SilentExit))
                {
                    string monitor = Read(RegistryHive.LocalMachine, view, SilentExit + "\\" + exe, "MonitorProcess") as string;
                    if (string.IsNullOrWhiteSpace(monitor)) continue;
                    var f = new RepairFinding { Id = "silent_exit", Area = "startup", Subject = exe, Status = RepairStatus.Unusual, Found = monitor, Expected = Missing() };
                    f.Locations.Add(PathOf(RegistryHive.LocalMachine, view, SilentExit + "\\" + exe, "MonitorProcess"));
                    found.Add(f);
                }
            if (found.Count == 0)
                found.Add(new RepairFinding { Id = "silent_exit", Area = "startup", Found = L.T("repair.none_found"), Expected = L.T("repair.none"), Locations = { PathOf(RegistryHive.LocalMachine, RegistryView.Registry64, SilentExit, null) } });
            return found;
        }

        /// <summary>Winlogon\Shell: what starts as the desktop at sign-in. Windows: explorer.exe, and nothing per user.</summary>
        static RepairFinding WinlogonShell()
        {
            var f = new RepairFinding { Id = "shell", Area = "startup", Expected = "explorer.exe" };
            string machine = Read(RegistryHive.LocalMachine, RegistryView.Registry64, Winlogon, "Shell") as string;
            string user = Read(RegistryHive.CurrentUser, RegistryView.Registry64, Winlogon, "Shell") as string;
            f.Locations.Add(PathOf(RegistryHive.LocalMachine, RegistryView.Registry64, Winlogon, "Shell"));
            f.Locations.Add(PathOf(RegistryHive.CurrentUser, RegistryView.Registry64, Winlogon, "Shell"));
            bool machineOk = machine != null && Simplify(machine).TrimEnd(',') == "explorer.exe";
            f.Found = (machine ?? Missing()) + (user != null ? "  ·  " + HiveName(RegistryHive.CurrentUser) + ": " + user : "");
            if (!machineOk || user != null) f.Status = RepairStatus.Changed;
            return f;
        }

        /// <summary>Winlogon\Userinit: runs at sign-in before the desktop. Windows: C:\Windows\system32\userinit.exe,</summary>
        static RepairFinding Userinit()
        {
            string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "userinit.exe") + ",";
            var f = new RepairFinding { Id = "userinit", Area = "startup", Expected = expected };
            string v = Read(RegistryHive.LocalMachine, RegistryView.Registry64, Winlogon, "Userinit") as string;
            f.Locations.Add(PathOf(RegistryHive.LocalMachine, RegistryView.Registry64, Winlogon, "Userinit"));
            f.Found = v ?? Missing();
            string s = v == null ? "" : Simplify(v).TrimEnd(',');
            if (s != Simplify(expected).TrimEnd(',') && s != "userinit.exe" && s != "userinit") f.Status = RepairStatus.Changed;
            return f;
        }

        /// <summary>AppInit_DLLs: DLLs loaded into every program that uses user32.dll, when LoadAppInit_DLLs is on.</summary>
        static RepairFinding AppInit()
        {
            var f = new RepairFinding { Id = "appinit", Area = "startup", Expected = Missing() };
            var found = new List<string>();
            foreach (var view in Views())
            {
                string dlls = Read(RegistryHive.LocalMachine, view, WindowsNtWindows, "AppInit_DLLs") as string;
                f.Locations.Add(PathOf(RegistryHive.LocalMachine, view, WindowsNtWindows, "AppInit_DLLs"));
                if (AsInt(Read(RegistryHive.LocalMachine, view, WindowsNtWindows, "LoadAppInit_DLLs")) != 0 && !string.IsNullOrWhiteSpace(dlls))
                    found.Add(dlls.Trim());
            }
            f.Found = found.Count > 0 ? string.Join("; ", found) : Missing();
            if (found.Count > 0) f.Status = RepairStatus.Changed;
            return f;
        }

        // ---- How .exe files open, and hidden files ----

        /// <summary>exefile\shell\open\command: how every .exe is started. Windows: "%1" %*</summary>
        static RepairFinding ExeOpenCommand()
        {
            const string key = @"Software\Classes\exefile\shell\open\command";
            var f = new RepairFinding { Id = "exe_open", Area = "files", Expected = "\"%1\" %*" };
            string machine = Read(RegistryHive.LocalMachine, RegistryView.Registry64, key, "") as string;
            string user = Read(RegistryHive.CurrentUser, RegistryView.Registry64, key, "") as string;
            f.Locations.Add(PathOf(RegistryHive.LocalMachine, RegistryView.Registry64, key, ""));
            f.Locations.Add(PathOf(RegistryHive.CurrentUser, RegistryView.Registry64, key, ""));
            f.Found = (machine ?? Missing()) + (user != null ? "  ·  " + HiveName(RegistryHive.CurrentUser) + ": " + user : "");
            if (machine == null || machine.Replace(" ", "") != "\"%1\"%*" || user != null) f.Status = RepairStatus.Changed;
            return f;
        }

        /// <summary>.exe → exefile: which file type .exe files are. Windows: exefile, with no per-user override.</summary>
        static RepairFinding ExeAssociation()
        {
            const string key = @"Software\Classes\.exe";
            var f = new RepairFinding { Id = "exe_assoc", Area = "files", Expected = "exefile" };
            string machine = Read(RegistryHive.LocalMachine, RegistryView.Registry64, key, "") as string;
            string user = Read(RegistryHive.CurrentUser, RegistryView.Registry64, key, "") as string;
            f.Locations.Add(PathOf(RegistryHive.LocalMachine, RegistryView.Registry64, key, ""));
            f.Locations.Add(PathOf(RegistryHive.CurrentUser, RegistryView.Registry64, key, ""));
            f.Found = (machine ?? Missing()) + (user != null ? "  ·  " + HiveName(RegistryHive.CurrentUser) + ": " + user : "");
            bool userOk = user == null || user.Equals("exefile", StringComparison.OrdinalIgnoreCase);
            if (machine == null || !machine.Equals("exefile", StringComparison.OrdinalIgnoreCase) || !userOk) f.Status = RepairStatus.Changed;
            return f;
        }

        /// <summary>
        /// SHOWALL\CheckedValue: what "Show hidden files" stores. Malware sets it to 0 so the option never sticks and
        /// its files stay hidden. Windows: 1.
        /// </summary>
        static RepairFinding ShowHidden()
        {
            var f = new RepairFinding { Id = "show_hidden", Area = "files", Expected = "1" };
            object v = Read(RegistryHive.LocalMachine, RegistryView.Registry64, ShowAll, "CheckedValue");
            f.Locations.Add(PathOf(RegistryHive.LocalMachine, RegistryView.Registry64, ShowAll, "CheckedValue"));
            f.Found = v == null ? Missing() : Describe(v);
            if (v != null && AsInt(v) != 1) f.Status = RepairStatus.Changed;
            return f;
        }

        // ---- Security ----

        /// <summary>Policies that switch Microsoft Defender (or its real-time protection) off.</summary>
        static RepairFinding DefenderPolicies()
        {
            var f = new RepairFinding { Id = "defender", Area = "security", Expected = Missing() };
            var found = new List<string>();
            foreach (var item in new[] { new[] { Defender, "DisableAntiSpyware" }, new[] { Defender, "DisableAntiVirus" }, new[] { Defender + @"\Real-Time Protection", "DisableRealtimeMonitoring" } })
            {
                f.Locations.Add(PathOf(RegistryHive.LocalMachine, RegistryView.Registry64, item[0], item[1]));
                if (AsInt(Read(RegistryHive.LocalMachine, RegistryView.Registry64, item[0], item[1])) != 0) found.Add(item[1] + " = 1");
            }
            f.Found = found.Count > 0 ? string.Join(", ", found) : Missing();
            if (found.Count > 0) f.Status = RepairStatus.Changed;
            return f;
        }

        /// <summary>SafeBoot\Minimal and \Network: without them Windows can't start in Safe Mode.</summary>
        static RepairFinding SafeMode()
        {
            var f = new RepairFinding { Id = "safe_mode", Area = "security", Expected = L.T("repair.safe_mode.expected") };
            var missing = new List<string>();
            foreach (var mode in new[] { "Minimal", "Network" })
            {
                f.Locations.Add(PathOf(RegistryHive.LocalMachine, RegistryView.Registry64, SafeBoot + "\\" + mode, null));
                if (!Exists(RegistryHive.LocalMachine, RegistryView.Registry64, SafeBoot + "\\" + mode)) missing.Add(mode);
            }
            f.Found = missing.Count == 0 ? L.T("repair.safe_mode.expected") : L.T("repair.safe_mode.missing", string.Join(", ", missing));
            if (missing.Count > 0) f.Status = RepairStatus.Changed;
            return f;
        }

        // ---- Network ----

        /// <summary>A proxy or proxy script: all web traffic goes through it. Windows: none.</summary>
        static RepairFinding Proxy()
        {
            var f = new RepairFinding { Id = "proxy", Area = "network", Expected = L.T("repair.none") };
            string server = Read(RegistryHive.CurrentUser, RegistryView.Registry64, InternetSettings, "ProxyServer") as string;
            string script = Read(RegistryHive.CurrentUser, RegistryView.Registry64, InternetSettings, "AutoConfigURL") as string;
            bool enabled = AsInt(Read(RegistryHive.CurrentUser, RegistryView.Registry64, InternetSettings, "ProxyEnable")) != 0;
            f.Locations.Add(PathOf(RegistryHive.CurrentUser, RegistryView.Registry64, InternetSettings, "ProxyServer"));
            f.Locations.Add(PathOf(RegistryHive.CurrentUser, RegistryView.Registry64, InternetSettings, "AutoConfigURL"));
            var found = new List<string>();
            if (enabled && !string.IsNullOrWhiteSpace(server)) found.Add(L.T("repair.proxy.server", server.Trim()));
            if (!string.IsNullOrWhiteSpace(script)) found.Add(L.T("repair.proxy.script", script.Trim()));
            f.Found = found.Count > 0 ? string.Join("  ·  ", found) : L.T("repair.none");
            if (found.Count > 0) f.Status = RepairStatus.Unusual;   // common at work; malware does it too
            return f;
        }

        // ---- Registry helpers (read-only) ----

        static IEnumerable<RegistryView> Views()
        {
            yield return RegistryView.Registry64;
            if (Environment.Is64BitOperatingSystem) yield return RegistryView.Registry32;
        }

        static object Read(RegistryHive hive, RegistryView view, string path, string value)
        {
            try
            {
                using (var root = RegistryKey.OpenBaseKey(hive, view))
                using (var k = root.OpenSubKey(path))
                    return k == null ? null : k.GetValue(value, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            }
            catch (System.Security.SecurityException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (IOException) { return null; }
        }

        static bool Exists(RegistryHive hive, RegistryView view, string path)
        {
            try
            {
                using (var root = RegistryKey.OpenBaseKey(hive, view))
                using (var k = root.OpenSubKey(path))
                    return k != null;
            }
            catch (System.Security.SecurityException) { return true; }   // can't tell: don't report it missing
            catch (UnauthorizedAccessException) { return true; }
        }

        static string[] SubKeys(RegistryHive hive, RegistryView view, string path)
        {
            try
            {
                using (var root = RegistryKey.OpenBaseKey(hive, view))
                using (var k = root.OpenSubKey(path))
                    return k == null ? new string[0] : k.GetSubKeyNames();
            }
            catch (System.Security.SecurityException) { return new string[0]; }
            catch (UnauthorizedAccessException) { return new string[0]; }
            catch (IOException) { return new string[0]; }
        }

        static int AsInt(object v)
        {
            if (v is int) return (int)v;
            if (v is long) return (int)(long)v;
            int parsed;
            return v != null && int.TryParse(v.ToString().Trim(), out parsed) ? parsed : 0;
        }

        static string Describe(object v)
        {
            var arr = v as string[];
            return arr != null ? string.Join("; ", arr) : Convert.ToString(v);
        }

        /// <summary>Lower-case, environment variables expanded, quotes and spaces trimmed: for comparing paths and commands.</summary>
        static string Simplify(string s)
        {
            return Environment.ExpandEnvironmentVariables(s).Trim().Trim('"').Trim().ToLowerInvariant();
        }

        static string Missing() { return L.T("repair.missing"); }

        static string HiveName(RegistryHive hive) { return hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU"; }

        static string PathOf(RegistryHive hive, RegistryView view, string path, string value)
        {
            if (hive == RegistryHive.LocalMachine && view == RegistryView.Registry32 && path.StartsWith("Software\\", StringComparison.OrdinalIgnoreCase))
                path = "Software\\WOW6432Node\\" + path.Substring("Software\\".Length);
            string full = HiveName(hive) + "\\" + path;
            if (value == null) return full;
            return full + "  →  " + (value.Length == 0 ? L.T("repair.default_value") : value);
        }
    }
}
