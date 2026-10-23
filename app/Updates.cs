// "Check for updates": asks GitHub for the game repo's latest release. Only when the user presses the button; it never
// downloads anything, the user opens the release page himself.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace Taareeb.Installer
{
    sealed class Release
    {
        public string Tag, Url;
        public bool Newer;
    }

    static class Updates
    {
        public static Release Latest(GameSpec g)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;     // TLS 1.2 on .NET Framework 4
            var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + g.Github + "/releases/latest");
            req.UserAgent = "TaareebInstaller";
            req.Accept = "application/vnd.github+json";
            req.Timeout = 15000;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    var d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(r.ReadToEnd());
                    var rel = new Release { Tag = Convert.ToString(d["tag_name"]), Url = Convert.ToString(d["html_url"]) };
                    rel.Newer = Compare(rel.Tag, g.Version) > 0;
                    return rel;
                }
            }
            catch (WebException e)
            {
                var resp = e.Response as HttpWebResponse;
                if (resp != null && resp.StatusCode == HttpStatusCode.NotFound) return null;   // no release yet
                throw;
            }
        }

        // "v1.2" vs "1.1"; text that is not a version counts as older
        public static int Compare(string a, string b)
        {
            Version x, y;
            if (!Version.TryParse(Norm(a), out x)) return -1;
            if (!Version.TryParse(Norm(b), out y)) return 1;
            return x.CompareTo(y);
        }

        static string Norm(string v)
        {
            v = (v ?? "").Trim().TrimStart('v', 'V');
            return v.Contains(".") ? v : v + ".0";
        }
    }
}
