// Taareeb Installer: one engine for every Arabic translation released on GitHub (TOOLS/installer/README.md).
// Without arguments it opens the window. Headless (tests): --verify|--install|--uninstall <folder>,
// --prepare <gamePkg> <updatePkg> <pkgToolDir> <outDir> (PS4 games), --info; optional --log <file>.
// --page before|prepare|install|installed|updated|removed opens the window on that step (for screenshots).
// --selftest-ui builds the window without showing it and visits every page in several orders (exit 0 = no error).
// An unexpected error in the window is written to %LOCALAPPDATA%\Taareeb\<id>\crash.log and shown in the window;
// the program stays open.
// The log goes to <exe name>.log next to the exe. Exit codes: 0 ok, 1 error, 2 usage,
// 3 (--verify) the translation is not fully installed.
using System;
using System.IO;
using System.Text;
using System.Windows;

namespace Taareeb.Installer
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool preview = args.Length == 2 && args[0] == "--page";     // screenshots: open the window on one step
            if (args.Length == 1 && args[0] == "--selftest-ui") return SelfTestUi();
            if (args.Length > 0 && !preview) return Headless(args);
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            GameSpec g;
            try { g = GameSpec.Load(); }
            catch (Exception e)
            {
                MessageBox.Show("game.json: " + e.Message, "Taareeb Installer", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
            var w = new MainWindow(g, preview ? args[1] : "");
            app.DispatcherUnhandledException += (s, e) =>
            {
                e.Handled = true;
                string where = CrashLog(g, e.Exception);
                Kit.Log("ERROR " + e.Exception);
                w.ShowCrash(where);
            };
            app.Run(w);
            return 0;
        }

        static string CrashLog(GameSpec g, Exception e)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Taareeb\\" + g.Id);
                Directory.CreateDirectory(dir);
                string f = Path.Combine(dir, "crash.log");
                File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + g.Id + " " + g.Version + "\r\n" + e + "\r\n\r\n", Encoding.UTF8);
                return f;
            }
            catch (Exception) { return ""; }
        }

        static int SelfTestUi()
        {
            try
            {
                new Application();
                var w = new MainWindow(GameSpec.Load(), "");
                w.SelfTest();
                return 0;
            }
            catch (Exception e)
            {
                File.WriteAllText(Path.Combine(Kit.Here, "selftest-ui.log"), e.ToString(), Encoding.UTF8);
                return 1;
            }
        }

        static int Headless(string[] a)
        {
            var log = new StringBuilder();
            Kit.Log = s => { lock (log) log.AppendLine(s); };
            string logFile = null;
            int at = Array.IndexOf(a, "--log");
            if (at >= 0 && at + 1 < a.Length)
            {
                logFile = a[at + 1];
                var rest = new string[a.Length - 2];
                Array.Copy(a, 0, rest, 0, at);
                Array.Copy(a, at + 2, rest, at, a.Length - at - 2);
                a = rest;
            }
            int rc = 0;
            GameSpec g = null;
            try
            {
                g = GameSpec.Load();
                Action<int, int> quiet = (i, n) => { };
                string cmd = a.Length > 0 ? a[0] : "";
                if (cmd == "--info" && a.Length == 1)
                    Kit.Log(string.Format("{0} | {1} | translation {2} | kit {3}", g.Id, g.Name.En, g.Version, Kit.KitVersion));
                else if (cmd == "--verify" && a.Length == 2)
                {
                    var r = Kit.Inspect(g, a[1], quiet);
                    Kit.Log(!r.IsGame ? "NOT THE GAME FOLDER" : r.Installed ? "INSTALLED" : r.Original ? "ORIGINAL" : "MIXED");
                    if (!r.Installed) rc = 3;
                }
                else if (cmd == "--install" && a.Length == 2) Kit.Log("OUTCOME " + Kit.Install(g, a[1], quiet));
                else if (cmd == "--uninstall" && a.Length == 2) Kit.Uninstall(g, a[1], quiet);
                else if (cmd == "--prepare" && a.Length == 5 && g.Ps4Pkg != null)
                    Kit.Log("GAME " + Ps4Pkg.Prepare(g.Ps4Pkg, a[1], a[2], a[3], a[4], s => { }));
                else
                {
                    Kit.Log("usage: --info | --verify <folder> | --install <folder> | --uninstall <folder>"
                            + " | --prepare <gamePkg> <updatePkg> <pkgToolDir> <outDir>   [--log <file>]");
                    rc = 2;
                }
            }
            catch (Exception e) { Kit.Log("ERROR " + e.Message); rc = 1; }
            if (logFile == null)
                logFile = Path.Combine(Kit.Here, (g != null ? g.ExeName : "TaareebInstaller") + ".log");
            File.WriteAllText(logFile, log.ToString(), Encoding.UTF8);
            return rc;
        }
    }
}
