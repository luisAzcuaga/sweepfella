using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

namespace Sweepfella
{
    /// <summary>
    /// System Restore points, read through WMI and deleted with the System Restore API. Both need administrator.
    /// Windows doesn't say how much space a single restore point takes: they all share the shadow copy storage,
    /// whose total is the only figure available, so it's measured before and after deleting.
    /// </summary>
    static class RestorePoints
    {
        public sealed class Point
        {
            public int Sequence;
            public string Description;
            public DateTime Created;   // local time

            public string Key { get { return "restore-point:" + Sequence; } }
        }

        /// <summary>Every restore point, oldest first. Empty when System Restore is off or can't be read.</summary>
        public static List<Point> All()
        {
            var list = new List<Point>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\default"),
                                                                   new ObjectQuery("SELECT SequenceNumber, Description, CreationTime FROM SystemRestore")))
                using (var results = searcher.Get())
                    foreach (ManagementObject o in results)
                        using (o)
                        {
                            var p = new Point
                            {
                                Sequence = Convert.ToInt32(o["SequenceNumber"]),
                                Description = (o["Description"] as string ?? "").Trim(),
                            };
                            var created = o["CreationTime"] as string;
                            if (string.IsNullOrEmpty(created)) continue;
                            p.Created = ManagementDateTimeConverter.ToDateTime(created);
                            list.Add(p);
                        }
            }
            catch (ManagementException) { }
            catch (UnauthorizedAccessException) { }
            catch (COMException) { }
            return list.OrderBy(p => p.Created).ThenBy(p => p.Sequence).ToList();
        }

        /// <summary>
        /// The ones a cleanup may delete: all but the newest, and none made after cutoffUtc (so "keep the last 24
        /// hours" also protects the point taken right before today's update or driver install).
        /// </summary>
        public static List<Point> Old(DateTime cutoffUtc)
        {
            var all = All();
            if (all.Count > 0) all.RemoveAt(all.Count - 1);
            return all.Where(p => cutoffUtc == DateTime.MinValue || p.Created.ToUniversalTime() <= cutoffUtc).ToList();
        }

        public static Point Newest()
        {
            return All().LastOrDefault();
        }

        public static bool Remove(Point p)
        {
            try { return SRRemoveRestorePoint(p.Sequence) == 0; }
            catch (DllNotFoundException) { return false; }          // no System Restore (Windows Server)
            catch (EntryPointNotFoundException) { return false; }
        }

        /// <summary>Space all shadow copies (restore points among them) use on every drive, or -1 if unknown.</summary>
        public static long StorageUsed()
        {
            try
            {
                long total = 0;
                using (var searcher = new ManagementObjectSearcher("SELECT UsedSpace FROM Win32_ShadowStorage"))
                using (var results = searcher.Get())
                    foreach (ManagementObject o in results)
                        using (o) total += Convert.ToInt64(o["UsedSpace"]);
                return total;
            }
            catch (ManagementException) { }
            catch (UnauthorizedAccessException) { }
            catch (COMException) { }
            return -1;
        }

        [DllImport("srclient.dll")]
        static extern int SRRemoveRestorePoint(int dwRPNum);
    }
}
