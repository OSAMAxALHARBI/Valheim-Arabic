// Optional step for PS4 ports (game.json "ps4_pkg"): make the game folder from the player's own game PKG and update PKG.
// The encrypted file system is unpacked by LibOrbisPkg's PkgTool.Core (the player downloads it; it needs the .NET
// Runtime); the clear sce_sys entries (param.sfo, icons, trophies) are read here. First used for inFAMOUS Second Son.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Taareeb.Installer
{
    static class Ps4Pkg
    {
        static readonly Dictionary<uint, string> EntryFiles = BuildEntryFiles();
        static Dictionary<uint, string> BuildEntryFiles()
        {
            var d = new Dictionary<uint, string> {
                {0x1000, "param.sfo"}, {0x1001, "playgo-chunk.dat"}, {0x1002, "playgo-chunk.sha"},
                {0x1003, "playgo-manifest.xml"}, {0x1004, "pronunciation.xml"}, {0x1005, "pronunciation.sig"},
                {0x1006, "pic1.png"}, {0x1007, "pubtoolinfo.dat"}, {0x1008, "app/playgo-chunk.dat"},
                {0x1009, "app/playgo-chunk.sha"}, {0x100A, "app/playgo-manifest.xml"}, {0x100B, "shareparam.json"},
                {0x0408, "origin-deltainfo.dat"}, {0x1200, "icon0.png"}, {0x1220, "pic0.png"}, {0x1240, "snd0.at9"},
                {0x1260, "changeinfo/changeinfo.xml"}, {0x1400, "trophy/trophy00.trp"} };
            for (uint i = 0; i < 30; i++) d[0x1261 + i] = string.Format("changeinfo/changeinfo_{0:D2}.xml", i);
            return d;
        }

        static uint BE32(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }

        static void ReadFull(Stream s, byte[] buf, int n)
        {
            int got = 0;
            while (got < n) { int k = s.Read(buf, got, n - got); if (k <= 0) throw new IOException("short read"); got += k; }
        }

        // the sce_sys files a PKG keeps in the clear (PkgTool's pkg_extract writes only the encrypted file system)
        public static void ExportSceSys(string pkg, string dest)
        {
            using (var fs = File.OpenRead(pkg))
            {
                var head = new byte[32];
                ReadFull(fs, head, 32);
                if (head[0] != 0x7F || head[1] != (byte)'C' || head[2] != (byte)'N' || head[3] != (byte)'T')
                    throw new Failure("الملف مو PKG حق PS4: " + pkg, "Not a PS4 package: " + pkg);
                uint count = BE32(head, 0x10), table = BE32(head, 0x18);
                var raw = new byte[count * 32];
                fs.Position = table;
                ReadFull(fs, raw, raw.Length);
                for (int i = 0; i < count; i++)
                {
                    uint id = BE32(raw, i * 32), flags = BE32(raw, i * 32 + 8);
                    uint off = BE32(raw, i * 32 + 16), size = BE32(raw, i * 32 + 20);
                    string name;
                    if (!EntryFiles.TryGetValue(id, out name) || (flags & 0x80000000u) != 0) continue;
                    string path = Path.Combine(dest, name.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var buf = new byte[size];
                    fs.Position = off;
                    ReadFull(fs, buf, (int)size);
                    File.WriteAllBytes(path, buf);
                }
            }
        }

        public static bool HasDotnet()
        {
            try { return RunDotnet("--list-runtimes", false) == 0; }
            catch (Exception) { return false; }
        }

        static int RunDotnet(string args, bool log)
        {
            var psi = new ProcessStartInfo("dotnet", args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            psi.EnvironmentVariables["DOTNET_ROLL_FORWARD"] = "Major";   // PkgTool.Core 0.2 targets .NET Core 3.0
            using (var p = Process.Start(psi))
            {
                p.OutputDataReceived += (s, e) => { if (log && !string.IsNullOrEmpty(e.Data)) Kit.Log("   " + e.Data); };
                p.ErrorDataReceived += (s, e) => { if (log && !string.IsNullOrEmpty(e.Data)) Kit.Log("   " + e.Data); };
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        static void Extract(string tool, string pkg, string outDir)
        {
            string args = string.Format("\"{0}\" pkg_extract \"{1}\" \"{2}\"", Path.Combine(tool, "PkgTool.Core.dll"), pkg, outDir);
            if (RunDotnet(args, true) != 0 || !Directory.Exists(Path.Combine(outDir, "uroot")))
                throw new Failure("PkgTool ما قدر يفك الملف: " + pkg, "PkgTool could not extract: " + pkg);
        }

        static void MoveTreeOver(string from, string to)
        {
            foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string dst = Path.Combine(to, f.Substring(from.Length).TrimStart('\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                if (File.Exists(dst)) { File.SetAttributes(dst, FileAttributes.Normal); File.Delete(dst); }
                File.Move(f, dst);
            }
        }

        // stage: 1 = unpacking the game, 2 = unpacking the update, 3 = merging, 4 = sce_sys, 5 = done
        public static string Prepare(Ps4PkgSpec spec, string gamePkg, string updatePkg, string tool, string outDir, Action<int> stage)
        {
            if (!File.Exists(Path.Combine(tool, "PkgTool.Core.dll")))
                throw new Failure("ما لقيت PkgTool.Core.dll في مجلد PkgTool.Core.", "PkgTool.Core.dll is not in the PkgTool.Core folder.");
            if (!HasDotnet())
                throw new Failure("ثبّت .NET Runtime (الإصدار 6 أو أحدث) من موقع مايكروسوفت ثم حاول مرة ثانية.",
                                  "Install the .NET Runtime (version 6 or newer) from Microsoft, then try again.");
            string game = Path.Combine(outDir, spec.FolderName);
            if (Directory.Exists(game))
                throw new Failure("المجلد موجود من قبل: " + game, "The folder already exists: " + game);
            string tmp = Path.Combine(outDir, "taareeb-pkg-tmp");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            stage(1); Kit.Log("extracting the game PKG (this takes a while)...");
            Extract(tool, gamePkg, Path.Combine(tmp, "base"));
            stage(2); Kit.Log("extracting the update PKG...");
            Extract(tool, updatePkg, Path.Combine(tmp, "update"));
            stage(3); Kit.Log("merging the update over the game...");
            Directory.Move(Path.Combine(tmp, "base", "uroot"), game);
            MoveTreeOver(Path.Combine(tmp, "update", "uroot"), game);
            stage(4); Kit.Log("writing sce_sys...");
            ExportSceSys(gamePkg, Path.Combine(game, "sce_sys"));
            ExportSceSys(updatePkg, Path.Combine(game, "sce_sys"));    // the update's param.sfo wins
            Directory.Delete(tmp, true);
            if (!File.Exists(Path.Combine(game, spec.CheckFile)))
                throw new Failure("ما طلع ملف " + spec.CheckFile + ". تأكد من ملفات الـ PKG.", spec.CheckFile + " is missing. Check your PKG files.");
            stage(5); Kit.Log("game folder ready: " + game);
            return game;
        }
    }
}
