// Per-game settings: the game.json that build.py embeds in the exe. One engine, one exe per game.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace Taareeb.Installer
{
    // a text in Arabic and English; the UI always shows them on two lines, never mixed on one
    sealed class Bi
    {
        public readonly string Ar, En;
        public Bi(string ar, string en) { Ar = ar ?? ""; En = en ?? ""; }
        public override string ToString() { return Ar + "\r\n" + En; }
    }

    sealed class LinkSpec
    {
        public Bi Text;
        public string Url;
    }

    sealed class GameSpec
    {
        public string Id, ExeName, Version, Github, SiteUrl, BackupDir, PatchRoot, SteamDir;
        public Bi Name, Platform, FolderLabel, FolderHint, Mismatch, Before;
        public string[] MustHave, Processes;
        public List<Bi> Requirements = new List<Bi>(), After = new List<Bi>();
        public List<LinkSpec> Links = new List<LinkSpec>();
        public Ps4PkgSpec Ps4Pkg;           // null when the game has no "PS4 PKG -> folder" step

        public string RepoUrl { get { return "https://github.com/" + Github; } }

        public static GameSpec Load()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("game.json"))
            using (var r = new StreamReader(s, Encoding.UTF8))
                return Parse(r.ReadToEnd());
        }

        public static GameSpec Parse(string json)
        {
            var d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            var g = new GameSpec
            {
                Id = Str(d, "id"), ExeName = Str(d, "exe_name"), Version = Str(d, "version"),
                Github = Str(d, "github"), SiteUrl = Str(d, "site_url", "https://iosamai.com"),
                BackupDir = Str(d, "backup_dir"), PatchRoot = Str(d, "patch_root", ""), SteamDir = Str(d, "steam_dir", ""),
                Name = BiOf(d, "name"), Platform = BiOf(d, "platform"),
                FolderLabel = BiOf(d, "folder_label"), FolderHint = BiOf(d, "folder_hint"),
                Mismatch = BiOf(d, "mismatch"), Before = BiOf(d, "before"),
                MustHave = Strs(d, "must_have"), Processes = Strs(d, "close_processes"),
            };
            if (g.MustHave.Length == 0)
                throw new InvalidDataException("game.json: must_have needs at least one file that proves the folder");
            foreach (object o in Arr(d, "requirements")) g.Requirements.Add(BiOf(o));
            foreach (object o in Arr(d, "after_install")) g.After.Add(BiOf(o));
            foreach (Dictionary<string, object> l in Arr(d, "links"))
                g.Links.Add(new LinkSpec { Text = BiOf(l, "text"), Url = Str(l, "url") });
            object p;
            if (d.TryGetValue("ps4_pkg", out p) && p != null) g.Ps4Pkg = Ps4PkgSpec.From((Dictionary<string, object>)p);
            return g;
        }

        internal static string Str(Dictionary<string, object> d, string key, string fallback = null)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            if (fallback != null) return fallback;
            throw new InvalidDataException("game.json: missing \"" + key + "\"");
        }

        internal static object[] Arr(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v is object[] ? (object[])v : new object[0];
        }

        static string[] Strs(Dictionary<string, object> d, string key)
        {
            var a = Arr(d, key);
            var r = new string[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = Convert.ToString(a[i]);
            return r;
        }

        internal static Bi BiOf(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? BiOf(v) : new Bi("", "");
        }

        internal static Bi BiOf(object o)
        {
            var d = (Dictionary<string, object>)o;
            return new Bi(Str(d, "ar", ""), Str(d, "en", ""));
        }
    }

    sealed class Ps4PkgSpec
    {
        public string FolderName, CheckFile;
        public Bi GameLabel, UpdateLabel, SizeNote, Note;
        public List<LinkSpec> Links = new List<LinkSpec>();

        public static Ps4PkgSpec From(Dictionary<string, object> d)
        {
            var p = new Ps4PkgSpec
            {
                FolderName = GameSpec.Str(d, "folder_name"), CheckFile = GameSpec.Str(d, "check_file", "eboot.bin"),
                GameLabel = GameSpec.BiOf(d, "game_label"), UpdateLabel = GameSpec.BiOf(d, "update_label"),
                SizeNote = GameSpec.BiOf(d, "size_note"), Note = GameSpec.BiOf(d, "note"),
            };
            foreach (Dictionary<string, object> l in GameSpec.Arr(d, "links"))
                p.Links.Add(new LinkSpec { Text = GameSpec.BiOf(l, "text"), Url = GameSpec.Str(l, "url") });
            return p;
        }
    }
}
