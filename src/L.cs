using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Sweepfella
{
    /// <summary>
    /// Translated strings. Each language is a lang/&lt;code&gt;.txt file embedded in the .exe with
    /// "key = text" lines ("\n" = line break, "#" = comment). Keys missing from a language fall back to
    /// English; keys missing from English show up as [key] so they are easy to spot.
    /// </summary>
    static class L
    {
        public const string DefaultCode = "en";
        const string ResourcePrefix = "lang.";

        static Dictionary<string, string> current = new Dictionary<string, string>();
        static Dictionary<string, string> fallback;

        public static string Code { get; private set; }
        public static CultureInfo Culture { get; private set; }

        /// <summary>Bundled languages: code → native name ("English", "Español").</summary>
        public static List<KeyValuePair<string, string>> Available()
        {
            return typeof(L).Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(ResourcePrefix) && n.EndsWith(".txt"))
                .Select(n => n.Substring(ResourcePrefix.Length, n.Length - ResourcePrefix.Length - 4))
                .Select(code => new KeyValuePair<string, string>(code, Load(code).ContainsKey("_name") ? Load(code)["_name"] : code))
                .OrderBy(kv => kv.Value, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public static void Use(string code)
        {
            if (fallback == null) fallback = Load(DefaultCode);
            var table = string.IsNullOrEmpty(code) ? null : Load(code);
            if (table == null || table.Count == 0) { code = DefaultCode; table = fallback; }
            current = table;
            Code = code;

            string cultureName;
            if (!current.TryGetValue("_culture", out cultureName)) cultureName = code;
            try { Culture = CultureInfo.GetCultureInfo(cultureName); }
            catch (CultureNotFoundException) { Culture = CultureInfo.InvariantCulture; }

            // Numbers and dates follow the chosen language, background threads included.
            Thread.CurrentThread.CurrentCulture = Culture;
            Thread.CurrentThread.CurrentUICulture = Culture;
            CultureInfo.DefaultThreadCurrentCulture = Culture;
            CultureInfo.DefaultThreadCurrentUICulture = Culture;
        }

        public static string T(string key)
        {
            string value;
            if (current.TryGetValue(key, out value)) return value;
            if (fallback != null && fallback.TryGetValue(key, out value)) return value;
            return "[" + key + "]";
        }

        public static string T(string key, params object[] args)
        {
            return string.Format(Culture ?? CultureInfo.CurrentCulture, T(key), args);
        }

        static readonly Dictionary<string, Dictionary<string, string>> cache = new Dictionary<string, Dictionary<string, string>>();

        static Dictionary<string, string> Load(string code)
        {
            Dictionary<string, string> table;
            if (cache.TryGetValue(code, out table)) return table;
            table = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var stream = typeof(L).Assembly.GetManifestResourceStream(ResourcePrefix + code + ".txt"))
            {
                if (stream != null)
                {
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            line = line.Trim();
                            if (line.Length == 0 || line[0] == '#') continue;
                            int eq = line.IndexOf('=');
                            if (eq <= 0) continue;
                            table[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim().Replace("\\n", "\n");
                        }
                    }
                }
            }
            cache[code] = table;
            return table;
        }
    }
}
