using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;

namespace Sweepfella
{
    /// <summary>
    /// Portable settings: a single Sweepfella.ini next to the executable ("key = value" lines).
    /// Nothing is written anywhere else, and the file is only created once a setting actually changes,
    /// so a cleaner that is just opened and closed leaves no trace. If the folder is read-only the
    /// settings simply aren't saved (and <see cref="SaveFailed"/> fires once so the user can be told).
    /// </summary>
    static class Settings
    {
        public const string FileName = "Sweepfella.ini";
        const char ListSeparator = '|';   // can't appear in Windows paths

        static Dictionary<string, string> values;
        static bool disabled;    // after "delete my data": never write again this session
        static bool? writable;
        static bool failureReported;

        /// <summary>Raised the first time a setting can't be saved.</summary>
        public static event Action SaveFailed;

        public static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName); }
        }

        /// <summary>Folder where v0.1.0 kept its settings; removed by <see cref="DeleteAll"/>.</summary>
        public static string LegacyFolder
        {
            get { return Env.Combine(Env.Roaming, "Sweepfella"); }
        }

        public static bool FileExists { get { return File.Exists(FilePath); } }

        public static bool HasAnyData
        {
            get { return FileExists || (LegacyFolder != null && Directory.Exists(LegacyFolder)); }
        }

        /// <summary>Whether settings can be saved next to the executable (probed without leaving a file behind).</summary>
        public static bool CanWrite
        {
            get
            {
                if (writable == null) writable = Probe();
                return writable.Value;
            }
        }

        static bool Probe()
        {
            try
            {
                if (FileExists)
                {
                    using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
                    return true;
                }
                string probe = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (SecurityException) { return false; }
        }

        static Dictionary<string, string> Values
        {
            get
            {
                if (values != null) return values;
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (FileExists)
                        foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                        {
                            string line = raw.Trim();
                            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                            int eq = line.IndexOf('=');
                            if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                        }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return values;
            }
        }

        public static string Get(string key)
        {
            string v;
            return Values.TryGetValue(key, out v) ? v : null;
        }

        /// <summary>Stores a value (null removes it). Only touches the disk when the value really changes.</summary>
        public static void Set(string key, string value)
        {
            if (disabled) return;
            string current;
            bool exists = Values.TryGetValue(key, out current);
            if (exists ? current == value : value == null) return;
            if (value == null) Values.Remove(key);
            else Values[key] = value;
            Save();
        }

        public static List<string> GetList(string key)
        {
            return (Get(key) ?? "").Split(new[] { ListSeparator }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        public static void SetList(string key, IEnumerable<string> items)
        {
            var sorted = items.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            Set(key, sorted.Count == 0 ? null : string.Join(ListSeparator.ToString(), sorted));
        }

        static void Save()
        {
            try
            {
                var lines = new List<string> { "; Sweepfella settings. Delete this file to reset them." };
                lines.AddRange(Values.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key + " = " + kv.Value));
                File.WriteAllLines(FilePath, lines, new UTF8Encoding(false));
                writable = true;
            }
            catch (IOException) { ReportFailure(); }
            catch (UnauthorizedAccessException) { ReportFailure(); }
            catch (SecurityException) { ReportFailure(); }
        }

        static void ReportFailure()
        {
            writable = false;
            if (failureReported) return;
            failureReported = true;
            var handler = SaveFailed;
            if (handler != null) handler();
        }

        /// <summary>Deletes every file Sweepfella created and stops saving for the rest of the session.</summary>
        public static void DeleteAll()
        {
            disabled = true;
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try { if (FileExists) File.Delete(FilePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try { if (LegacyFolder != null && Directory.Exists(LegacyFolder)) Directory.Delete(LegacyFolder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Lifetime counter of disk space Sweepfella has actually given back.</summary>
    static class Stats
    {
        const string DateFormat = "yyyy-MM-dd";

        public static long Freed
        {
            get
            {
                long v;
                return long.TryParse(Settings.Get("freed_bytes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
            }
        }

        /// <summary>Day of the first recorded cleanup, or null if nothing was freed yet.</summary>
        public static DateTime? Since
        {
            get
            {
                DateTime d;
                return DateTime.TryParseExact(Settings.Get("freed_since"), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d : (DateTime?)null;
            }
        }

        public static void Add(long bytes)
        {
            if (bytes <= 0) return;
            if (Since == null) Settings.Set("freed_since", DateTime.Today.ToString(DateFormat, CultureInfo.InvariantCulture));
            Settings.Set("freed_bytes", (Freed + bytes).ToString(CultureInfo.InvariantCulture));
        }

        public static void Reset()
        {
            Settings.Set("freed_bytes", null);
            Settings.Set("freed_since", null);
        }
    }

    /// <summary>
    /// Free space on every fixed drive. Comparing two snapshots measures what an uninstaller really freed,
    /// whatever size the program claimed and whichever drive it lived on.
    /// </summary>
    static class DiskSpace
    {
        public static Dictionary<string, long> Snapshot()
        {
            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady) result[drive.Name] = drive.TotalFreeSpace;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return result;
        }

        /// <summary>Space freed since the snapshot. Drives that lost space (other programs writing) count as zero.</summary>
        public static long FreedSince(Dictionary<string, long> before)
        {
            var now = Snapshot();
            long total = 0;
            foreach (var kv in before)
            {
                long after;
                if (now.TryGetValue(kv.Key, out after) && after > kv.Value) total += after - kv.Value;
            }
            return total;
        }
    }
}
