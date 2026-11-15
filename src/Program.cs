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
                case "--programs": return Report(ListPrograms);
                case "--leftovers": return Report(() => ListLeftovers(args.Contains("--all")));
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
                Console.WriteLine("{0,10}  {1,-16} {2}", Bytes.Format(l.Bytes), Ui.Ago(l.LastChange), l.Path);
            Console.WriteLine(L.T("cli.leftovers_total", leftovers.Count, Bytes.Format(leftovers.Sum(l => l.Bytes))));
            if (!includeInUse) return;
            Console.WriteLine();
            foreach (var l in found.Where(l => l.InUse).OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine("{0,-60} {1}", l.Path, l.Owner.Describe());
        }
    }
}
