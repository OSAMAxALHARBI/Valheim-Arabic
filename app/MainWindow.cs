// The installer window: cover header, steps on the right, the current step's page, footer with links and updates.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Taareeb.Installer
{
    enum Page { Before, Prepare, Install, Done }

    sealed class MainWindow : Window
    {
        readonly GameSpec g;
        readonly List<Page> steps = new List<Page>();
        Page current;
        bool busy;
        string doneKind = "";                   // installed | updated | removed

        readonly StackPanel rail = new StackPanel();
        readonly ContentControl host = new ContentControl();
        readonly ContentControl actionsHost = new ContentControl();
        readonly ScrollViewer scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        readonly Border banner = new Border { Visibility = Visibility.Collapsed };
        readonly StackPanel work = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        readonly ProgressBar bar = new ProgressBar();
        readonly ContentControl workText = new ContentControl();
        readonly TextBox log = new TextBox();
        readonly List<Button> locked = new List<Button>();

        // install page
        readonly TextBox folder = new TextBox();
        readonly Border stateCard = new Border();
        Button installBtn, uninstallBtn, backupBtn, verifyBtn;
        readonly DispatcherTimer debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        int inspectSeq;
        Report report;

        // PS4 page
        readonly TextBox gamePkg = new TextBox(), updatePkg = new TextBox(), toolDir = new TextBox(), outDir = new TextBox();

        static readonly Bi[] StepNames = {
            new Bi("قبل ما تبدأ", "Before you start"), new Bi("جهّز مجلد اللعبة", "Make the game folder"),
            new Bi("ثبّت التعريب", "Install the translation"), new Bi("جاهز للعب", "Ready to play") };

        readonly string startPage;

        public MainWindow(GameSpec spec, string page)
        {
            g = spec;
            startPage = page;
            Ui.LoadTheme();
            Resources.MergedDictionaries.Add(Ui.Theme);
            FontFamily = Ui.LoadFont();
            FontSize = 14;
            Foreground = Ui.B("Text");
            Background = Ui.B("Bg");
            Title = g.Name.En + " · Arabic translation";
            Width = 1040; Height = 800; MinWidth = 900; MinHeight = 660;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            // digits stay as written: with an Arabic Windows locale WPF would show "1.1.8" as "١,١,٨"
            NumberSubstitution.SetSubstitution(this, NumberSubstitutionMethod.European);
            try { using (var s = Ui.Res("icon.ico")) Icon = BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); }
            catch (Exception) { }

            steps.Add(Page.Before);
            if (g.Ps4Pkg != null) steps.Add(Page.Prepare);
            steps.Add(Page.Install);
            steps.Add(Page.Done);

            var root = new Grid { FlowDirection = FlowDirection.RightToLeft };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var body = Body(); Grid.SetRow(body, 1);
            var foot = Footer(); Grid.SetRow(foot, 2);
            root.Children.Add(Header()); root.Children.Add(body); root.Children.Add(foot);
            Content = root;

            Kit.Log = s => Dispatcher.BeginInvoke((Action)(() => log.AppendText(DateTime.Now.ToString("HH:mm:ss ") + s + "\r\n")));
            folder.TextChanged += (s, e) => { debounce.Stop(); debounce.Start(); };
            debounce.Tick += (s, e) => { debounce.Stop(); Inspect(); };
            SourceInitialized += (s, e) => DarkTitleBar();
            Closing += (s, e) =>
            {
                if (!busy) return;
                e.Cancel = true;
                Banner("Gold", new Bi("انتظر لين يخلص الشغل، لا تسكّر البرنامج الحين.", "Wait until this finishes; do not close the program now."));
            };
            Loaded += (s, e) => Start();
        }

        // --selftest-ui: every page in order, backwards and jumping around, three times (the 2026-10-23 crash came on a
        // second visit to a page)
        public void SelfTest()
        {
            doneKind = "installed";
            var order = new List<Page>(steps);
            for (int i = steps.Count - 1; i >= 0; i--) order.Add(steps[i]);
            for (int i = 0; i < steps.Count; i++) { order.Add(steps[i]); order.Add(steps[steps.Count - 1 - i]); }
            for (int round = 0; round < 3; round++)
                foreach (var p in order) GoTo(p);
        }

        public void ShowCrash(string logFile)
        {
            busy = false;
            work.Visibility = Visibility.Collapsed;
            foreach (var b in locked) b.IsEnabled = true;
            Banner("Danger", new Bi("صار خطأ في البرنامج. انسخ السجل وأرسله لي" + (logFile != "" ? " (أو الملف " + logFile + ")" : "") + ".",
                                    "Something went wrong in the program. Copy the log and send it to me" + (logFile != "" ? " (or the file " + logFile + ")" : "") + "."));
        }

        void Start()
        {
            Kit.Log(string.Format("Taareeb Installer {0} | {1} | translation {2}", BuildInfo.Engine, g.Name.En, g.Version));
            try
            {
                Kit.Log("kit: " + Kit.Files().Count + " files, version " + Kit.KitVersion);
                if (Kit.KitVersion != "" && Kit.KitVersion != g.Version) Kit.Log("warning: kit version differs from the program version");
            }
            catch (Failure f) { Banner("Danger", f.Text); }
            folder.Text = Kit.FindFolder(g);
            if (startPage == "prepare" && g.Ps4Pkg != null) GoTo(Page.Prepare);
            else if (startPage == "install") GoTo(Page.Install);
            else if (startPage == "installed" || startPage == "updated" || startPage == "removed") { doneKind = startPage; GoTo(Page.Done); }
            else GoTo(steps[0]);
        }

        // ------------------------------------------------------------ frame

        UIElement Header()
        {
            var h = new Grid { Height = 190, ClipToBounds = true, FlowDirection = FlowDirection.LeftToRight };
            var cover = new Image { Source = Ui.Bitmap("cover.jpg"), Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(cover, BitmapScalingMode.HighQuality);
            h.Children.Add(cover);
            h.Children.Add(new Rectangle { Fill = Ui.Fade(new Point(1, 0.5), new Point(0, 0.5), 0, 0.94, 0.42, 0.72, 1, 0.12) });
            h.Children.Add(new Rectangle { Fill = Ui.Fade(new Point(0.5, 1), new Point(0.5, 0), 0, 1, 0.38, 0.45, 1, 0) });

            var logo = new Image
            {
                Source = Ui.Bitmap("logo.png"), Height = 28, Margin = new Thickness(0, 22, 36, 0), Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, ToolTip = "iosamai.com",
            };
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
            logo.MouseLeftButtonUp += (s, e) => Open(g.SiteUrl);
            h.Children.Add(logo);

            var title = new StackPanel
            {
                FlowDirection = FlowDirection.RightToLeft, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 36, 22),
            };
            title.Children.Add(new Border
            {
                // solid tab like the website's «تعريب» tab; the 1-px outline pill clipped the tall Arabic letters
                Background = Ui.B("Accent"), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 4, 14, 6),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10),
                Child = Ui.T("تعريب عربي" + (g.Platform.Ar != "" ? " · " + g.Platform.Ar : ""), 13, "AccentInk", true, true),
            });
            var name = Ui.T(g.Name.Ar, 34, "Text", true, true);
            name.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.7, Color = Colors.Black };
            TextOptions.SetTextFormattingMode(name, TextFormattingMode.Ideal);
            title.Children.Add(name);
            var en = Ui.T(g.Name.En + (g.Platform.En != "" ? "  ·  " + g.Platform.En : ""), 14, "Muted", false);
            title.Children.Add(en);
            h.Children.Add(title);
            return h;
        }

        UIElement Body()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            var side = new StackPanel();
            side.Children.Add(new TextBlock { Text = "الخطوات", Foreground = Ui.B("Muted"), FontSize = 12, Margin = new Thickness(14, 0, 0, 10) });
            side.Children.Add(rail);
            grid.Children.Add(new Border
            {
                Background = Ui.B("Bg2"), BorderBrush = Ui.B("Line"), BorderThickness = new Thickness(0, 0, 1, 0),
                Padding = new Thickness(14, 20, 14, 14), Child = side,
            });

            var main = new Grid { Margin = new Thickness(34, 22, 34, 12) };
            main.RowDefinitions.Add(new RowDefinition());
            for (int i = 0; i < 4; i++) main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            scroller.Content = host;
            main.Children.Add(scroller);

            Grid.SetRow(actionsHost, 1);                      // the page's buttons stay in view, under the scrolling part
            main.Children.Add(actionsHost);

            Grid.SetRow(banner, 2);
            banner.Margin = new Thickness(0, 10, 0, 0);
            main.Children.Add(banner);

            bar.Margin = new Thickness(0, 0, 0, 6);
            work.Children.Add(bar);
            work.Children.Add(workText);
            Grid.SetRow(work, 3);
            main.Children.Add(work);

            SetUpLog();
            Grid.SetRow(log, 4);
            main.Children.Add(log);

            Grid.SetColumn(main, 1);
            grid.Children.Add(main);
            return grid;
        }

        void SetUpLog()
        {
            log.IsReadOnly = true;
            log.FlowDirection = FlowDirection.LeftToRight;
            log.FontFamily = new FontFamily("Consolas");
            log.FontSize = 12;
            log.Height = 130;
            log.TextWrapping = TextWrapping.Wrap;
            log.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            log.Visibility = Visibility.Collapsed;
            log.Margin = new Thickness(0, 10, 0, 0);
        }

        UIElement Footer()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var start = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var by = Ui.Two(new Bi("ترجمة وتعريب: أسامه", "Translated by Osama (iOsamai)"), 13, 11);
            by.VerticalAlignment = VerticalAlignment.Center;
            by.Margin = new Thickness(0, 0, 0, 0);
            start.Children.Add(by);
            start.Children.Add(Link("iosamai.com", g.SiteUrl));
            start.Children.Add(Link("GitHub", g.RepoUrl));
            var toggle = Small(new Bi("السجل", "Log"), () =>
                log.Visibility = log.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible);
            toggle.Margin = new Thickness(18, 0, 0, 0);
            start.Children.Add(toggle);
            start.Children.Add(Small(new Bi("نسخ السجل", "Copy log"), () =>
            {
                try { Clipboard.SetText(log.Text); Banner("Accent", new Bi("نسخت السجل.", "The log is copied.")); }
                catch (Exception) { }
            }));
            grid.Children.Add(start);

            var end = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var ver = Ui.Two(new Bi("الإصدار " + g.Version, "Version " + g.Version), 13, 11);
            ver.VerticalAlignment = VerticalAlignment.Center;
            ver.Margin = new Thickness(0, 0, 14, 0);
            end.Children.Add(ver);
            var check = Small(new Bi("تحقق من التحديثات", "Check for updates"), CheckUpdates);
            check.Margin = new Thickness(0);
            end.Children.Add(check);
            Grid.SetColumn(end, 1);
            grid.Children.Add(end);

            return new Border
            {
                Background = Ui.B("Bg2"), BorderBrush = Ui.B("Line"), BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(26, 10, 26, 10), Child = grid,
            };
        }

        Button Link(string text, string url)
        {
            var b = new Button { Style = Ui.S("Ghost"), Margin = new Thickness(18, 0, 0, 0), FlowDirection = FlowDirection.LeftToRight, ToolTip = url };
            b.Content = new TextBlock { Text = text, FontSize = 13 };
            b.Click += (s, e) => Open(url);
            return b;
        }

        Button Small(Bi label, Action click)
        {
            var b = Ui.Btn(label, "Ghost", click, 13, 10.5);
            b.Margin = new Thickness(0, 0, 6, 0);
            return b;
        }

        // ------------------------------------------------------------ steps

        void GoTo(Page p)
        {
            current = p;
            locked.Clear();
            actionsHost.Content = null;
            switch (p)
            {
                case Page.Before: host.Content = BeforePage(); break;
                case Page.Prepare: host.Content = PreparePage(); break;
                case Page.Install: host.Content = InstallPage(); Inspect(); break;
                default: host.Content = DonePage(); break;
            }
            scroller.ScrollToTop();
            DrawRail();
        }

        void DrawRail()
        {
            rail.Children.Clear();
            int at = steps.IndexOf(current);
            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                bool done = i < at || (step == Page.Done && current == Page.Done);
                bool now = i == at && !done;
                var circle = new Border
                {
                    Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
                    Background = done ? Ui.B("Accent") : now ? Ui.B("AccentSoft") : Brushes.Transparent,
                    BorderBrush = done ? Ui.B("Accent") : now ? Ui.B("Accent2") : Ui.B("Line"),
                    BorderThickness = new Thickness(now ? 2 : 1.5),
                };
                if (done) circle.Child = Ui.Check(14, "AccentInk", 2.4);
                else circle.Child = new TextBlock
                {
                    Text = Ui.Digits(i + 1), FontSize = 15, FontWeight = FontWeights.SemiBold,
                    Foreground = Ui.B(now ? "Accent2" : "Muted"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.Children.Add(circle);
                var text = Ui.Two(StepNames[(int)step], 14.5, 11.5, now || done ? "Text" : "Muted", "Muted", now);
                text.Margin = new Thickness(12, 0, 0, 0);
                text.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(text, 1);
                grid.Children.Add(text);
                var item = new Button
                {
                    Style = Ui.S("Step"), Content = grid, Background = now ? Ui.B("Surface") : Brushes.Transparent,
                    IsEnabled = !busy && (step != Page.Done || doneKind != ""),
                };
                var target = step;
                item.Click += (s, e) => { HideBanner(); GoTo(target); };
                rail.Children.Add(item);
                if (i + 1 < steps.Count)
                    rail.Children.Add(new Rectangle
                    {
                        Width = 2, Height = 14, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(28, 0, 0, 0),
                        Fill = i < at ? Ui.B("Accent") : Ui.B("Line"),
                    });
            }
        }

        void Next()
        {
            int i = steps.IndexOf(current);
            HideBanner();
            if (i + 1 < steps.Count && steps[i + 1] != Page.Done) GoTo(steps[i + 1]);
        }

        // ------------------------------------------------------------ pages

        StackPanel PageStart(Bi heading)
        {
            var p = new StackPanel();
            var h = Ui.Two(heading, 24, 13, "Text", "Muted", true);
            h.Margin = new Thickness(0, 0, 0, 16);
            p.Children.Add(h);
            return p;
        }

        static UIElement Sub(Bi b)
        {
            var s = Ui.Two(b, 15, 11.5, "Accent2", "Muted", true);
            s.Margin = new Thickness(0, 14, 0, 8);
            return s;
        }

        static UIElement Para(Bi b)
        {
            var s = Ui.Two(b, 14, 12);
            s.Margin = new Thickness(0, 0, 0, 10);
            return s;
        }

        // the page's buttons go under the scrolling part; returns an empty spacer for the page itself
        UIElement Actions(params Button[] buttons)
        {
            var w = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            foreach (var b in buttons) { w.Children.Add(b); locked.Add(b); }
            actionsHost.Content = w;
            return new Border { Height = 4 };
        }

        WrapPanel LinkButtons(List<LinkSpec> links)
        {
            var w = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var l in links)
            {
                var url = l.Url;
                var b = Ui.Btn(l.Text, "Btn", () => Open(url), 13, 10.5);
                b.ToolTip = url;
                w.Children.Add(b);
            }
            return w;
        }

        UIElement BeforePage()
        {
            var p = PageStart(StepNames[0]);
            if (g.Before.Ar != "" || g.Before.En != "") p.Children.Add(Para(g.Before));
            if (g.Requirements.Count > 0)
            {
                p.Children.Add(Sub(new Bi("تحتاج", "You need")));
                foreach (var r in g.Requirements) p.Children.Add(Ui.Item(Ui.Dot("Accent"), Ui.Two(r, 14, 12)));
            }
            if (g.Links.Count > 0)
            {
                p.Children.Add(Sub(new Bi("روابط", "Links")));
                p.Children.Add(LinkButtons(g.Links));
            }
            p.Children.Add(Actions(Ui.Btn(new Bi("التالي", "Next"), "Primary", Next)));
            return p;
        }

        UIElement PreparePage()
        {
            var k = g.Ps4Pkg;
            var p = PageStart(StepNames[1]);
            if (k.Note.Ar != "" || k.Note.En != "") p.Children.Add(Para(k.Note));
            if (k.Links.Count > 0) p.Children.Add(LinkButtons(k.Links));
            p.Children.Add(Picker(k.GameLabel, gamePkg, true));
            p.Children.Add(Picker(k.UpdateLabel, updatePkg, true));
            p.Children.Add(Picker(new Bi("مجلد أداة PkgTool.Core", "PkgTool.Core folder"), toolDir, false));
            p.Children.Add(Picker(new Bi("وين تبي مجلد اللعبة (" + k.SizeNote.Ar + ")", "Where to make the game folder (" + k.SizeNote.En + ")"), outDir, false));
            p.Children.Add(Actions(
                Ui.Btn(new Bi("جهّز مجلد اللعبة", "Make the game folder"), "Primary", OnPrepare),
                Ui.Btn(new Bi("مجلد اللعبة عندي جاهز", "My game folder is ready"), "Ghost", Next)));
            return p;
        }

        UIElement InstallPage()
        {
            var p = PageStart(StepNames[2]);
            p.Children.Add(Picker(g.FolderLabel, folder, false));
            stateCard.Margin = new Thickness(0, 4, 0, 0);
            p.Children.Add(Detach(stateCard));
            installBtn = Ui.Btn(new Bi("ثبّت التعريب", "Install"), "Primary", OnInstall);
            verifyBtn = Ui.Btn(new Bi("افحص الملفات", "Check the files"), "Btn", () => Inspect());
            uninstallBtn = Ui.Btn(new Bi("أزل التعريب", "Uninstall"), "Btn", OnUninstall);
            backupBtn = Ui.Btn(new Bi("مجلد النسخة الاحتياطية", "Backup folder"), "Ghost",
                               () => Open(Kit.BackupPath(g, folder.Text.Trim())));
            p.Children.Add(Actions(installBtn, verifyBtn, uninstallBtn, backupBtn));
            return p;
        }

        UIElement DonePage()
        {
            var p = new StackPanel();
            var circle = new Border
            {
                Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = Ui.B("Accent"),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 14),
                Child = Ui.Check(26, "AccentInk", 3.4),
                Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.55, Color = Ui.C("#2FA69B", 255) },
            };
            p.Children.Add(circle);
            Bi head = doneKind == "removed" ? new Bi("تمت إزالة التعريب", "The translation was removed")
                    : doneKind == "updated" ? new Bi("تم تحديث التعريب إلى الإصدار " + g.Version, "The translation was updated to version " + g.Version)
                    : new Bi("تم تثبيت التعريب", "The Arabic translation is installed");
            var h = Ui.Two(head, 24, 13, "Text", "Muted", true);
            h.Margin = new Thickness(0, 0, 0, 12);
            p.Children.Add(h);
            if (doneKind == "removed")
                p.Children.Add(Para(new Bi("رجعت ملفات اللعبة الأصلية.", "The original game files are back.")));
            else if (g.After.Count > 0)
            {
                p.Children.Add(Sub(new Bi("وش تسوي بعدها", "What to do next")));
                for (int i = 0; i < g.After.Count; i++)
                {
                    p.Children.Add(Ui.Item(Ui.Badge(i + 1), Ui.Two(g.After[i], 14, 12)));
                }
            }
            string dir = folder.Text.Trim();
            p.Children.Add(Actions(
                Ui.Btn(new Bi("افتح مجلد اللعبة", "Open the game folder"), "Btn", () => Open(dir)),
                Ui.Btn(new Bi("صفحة التعريب", "Translation page"), "Btn", () => Open(g.RepoUrl)),
                Ui.Btn(new Bi("إغلاق", "Close"), "Primary", Close)));
            return p;
        }

        // pages are rebuilt on every visit, but some controls live for the whole window (the folder box keeps its text,
        // the state card its result): take one off the old page before it goes on the new one. Without this a second
        // visit to a page threw "Specified element is already the logical child of another element" (2026-10-23).
        static T Detach<T>(T e) where T : FrameworkElement
        {
            var panel = e.Parent as Panel;
            if (panel != null) panel.Children.Remove(e);
            return e;
        }

        UIElement Picker(Bi label, TextBox box, bool file)
        {
            var p = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var l = Ui.Two(label, 13.5, 11.5, "Text", "Muted");
            l.Margin = new Thickness(0, 0, 0, 6);
            p.Children.Add(l);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            box.FlowDirection = FlowDirection.LeftToRight;
            box.VerticalContentAlignment = VerticalAlignment.Center;
            box.FontSize = 13;
            row.Children.Add(Detach(box));
            var browse = Ui.Btn(new Bi("اختيار", "Browse"), "Btn", () =>
            {
                string picked = file ? PickFile(box.Text) : FolderPicker.Pick(new WindowInteropHelper(this).Handle,
                                                                               label.En, box.Text.Trim());
                if (picked != null) box.Text = picked;
            }, 13, 10.5);
            browse.Margin = new Thickness(10, 0, 0, 0);
            locked.Add(browse);
            Grid.SetColumn(browse, 1);
            row.Children.Add(browse);
            p.Children.Add(row);
            return p;
        }

        string PickFile(string start)
        {
            var d = new Microsoft.Win32.OpenFileDialog { Filter = "PS4 PKG (*.pkg)|*.pkg|All files (*.*)|*.*" };
            try { if (File.Exists(start)) d.InitialDirectory = System.IO.Path.GetDirectoryName(start); }
            catch (ArgumentException) { }
            return d.ShowDialog(this) == true ? d.FileName : null;
        }

        // ------------------------------------------------------------ install page state

        void Inspect()
        {
            if (current != Page.Install || busy) return;
            string dir = folder.Text.Trim();
            int seq = ++inspectSeq;
            report = null;
            UpdateButtons();
            if (dir == "") { State("Muted", new Bi("اختر مجلد اللعبة.", "Choose the game folder."), null); return; }
            if (!Kit.IsGameFolder(g, dir)) { State("Danger", Kit.NotTheFolder(g), null); return; }
            State("Muted", new Bi("أفحص الملفات...", "Checking the files..."), null);
            new Thread(() =>
            {
                Report r = null;
                Failure fail = null;
                try { r = Kit.Inspect(g, dir, (i, n) => { }); }
                catch (Failure f) { fail = f; }
                catch (Exception e) { fail = new Failure("ما قدرت أفحص الملفات: " + e.Message, "Could not check the files: " + e.Message); }
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (seq != inspectSeq || current != Page.Install) return;
                    if (fail != null) { State("Danger", fail.Text, null); return; }
                    report = r;
                    ShowReport(r);
                    UpdateButtons();
                }));
            }) { IsBackground = true }.Start();
        }

        void ShowReport(Report r)
        {
            string counts = string.Format("{0} files: {1} original, {2} translated", r.Total, r.Stock, r.Patched)
                          + (r.Older > 0 ? ", " + r.Older + " older translation" : "")
                          + (r.Changed > 0 ? ", " + r.Changed + " changed" : "") + (r.Missing > 0 ? ", " + r.Missing + " missing" : "");
            if (r.Changed > 0 || r.Missing > 0)
                State("Danger", new Bi("فيه ملفات ما تطابق النسخة الأصلية (أولها " + r.FirstProblem + "). " + g.Mismatch.Ar,
                                       "Some files do not match the original game (first: " + r.FirstProblem + "). " + g.Mismatch.En), counts);
            else if (r.Installed)
                State("Accent", new Bi("التعريب مثبّت (الإصدار " + g.Version + ").", "The translation is installed (version " + g.Version + ")."), counts);
            else if (r.Older > 0)
                State("Gold", new Bi("فيه نسخة أقدم من التعريب" + (r.InstalledVersion != "" ? " (" + r.InstalledVersion + ")" : "") + "، وبتتحدّث للإصدار " + g.Version + ".",
                                     "An older version of the translation is installed; it will be updated to " + g.Version + "."), counts);
            else if (r.Patched > 0)
                State("Gold", new Bi("التعريب مثبّت جزئيًا. اضغط «ثبّت التعريب» عشان يكمل.", "The translation is partly installed. Press Install to finish it."), counts);
            else
                State("Accent2", new Bi("ملفات اللعبة أصلية وجاهزة للتعريب.", "Original game files, ready for the translation."), counts);
        }

        void State(string brush, Bi text, string detail)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var dot = new Ellipse { Width = 10, Height = 10, Fill = Ui.B(brush), Margin = new Thickness(0, 6, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            grid.Children.Add(dot);
            var body = Ui.Two(text, 14, 12);
            if (detail != null)
            {
                var d = Ui.T(detail, 11, "Muted", false);
                d.Margin = new Thickness(0, 4, 0, 0);
                d.Opacity = 0.8;
                body.Children.Add(d);
            }
            Grid.SetColumn(body, 1);
            grid.Children.Add(body);
            stateCard.Child = Ui.Card(grid, new Thickness(0));
        }

        void UpdateButtons()
        {
            if (installBtn == null) return;
            var r = report;
            installBtn.IsEnabled = !busy && r != null && r.CanInstall;
            uninstallBtn.IsEnabled = !busy && r != null && r.HasBackup;
            backupBtn.IsEnabled = !busy && r != null && r.HasBackup;
            verifyBtn.IsEnabled = !busy;
            var label = r != null && r.Older > 0 ? new Bi("حدّث التعريب", "Update") : new Bi("ثبّت التعريب", "Install");
            installBtn.Content = Ui.BtnText(label, "Primary");
        }

        // ------------------------------------------------------------ work

        void Run(Bi what, Action<Action<int, int>> job, Action success)
        {
            busy = true;
            HideBanner();
            foreach (var b in locked) b.IsEnabled = false;
            folder.IsReadOnly = true;
            DrawRail();
            bar.Value = 0; bar.Maximum = 1;
            workText.Content = Ui.Two(what, 13, 11);
            work.Visibility = Visibility.Visible;
            new Thread(() =>
            {
                Failure fail = null;
                try
                {
                    job((i, n) => Dispatcher.BeginInvoke((Action)(() => { bar.Maximum = Math.Max(1, n); bar.Value = i; })));
                }
                catch (Failure f) { fail = f; Kit.Log("ERROR " + f.Message); }
                catch (UnauthorizedAccessException e)          // e.g. a game under Program Files
                {
                    fail = new Failure("ويندوز ما سمح لي أكتب في مجلد اللعبة. سكّر البرنامج وشغّله كمسؤول (زر يمين ← تشغيل كمسؤول).",
                                       "Windows did not let me write to the game folder. Close the program and run it as administrator (right-click > Run as administrator).");
                    Kit.Log("ERROR " + e.Message);
                }
                catch (Exception e)
                {
                    fail = new Failure("صار خطأ ما توقعته. انسخ السجل وأرسله لي: " + e.Message, "Unexpected error. Copy the log and send it to me: " + e.Message);
                    Kit.Log("ERROR " + e);
                }
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    busy = false;
                    folder.IsReadOnly = false;
                    work.Visibility = Visibility.Collapsed;
                    foreach (var b in locked) b.IsEnabled = true;
                    DrawRail();
                    if (fail != null) { Banner("Danger", fail.Text); Inspect(); }
                    else success();
                }));
            }) { IsBackground = true }.Start();
        }

        void OnInstall()
        {
            string dir = folder.Text.Trim();
            Outcome outcome = Outcome.Installed;
            Run(new Bi("أثبّت التعريب...", "Installing the translation..."), p => outcome = Kit.Install(g, dir, p), () =>
            {
                if (outcome == Outcome.AlreadyInstalled)
                {
                    Banner("Accent", new Bi("التعريب مثبّت من قبل، ما تغيّر شي.", "The translation was already installed; nothing changed."));
                    Inspect();
                    return;
                }
                doneKind = outcome == Outcome.Updated ? "updated" : "installed";
                GoTo(Page.Done);
            });
        }

        void OnUninstall()
        {
            string dir = folder.Text.Trim();
            Run(new Bi("أرجّع ملفات اللعبة الأصلية...", "Restoring the original game files..."), p => Kit.Uninstall(g, dir, p), () =>
            {
                doneKind = "removed";
                GoTo(Page.Done);
            });
        }

        void OnPrepare()
        {
            string gp = gamePkg.Text.Trim(), up = updatePkg.Text.Trim(), t = toolDir.Text.Trim(), o = outDir.Text.Trim();
            if (!File.Exists(gp) || !File.Exists(up) || !Directory.Exists(t) || !Directory.Exists(o))
            {
                Banner("Danger", new Bi("اختر ملفي الـ PKG والمجلدين أول.", "Choose both PKG files and both folders first."));
                return;
            }
            string made = null;
            Run(new Bi("أجهّز مجلد اللعبة (ياخذ وقت طويل)...", "Making the game folder (this takes a long time)..."),
                p => made = Ps4Pkg.Prepare(g.Ps4Pkg, gp, up, t, o, stage => p(stage, 5)), () =>
                {
                    folder.Text = made;
                    GoTo(Page.Install);
                    Banner("Accent", new Bi("تم تجهيز مجلد اللعبة. اختاره في البورت، وبعدين ثبّت التعريب.",
                                            "The game folder is ready. Choose it in the port, then install the translation."));
                });
        }

        void CheckUpdates()
        {
            if (string.IsNullOrEmpty(g.Github)) return;
            Banner("Muted", new Bi("أتحقق من التحديثات...", "Checking for updates..."));
            new Thread(() =>
            {
                Release r = null;
                Exception err = null;
                try { r = Updates.Latest(g); }
                catch (Exception e) { err = e; }
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (err != null)
                    {
                        Kit.Log("update check failed: " + err.Message);
                        Banner("Danger", new Bi("ما قدرت أتحقق من التحديثات. تأكد من الإنترنت.", "Could not check for updates. Check your internet connection."));
                    }
                    else if (r == null || !r.Newer)
                        Banner("Accent", new Bi("عندك آخر إصدار.", "You have the latest version."));
                    else
                    {
                        string url = r.Url;
                        Banner("Gold", new Bi("فيه إصدار جديد من التعريب: " + r.Tag, "A new version of the translation is out: " + r.Tag),
                               new Bi("افتح صفحة التحميل", "Open the download page"), () => Open(url));
                    }
                }));
            }) { IsBackground = true }.Start();
        }

        // ------------------------------------------------------------ small things

        void Banner(string brush, Bi text, Bi action = null, Action onAction = null)
        {
            var color = ((SolidColorBrush)Ui.B(brush)).Color;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(Ui.Two(text, 14, 12));
            if (action != null)
            {
                var b = Ui.Btn(action, "Btn", onAction, 13, 10.5);
                b.Margin = new Thickness(12, 0, 0, 0);
                b.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(b, 1);
                grid.Children.Add(b);
            }
            banner.Background = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B));
            banner.BorderBrush = new SolidColorBrush(color);
            banner.BorderThickness = new Thickness(0, 0, 3, 0);      // the start side (right) in the right-to-left page
            banner.CornerRadius = new CornerRadius(10);
            banner.Padding = new Thickness(16, 10, 14, 10);
            banner.Child = grid;
            banner.Visibility = Visibility.Visible;
        }

        void HideBanner() { banner.Visibility = Visibility.Collapsed; }

        void Open(string target)
        {
            try
            {
                if (string.IsNullOrEmpty(target)) return;
                if (target.StartsWith("http")) Process.Start(target);
                else if (Directory.Exists(target)) Process.Start("explorer.exe", "\"" + target + "\"");
            }
            catch (Exception e) { Kit.Log("could not open " + target + ": " + e.Message); }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // dark title bar (Windows 10 2004+) and its colour (Windows 11); ignored where not supported
        void DarkTitleBar()
        {
            try
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                int on = 1, caption = 0x001B1512;        // COLORREF of #12151B
                DwmSetWindowAttribute(h, 20, ref on, 4);
                DwmSetWindowAttribute(h, 35, ref caption, 4);
            }
            catch (Exception) { }
        }
    }
}
