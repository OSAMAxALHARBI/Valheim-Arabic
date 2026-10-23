// The patch kit next to the exe: patch/manifest.json (+ patch/data.bin and/or patch/files/), written by each game's
// TOOLS/make_release.py. No game file is in the kit. manifest:
//   files[]: path (under the patch root), stock_sha256 (null = a file the game does not have), patched_sha256, and
//     ops [[kind, offset, length]...] (0 = copy from the original file, 1 = copy from data.bin, 2 = zeros;
//     [3, offset, csize, usize, start, length] = a slice of an LZ4 block of the original: Unity bundles), or no ops:
//     the whole file is in patch/files/<path>. Optional "requires": a path that must exist, else the file is skipped
//     (Borderlands 2: a DLC's files only when that DLC is installed).
//   exe + exe_entries[] (optional): {name, stock, patched} hex values stored right after "<name>\0" inside that file
//     (Borderlands 2 keeps package hashes in its exe); found by searching, so a game update that moves them is fine.
// Install: check everything, back up the originals, rebuild each file, check its SHA-256, swap. Uninstall: put the
// backups back, delete the files the game does not have, write the original entry values back.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Taareeb.Installer
{
    // a failure the user can act on, with the words shown to him
    sealed class Failure : Exception
    {
        public readonly Bi Text;
        public Failure(string ar, string en) : base(en) { Text = new Bi(ar, en); }
        public Failure(Bi text) : base(text.En) { Text = text; }
    }

    enum FileState { Missing, Stock, Patched, Older, Changed }

    sealed class KitFile
    {
        public string Path, Stock, Patched, Requires;
        public object[] Ops;                    // null: the whole file is in patch/files/
    }

    sealed class KitEntry
    {
        public string File, Name, Stock, Patched;
        public int Length { get { return Stock.Length / 2; } }
    }

    sealed class Report
    {
        public bool IsGame, HasBackup;
        public int Total, Stock, Patched, Older, Changed, Missing;
        public string InstalledVersion = "";   // from the backup marker; "" when unknown
        public string FirstProblem = "";

        public bool Installed { get { return IsGame && Patched == Total; } }
        public bool Original { get { return IsGame && Stock == Total; } }
        public bool CanInstall { get { return IsGame && Missing == 0 && Changed == 0 && Patched < Total; } }
    }

    enum Outcome { Installed, Updated, AlreadyInstalled }

    static class Kit
    {
        public static Action<string> Log = delegate { };
        public static readonly string Here = AppDomain.CurrentDomain.BaseDirectory;
        const string Marker = "taareeb-install.json";
        static List<KitFile> files;
        static List<KitEntry> entries;
        static string kitVersion = "";

        public static string KitVersion { get { Load(); return kitVersion; } }
        public static List<KitFile> Files() { Load(); return files; }

        static string PatchDir { get { return System.IO.Path.Combine(Here, "patch"); } }

        static void Load()
        {
            if (files != null) return;
            string p = System.IO.Path.Combine(PatchDir, "manifest.json");
            if (!File.Exists(p))
                throw new Failure("مجلد patch ناقص. فك ضغط ملف التعريب كامل وشغّل البرنامج من داخله.",
                                  "The patch folder is missing. Extract the whole zip and run the program from inside it.");
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var m = (Dictionary<string, object>)js.DeserializeObject(File.ReadAllText(p, Encoding.UTF8));
            object v;
            kitVersion = m.TryGetValue("version", out v) ? Convert.ToString(v) : "";
            var list = new List<KitFile>();
            foreach (Dictionary<string, object> f in (object[])m["files"])
            {
                object ops, req;
                list.Add(new KitFile
                {
                    Path = SafeRel((string)f["path"]), Stock = (string)f["stock_sha256"], Patched = (string)f["patched_sha256"],
                    Ops = f.TryGetValue("ops", out ops) ? (object[])ops : null,
                    Requires = f.TryGetValue("requires", out req) && req != null ? SafeRel((string)req) : null,
                });
            }
            var ents = new List<KitEntry>();
            if (m.TryGetValue("exe_entries", out v) && v is object[])
                foreach (Dictionary<string, object> e in (object[])v)
                    ents.Add(new KitEntry { File = SafeRel((string)m["exe"]), Name = (string)e["name"], Stock = (string)e["stock"], Patched = (string)e["patched"] });
            foreach (var f in list)
                if (f.Ops != null && !File.Exists(System.IO.Path.Combine(PatchDir, "data.bin")) || f.Ops == null && !File.Exists(Source(f)))
                    throw new Failure("مجلد patch ناقص. فك ضغط ملف التعريب كامل وشغّل البرنامج من داخله.",
                                      "The patch folder is incomplete. Extract the whole zip and run the program from inside it.");
            files = list;
            entries = ents;
        }

        static string SafeRel(string rel)
        {
            rel = rel.Replace('/', '\\');
            if (rel.Length == 0 || System.IO.Path.IsPathRooted(rel) || rel.Contains(".."))
                throw new Failure("ملف patch/manifest.json فيه مسار غلط.", "patch/manifest.json has a bad path: " + rel);
            return rel;
        }

        static string Source(KitFile f) { return System.IO.Path.Combine(PatchDir, "files\\" + f.Path); }

        // the files that apply to this folder: not the ones the translation leaves as they are, and a DLC's files only
        // when the player has that DLC
        static List<KitFile> Active(GameSpec g, string dir)
        {
            var r = new List<KitFile>();
            foreach (var f in Files())
                if (f.Stock != f.Patched &&
                    (f.Requires == null || File.Exists(System.IO.Path.Combine(dir, f.Requires)) || Directory.Exists(System.IO.Path.Combine(dir, f.Requires))))
                    r.Add(f);
            return r;
        }

        // ------------------------------------------------------------ folders

        public static bool IsGameFolder(GameSpec g, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            try
            {
                if (!Directory.Exists(dir)) return false;
                foreach (string f in g.MustHave)
                    if (!File.Exists(System.IO.Path.Combine(dir, f.Replace('/', '\\')))) return false;
                return true;
            }
            catch (ArgumentException) { return false; }      // a typed path with bad characters
        }

        static string Root(GameSpec g, string dir) { return System.IO.Path.Combine(dir, g.PatchRoot.Replace('/', '\\')); }
        static string Backup(GameSpec g, string dir) { return System.IO.Path.Combine(dir, g.BackupDir); }

        static string StateFile(GameSpec g)
        {
            // TAAREEB_STATE_DIR: test runs keep their scratch folders out of the player's remembered folder
            // (2026-10-23: the test harness's folder was remembered on the dev PC and the next real install went there)
            string root = Environment.GetEnvironmentVariable("TAAREEB_STATE_DIR");
            if (string.IsNullOrEmpty(root)) root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string d = System.IO.Path.Combine(root, "Taareeb\\" + g.Id);
            return System.IO.Path.Combine(d, "game_folder.txt");
        }

        public static void Remember(GameSpec g, string dir)
        {
            try
            {
                string f = StateFile(g);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f));
                File.WriteAllText(f, dir, Encoding.UTF8);
            }
            catch (Exception e) { Log("could not save the folder: " + e.Message); }
        }

        // last folder used, the old per-game exes' game_folder.txt, the exe's own folder and its parents, Steam libraries
        public static string FindFolder(GameSpec g)
        {
            var tries = new List<string>();
            foreach (string f in new[] { StateFile(g), System.IO.Path.Combine(Here, "game_folder.txt") })
                try { if (File.Exists(f)) tries.Add(File.ReadAllText(f, Encoding.UTF8).Trim()); }
                catch (IOException) { }
            string d = Here.TrimEnd('\\');
            for (int i = 0; i < 4 && !string.IsNullOrEmpty(d); i++, d = System.IO.Path.GetDirectoryName(d)) tries.Add(d);
            if (!string.IsNullOrEmpty(g.SteamDir))
                foreach (string lib in SteamLibraries()) tries.Add(System.IO.Path.Combine(lib, "steamapps\\common\\" + g.SteamDir));
            foreach (string t in tries) if (IsGameFolder(g, t)) return t;
            return tries.Count > 0 && Directory.Exists(tries[0]) ? tries[0] : "";
        }

        static List<string> SteamLibraries()
        {
            var libs = new List<string>();
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey("Software\\Valve\\Steam"))
                {
                    string steam = k == null ? null : k.GetValue("SteamPath") as string;
                    if (steam == null) return libs;
                    libs.Add(steam.Replace('/', '\\'));
                    string vdf = System.IO.Path.Combine(steam, "steamapps\\libraryfolders.vdf");
                    if (File.Exists(vdf))
                        foreach (Match x in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                            libs.Add(x.Groups[1].Value.Replace("\\\\", "\\"));
                }
            }
            catch (Exception e) { Log("could not read the Steam libraries: " + e.Message); }
            return libs;
        }

        public static void CheckClosed(GameSpec g)
        {
            foreach (string p in g.Processes)
                if (Process.GetProcessesByName(p).Length > 0)
                    throw new Failure("سكّر اللعبة أول (" + p + ") وبعدين حاول مرة ثانية.", "Close the game first (" + p + "), then try again.");
        }

        // ------------------------------------------------------------ checking

        public static string Sha(string path)
        {
            using (var s = File.OpenRead(path))
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "");
        }

        public static FileState StateOf(GameSpec g, string dir, KitFile f)
        {
            string dst = System.IO.Path.Combine(Root(g, dir), f.Path);
            if (!File.Exists(dst)) return f.Stock == null ? FileState.Stock : FileState.Missing;
            string h = Sha(dst);
            if (h == f.Patched) return FileState.Patched;
            if (f.Stock == null)            // a file the game does not have: ours from an earlier version, or someone else's
                return Directory.Exists(Backup(g, dir)) ? FileState.Older : FileState.Changed;
            if (h == f.Stock) return FileState.Stock;
            string b = System.IO.Path.Combine(Backup(g, dir), f.Path);    // an earlier version of the translation
            return File.Exists(b) && Sha(b) == f.Stock ? FileState.Older : FileState.Changed;
        }

        // where "<name>\0" is in the file (the value follows it), or -1 when it is not there exactly once
        static long FindEntry(byte[] data, KitEntry e)
        {
            byte[] key = Encoding.ASCII.GetBytes(e.Name + "\0");
            long at = -1;
            for (int i = 0; i + key.Length + e.Length <= data.Length; i++)
            {
                if (data[i] != key[0]) continue;
                int j = 1;
                while (j < key.Length && data[i + j] == key[j]) j++;
                if (j < key.Length) continue;
                if (at >= 0) return -1;
                at = i + key.Length;
            }
            return at;
        }

        static FileState EntryState(GameSpec g, string dir, KitEntry e, out long at)
        {
            at = -1;
            string p = System.IO.Path.Combine(Root(g, dir), e.File);
            if (!File.Exists(p)) return FileState.Missing;
            byte[] data = File.ReadAllBytes(p);
            at = FindEntry(data, e);
            if (at < 0) return FileState.Changed;
            string now = BitConverter.ToString(data, (int)at, e.Length).Replace("-", "");
            return now == e.Patched ? FileState.Patched : now == e.Stock ? FileState.Stock : FileState.Changed;
        }

        public static Report Inspect(GameSpec g, string dir, Action<int, int> progress)
        {
            var r = new Report { IsGame = IsGameFolder(g, dir) };
            if (!r.IsGame) return r;
            Load();
            var all = Active(g, dir);
            r.Total = all.Count + entries.Count;
            r.HasBackup = Directory.Exists(Backup(g, dir));
            r.InstalledVersion = ReadMarker(g, dir);
            for (int i = 0; i < r.Total; i++)
            {
                long at;
                FileState s = i < all.Count ? StateOf(g, dir, all[i]) : EntryState(g, dir, entries[i - all.Count], out at);
                switch (s)
                {
                    case FileState.Missing: r.Missing++; break;
                    case FileState.Stock: r.Stock++; break;
                    case FileState.Patched: r.Patched++; break;
                    case FileState.Older: r.Older++; break;
                    default: r.Changed++; break;
                }
                if (r.FirstProblem == "" && (s == FileState.Missing || s == FileState.Changed))
                    r.FirstProblem = i < all.Count ? all[i].Path : entries[i - all.Count].File;
                progress(i + 1, r.Total);
            }
            Log(string.Format("checked {0}: {1} original, {2} translated, {3} older translation, {4} changed, {5} missing",
                              r.Total, r.Stock, r.Patched, r.Older, r.Changed, r.Missing));
            return r;
        }

        static string ReadMarker(GameSpec g, string dir)
        {
            try
            {
                string p = System.IO.Path.Combine(Backup(g, dir), Marker);
                if (!File.Exists(p)) return "";
                var m = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(p, Encoding.UTF8));
                object v;
                return m.TryGetValue("version", out v) ? Convert.ToString(v) : "";
            }
            catch (Exception) { return ""; }
        }

        // ------------------------------------------------------------ install / uninstall

        static void CopyRange(Stream from, Stream to, long off, long n, byte[] buf)
        {
            from.Seek(off, SeekOrigin.Begin);
            while (n > 0)
            {
                int k = from.Read(buf, 0, (int)Math.Min(buf.Length, n));
                if (k <= 0) throw new IOException("short read");
                to.Write(buf, 0, k); n -= k;
            }
        }

        // one LZ4 block (raw block format, as Unity bundles store them) read from `from` at `off`
        static byte[] Lz4Block(Stream from, long off, int csize, int usize)
        {
            var s = new byte[csize];
            from.Seek(off, SeekOrigin.Begin);
            for (int got = 0; got < csize; )
            {
                int k = from.Read(s, got, csize - got);
                if (k <= 0) throw new IOException("short read");
                got += k;
            }
            var d = new byte[usize];
            int i = 0, o = 0;
            while (i < csize)
            {
                int token = s[i++];
                int lit = token >> 4;
                if (lit == 15) { int b; do { b = s[i++]; lit += b; } while (b == 255); }
                Buffer.BlockCopy(s, i, d, o, lit);
                i += lit; o += lit;
                if (i >= csize) break;                          // the last sequence has literals only
                int dist = s[i] | (s[i + 1] << 8);
                i += 2;
                int len = token & 15;
                if (len == 15) { int b; do { b = s[i++]; len += b; } while (b == 255); }
                len += 4;
                if (dist == 0 || dist > o) throw new IOException("bad LZ4 data");
                for (int k = 0; k < len; k++, o++) d[o] = d[o - dist];   // byte by byte: matches may overlap
            }
            if (o != usize) throw new IOException("bad LZ4 block size");
            return d;
        }

        static Failure Mismatch(GameSpec g, string what)
        {
            return new Failure(new Bi("الملف " + what + " ما يطابق النسخة الأصلية. " + g.Mismatch.Ar,
                                      what + " does not match the original file. " + g.Mismatch.En));
        }

        public static Outcome Install(GameSpec g, string dir, Action<int, int> progress)
        {
            CheckClosed(g);
            if (!IsGameFolder(g, dir)) throw new Failure(NotTheFolder(g));
            Load();
            var all = Active(g, dir);
            int steps = (all.Count + entries.Count) * 2, step = 0;
            string root = Root(g, dir), bak = Backup(g, dir);
            string before = ReadMarker(g, dir);
            var todo = new List<KitFile>();
            var original = new List<KitFile>();
            bool older = false;
            foreach (var f in all)
            {
                FileState s = StateOf(g, dir, f);
                progress(++step, steps);
                if (s == FileState.Missing)
                    throw new Failure("الملف " + f.Path + " مو موجود في مجلد اللعبة.", f.Path + " is missing from the game folder.");
                if (s == FileState.Changed) throw Mismatch(g, f.Path);
                if (s == FileState.Older) older = true;
                if (s == FileState.Stock && f.Stock != null) original.Add(f);
                if (s != FileState.Patched) todo.Add(f);
            }
            var entryTodo = new List<KeyValuePair<KitEntry, long>>();
            foreach (var e in entries)
            {
                long at;
                FileState s = EntryState(g, dir, e, out at);
                progress(++step, steps);
                if (s == FileState.Missing || s == FileState.Changed) throw Mismatch(g, e.File);
                if (s == FileState.Stock) entryTodo.Add(new KeyValuePair<KitEntry, long>(e, at));
            }
            int strays = RestoreStrays(g, dir);
            if (todo.Count == 0 && entryTodo.Count == 0 && strays == 0)
            {
                Log("the translation is already installed");
                return Outcome.AlreadyInstalled;
            }
            foreach (var f in original)                 // "Older" files already have a checked original in the backup
            {
                string b = System.IO.Path.Combine(bak, f.Path);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(b));
                if (File.Exists(b)) File.SetAttributes(b, FileAttributes.Normal);
                File.Copy(System.IO.Path.Combine(root, f.Path), b, true);
            }
            Directory.CreateDirectory(bak);
            Log("backed up " + original.Count + " original files -> " + bak);
            WriteFiles(root, bak, todo, () => progress(++step, steps));
            foreach (var pair in entryTodo)
            {
                WriteEntry(System.IO.Path.Combine(root, pair.Key.File), pair.Value, pair.Key.Patched);
                Log("wrote the " + pair.Key.Name + " entry in " + pair.Key.File);
            }
            WriteMarker(g, dir);
            Remember(g, dir);
            progress(steps, steps);
            bool update = older || strays > 0 || (before != "" && before != KitVersion);
            Log(update ? "updated the translation" + (before != "" ? " from version " + before : "") + " to " + KitVersion
                       : "installed the translation " + KitVersion);
            return update ? Outcome.Updated : Outcome.Installed;
        }

        static void WriteFiles(string root, string bak, List<KitFile> todo, Action tick)
        {
            var buf = new byte[1 << 20];
            var zero = new byte[1 << 20];
            string dataPath = System.IO.Path.Combine(PatchDir, "data.bin");
            using (var data = File.Exists(dataPath) ? File.OpenRead(dataPath) : null)
                foreach (var f in todo)
                {
                    string dst = System.IO.Path.Combine(root, f.Path);
                    string tmp = dst + ".arabic-tmp";
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst));
                    if (f.Ops == null) File.Copy(Source(f), tmp, true);
                    else
                        using (var src = File.OpenRead(System.IO.Path.Combine(bak, f.Path)))
                        using (var output = File.Create(tmp))
                        {
                            long blockAt = -1;
                            byte[] block = null;
                            foreach (object[] op in f.Ops)
                            {
                                int kind = Convert.ToInt32(op[0]);
                                long off = Convert.ToInt64(op[1]), n = Convert.ToInt64(op[2]);
                                if (kind == 0) CopyRange(src, output, off, n, buf);
                                else if (kind == 1) CopyRange(data, output, off, n, buf);
                                else if (kind == 3)
                                {
                                    // [3, offset, csize, usize, start, length]: a slice of one LZ4 block of the original
                                    if (blockAt != off)
                                    {
                                        block = Lz4Block(src, off, (int)n, Convert.ToInt32(op[3]));
                                        blockAt = off;
                                    }
                                    output.Write(block, Convert.ToInt32(op[4]), Convert.ToInt32(op[5]));
                                }
                                else while (n > 0) { int k = (int)Math.Min(zero.Length, n); output.Write(zero, 0, k); n -= k; }
                            }
                        }
                    File.SetAttributes(tmp, FileAttributes.Normal);
                    if (Sha(tmp) != f.Patched)
                    {
                        File.Delete(tmp);
                        throw new Failure("فشل بناء الملف " + f.Path + " وما تغيّر. اضغط «أزل التعريب» لإرجاع الملفات الأصلية.",
                                          "Building " + f.Path + " failed; that file was not changed. Press Uninstall to restore the original files.");
                    }
                    if (File.Exists(dst)) { File.SetAttributes(dst, FileAttributes.Normal); File.Delete(dst); }
                    File.Move(tmp, dst);
                    Log("wrote " + f.Path);
                    tick();
                }
        }

        static void WriteEntry(string path, long at, string hex)
        {
            var value = new byte[hex.Length / 2];
            for (int i = 0; i < value.Length; i++) value[i] = Convert.ToByte(hex.Substring(2 * i, 2), 16);
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            using (var s = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                s.Seek(at, SeekOrigin.Begin);
                s.Write(value, 0, value.Length);
            }
        }

        // files an earlier version changed that this version leaves alone: put the original back
        static int RestoreStrays(GameSpec g, string dir)
        {
            string bak = Backup(g, dir);
            if (!Directory.Exists(bak)) return 0;
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Marker };
            foreach (var f in Files()) known.Add(f.Path);
            foreach (var e in entries) known.Add(e.File);       // an old installer's whole-file backup of the exe stays
            int n = 0;
            foreach (string b in Directory.GetFiles(bak, "*", SearchOption.AllDirectories))
            {
                string rel = b.Substring(bak.Length).TrimStart('\\');
                if (known.Contains(rel)) continue;
                string dst = System.IO.Path.Combine(Root(g, dir), rel);
                if (File.Exists(dst)) File.SetAttributes(dst, FileAttributes.Normal);
                File.SetAttributes(b, FileAttributes.Normal);
                File.Copy(b, dst, true);
                File.Delete(b);
                Log("restored " + rel + " (not part of this version)");
                n++;
            }
            return n;
        }

        static void WriteMarker(GameSpec g, string dir)
        {
            var m = new Dictionary<string, object> {
                { "game", g.Id }, { "version", KitVersion }, { "installed", DateTime.Now.ToString("yyyy-MM-dd HH:mm") } };
            File.WriteAllText(System.IO.Path.Combine(Backup(g, dir), Marker), new JavaScriptSerializer().Serialize(m), Encoding.UTF8);
        }

        public static void Uninstall(GameSpec g, string dir, Action<int, int> progress)
        {
            CheckClosed(g);
            if (!IsGameFolder(g, dir)) throw new Failure(NotTheFolder(g));
            Load();
            string bak = Backup(g, dir), root = Root(g, dir);
            if (!Directory.Exists(bak))
                throw new Failure("ما لقيت النسخة الاحتياطية (" + g.BackupDir + ")، فالتعريب غالبًا مو مثبّت هنا.",
                                  "No backup found (" + g.BackupDir + "), so the translation is probably not installed here.");
            var backups = Directory.GetFiles(bak, "*", SearchOption.AllDirectories);
            int n = 0, removed = 0;
            for (int i = 0; i < backups.Length; i++)
            {
                string b = backups[i], rel = b.Substring(bak.Length).TrimStart('\\');
                File.SetAttributes(b, FileAttributes.Normal);    // backups of read-only originals are read-only too
                if (!rel.Equals(Marker, StringComparison.OrdinalIgnoreCase))
                {
                    string dst = System.IO.Path.Combine(root, rel);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst));
                    if (File.Exists(dst)) File.SetAttributes(dst, FileAttributes.Normal);
                    File.Copy(b, dst, true);
                    n++;
                }
                progress(i + 1, backups.Length + 1);
            }
            foreach (var f in Files())                      // files the game does not have: only ours are deleted
            {
                string dst = System.IO.Path.Combine(root, f.Path);
                if (f.Stock != null || !File.Exists(dst)) continue;
                if (Sha(dst) == f.Patched) { File.SetAttributes(dst, FileAttributes.Normal); File.Delete(dst); removed++; }
                else Log("left " + f.Path + " (not the translation's file)");
            }
            foreach (var e in entries)
            {
                long at;
                if (EntryState(g, dir, e, out at) == FileState.Patched)
                {
                    WriteEntry(System.IO.Path.Combine(root, e.File), at, e.Stock);
                    Log("wrote the original " + e.Name + " entry back in " + e.File);
                }
            }
            Directory.Delete(bak, true);
            progress(1, 1);
            Log("restored " + n + " original files, removed " + removed + " added files and " + bak);
        }

        public static Bi NotTheFolder(GameSpec g)
        {
            return new Bi("هذا مو مجلد اللعبة. " + g.FolderHint.Ar, "This is not the game folder. " + g.FolderHint.En);
        }

        public static string BackupPath(GameSpec g, string dir) { return Backup(g, dir); }
    }
}
