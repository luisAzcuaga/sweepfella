using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Sweepfella
{
    /// <summary>The latest published release.</summary>
    sealed class Release
    {
        public Version Version;
        public string Url;      // release page on GitHub
        public bool IsNewer;    // newer than the running version

        public string Tag { get { return "v" + Version.ToString(3); } }
    }

    /// <summary>
    /// Asks GitHub for the latest release. It's a single anonymous request: nothing is stored and
    /// nothing is downloaded — the user is only pointed at the release page.
    /// </summary>
    static class Updates
    {
        const string LatestReleaseApi = "https://api.github.com/repos/luisAzcuaga/sweepfella/releases/latest";
        const int TimeoutMs = 5000;
        const string SettingKey = "check_updates";

        static Task<Release> startupCheck;

        /// <summary>Whether to check at startup (on by default; only "false" is stored, so the default writes nothing).</summary>
        public static bool CheckAtStartup
        {
            get { return Settings.Get(SettingKey) != "false"; }
            set { Settings.Set(SettingKey, value ? null : "false"); }
        }

        public static Version Current
        {
            get { return typeof(Updates).Assembly.GetName().Version; }
        }

        /// <summary>The startup check, run once per session (windows rebuilt after a language change reuse it).</summary>
        public static Task<Release> StartupCheck()
        {
            if (startupCheck == null)
                startupCheck = CheckAtStartup
                    ? Task.Run(() => { try { return Fetch(); } catch (Exception) { return null; } })   // offline, GitHub down…: stay quiet
                    : Task.FromResult<Release>(null);
            return startupCheck;
        }

        /// <summary>Checks now. Throws if GitHub can't be reached.</summary>
        public static Task<Release> CheckNow()
        {
            return Task.Run(() => Fetch());
        }

        static Release Fetch()
        {
            // GitHub only accepts TLS 1.2+, which older .NET Framework setups don't enable by default.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            var request = (HttpWebRequest)WebRequest.Create(LatestReleaseApi);
            request.UserAgent = "Sweepfella/" + Current.ToString(3);   // GitHub requires a User-Agent
            request.Accept = "application/vnd.github+json";
            request.Timeout = TimeoutMs;
            request.ReadWriteTimeout = TimeoutMs;

            string json;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                json = reader.ReadToEnd();

            var tag = Regex.Match(json, @"""tag_name""\s*:\s*""v?(\d+(?:\.\d+){1,3})""");
            var url = Regex.Match(json, @"""html_url""\s*:\s*""(https://github\.com/[^""]+/releases/tag/[^""]+)""");
            if (!tag.Success || !url.Success) throw new InvalidDataException("Unexpected response from GitHub.");

            var latest = ThreePart(Version.Parse(tag.Groups[1].Value));
            return new Release { Version = latest, Url = url.Groups[1].Value, IsNewer = latest > ThreePart(Current) };
        }

        /// <summary>"1.0" and "1.0.0.0" both become 1.0.0, so versions compare cleanly.</summary>
        static Version ThreePart(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }
}
