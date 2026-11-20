using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Sweepfella
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            L.Use(Settings.Get("language") ?? L.DefaultCode);

            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            switch (mode)
            {
                case "--analyze": return Report(Analyze);
                case "--details": return Report(Details);
                case "--registry": return Report(Registry);
                case "--programs": return Report(ListPrograms);
                case "--leftovers": return Report(() => ListLeftovers(args.Contains("--all")));
                case "--check-updates":
                    return Report(() =>
                    {
                        var latest = Updates.CheckNow().Result;
                        Console.WriteLine("Running v{0}; latest release {1} ({2}) {3}", Updates.Current.ToString(3), latest.Tag, latest.Url,
                                          latest.IsNewer ? "is newer" : "is not newer");
                    });
                case "--inspect":
                    if (args.Length < 2) return Report(() => Console.WriteLine("Usage: --inspect <folder>"));
                    return Report(() => Inspect(args[1]));
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new WindowHost());
            return 0;
        }

        // ---- Console modes: read-only, they never delete anything. Handy to review what the app would do. ----

        static int Report(Action body)
        {
            if (!Console.IsOutputRedirected) Native.AttachConsole(-1);
            else Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            body();
            return 0;
        }

        static void Analyze()
        {
            bool admin = Elevation.IsAdmin();
            DateTime cutoff = DateTime.UtcNow.AddHours(-24);
            long total = 0;
            foreach (var c in Catalog.Build())
            {
                if (c.RequiresAdmin && !admin) { Console.WriteLine("{0,-42} {1}", c.Name, L.T("cli.requires_admin")); continue; }
                var r = Cleaner.Run(c, false, cutoff, CancellationToken.None);
                Console.WriteLine(L.T("cli.analyze_line", c.Name, r.Files, Bytes.Format(r.Bytes)));
                total += r.Bytes;
            }
            Console.WriteLine(L.T("cli.total", Bytes.Format(total)));
        }

        static void Details()
        {
            bool admin = Elevation.IsAdmin();
            DateTime cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var c in Catalog.Build())
            {
                var entries = Cleaner.List(c, cutoff, CancellationToken.None);
                Console.WriteLine("== {0} ({1}){2}", c.Name, entries.Count, c.RequiresAdmin && !admin ? " " + L.T("cli.requires_admin") : "");
                foreach (var e in entries)
                    Console.WriteLine("  {0,10} {1,7:N0}  {2}{3}", Bytes.Format(e.Bytes), e.Files,
                                      Path.Combine(e.Location ?? "", e.Name), e.Deleted.HasValue ? "  (" + Ui.Ago(e.Deleted.Value) + ")" : "");
            }
        }

                static void Registry()
        {
            var findings = RepairScanner.Scan();
            foreach (var area in RepairScanner.Areas)
            {
                Console.WriteLine("== " + L.T("repairs.area." + area));
                foreach (var f in findings.Where(f => f.Area == area))
                {
                    string name = L.T("repair." + f.Id + ".name") + (f.Subject != null ? ": " + f.Subject : "");
                    Console.WriteLine("  {0,-10} {1,-45} {2}", L.T("repairs.status." + f.Status.ToString().ToLowerInvariant()), name, f.Found);
                    if (f.Status != RepairStatus.Ok)
                    {
                        Console.WriteLine("             expected: " + f.Expected);
                        foreach (var where in f.Locations) Console.WriteLine("             at: " + where);
                        if (f.Note != null) Console.WriteLine("             note: " + f.Note);
                    }
                }
            }
            int changed = findings.Count(f => f.Status == RepairStatus.Changed), unusual = findings.Count(f => f.Status == RepairStatus.Unusual);
            Console.WriteLine(changed + unusual == 0 ? L.T("repairs.summary_ok", findings.Count) : L.T("repairs.summary", changed, unusual, findings.Count));
        }

        static void ListPrograms()
        {
            var programs = ProgramRegistry.Read();
            var scanner = new UsageScanner(programs);
            scanner.Run(null, CancellationToken.None);
            scanner.Apply(programs);
            Console.WriteLine(L.T("cli.programs_header", programs.Count, scanner.FilesScanned, scanner.Roots.Count));
            foreach (var g in programs.GroupBy(p => p.Kind).OrderBy(g => Array.IndexOf(ProgramKinds.Order, g.Key)))
            {
                Console.WriteLine();
                Console.WriteLine("== " + ProgramKinds.Title(g.Key));
                foreach (var p in g.OrderBy(p => p.Name))
                {
                    Console.WriteLine("  {0,-60} {1,-16} {2}", p.ShortName, p.Version, p.StatusText);
                    if (p.Runtime != null && p.UsedBy != null)
                        foreach (var u in p.UsedBy.Take(6)) Console.WriteLine("      " + L.T("cli.used_by", u));
                }
            }
        }

        static void Inspect(string dir)
        {
            var c = LeftoverFinder.Investigate(dir, CancellationToken.None);
            Console.WriteLine(dir);
            if (c.Running.Count > 0) Console.WriteLine(L.T("leftovers.clue.running") + ": " + string.Join(", ", c.Running));
            if (c.OnlyCaches) Console.WriteLine(L.T("leftovers.clue.caches"));
            if (c.UnityGames.Count > 0) Console.WriteLine(L.T("leftovers.clue.unity") + ": " + string.Join(", ", c.UnityGames));
            if (c.WebView) Console.WriteLine(L.T("leftovers.clue.webview"));
            if (c.Sources.Count > 0) Console.WriteLine(L.T("leftovers.clue.sources") + ": " + string.Join("; ", c.Sources));
            Console.WriteLine(L.T("leftovers.clue.contents") + (c.Truncated ? " " + L.T("leftovers.clue.contents_partial") : "") + ":");
            foreach (var kv in c.Contents.Take(10)) Console.WriteLine("  {0,10}  {1}", Bytes.Format(kv.Value), kv.Key);
            if (c.LatestFile != null) Console.WriteLine(L.T("leftovers.clue.latest") + ": " + c.LatestFile + " (" + Ui.Ago(c.LatestChange) + ")");
        }

        static void ListLeftovers(bool includeInUse)
        {
            var found = LeftoverFinder.Find(LeftoverFinder.LoadIgnored(), null, CancellationToken.None);
            var leftovers = found.Where(l => !l.InUse).ToList();
            foreach (var l in leftovers.OrderByDescending(l => l.Bytes))
            {
                Console.WriteLine("{0,10}  {1,-16} {2}", Bytes.Format(l.Bytes), Ui.Ago(l.LastChange), l.Path);
                if (l.PartlyInUse)
                    foreach (var p in l.Parts)
                        Console.WriteLine("{0,10}  {1,-16}   {2}{3}", Bytes.Format(p.Bytes), Ui.Ago(p.LastChange), p.Name, p.InUse ? "  <- " + p.Owner.Describe() : "");
            }
            Console.WriteLine(L.T("cli.leftovers_total", leftovers.Count, Bytes.Format(leftovers.Sum(l => l.Bytes))));
            if (!includeInUse) return;
            Console.WriteLine();
            foreach (var l in found.Where(l => l.InUse).OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine("{0,-60} {1}", l.Path, l.Owner.Describe());
        }
    }
}
