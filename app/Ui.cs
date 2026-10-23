// Building blocks for the window. Rule for every text: Arabic on its own line (right to left), English on the next
// line (left to right), never both on one line.
using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Taareeb.Installer
{
    static class Ui
    {
        public static ResourceDictionary Theme;

        public static Stream Res(string name)
        {
            var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (s == null) throw new FileNotFoundException("resource missing: " + name);
            return s;
        }

        public static void LoadTheme() { using (var s = Res("Theme.xaml")) Theme = (ResourceDictionary)XamlReader.Load(s); }

        public static Brush B(string key) { return (Brush)Theme[key]; }
        public static Style S(string key) { return (Style)Theme[key]; }

        public static Color C(string hex, byte alpha)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            c.A = alpha;
            return c;
        }

        public static ImageSource Bitmap(string name)
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = Res(name);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }

        // Readex Pro (the iosamai.com font, OFL) is embedded; WPF loads fonts from a folder, so it is written out once
        public static FontFamily LoadFont()
        {
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                    "Taareeb\\Installer\\fonts-" + BuildInfo.Engine);
                Directory.CreateDirectory(dir);
                foreach (string name in new[] { "ReadexPro-Regular.ttf", "ReadexPro-SemiBold.ttf" })
                {
                    string p = System.IO.Path.Combine(dir, name);
                    if (File.Exists(p)) continue;
                    using (var s = Res(name))
                    using (var f = File.Create(p + ".tmp")) s.CopyTo(f);
                    File.Move(p + ".tmp", p);
                }
                return new FontFamily(new Uri(dir + "\\"), "./#Readex Pro, Segoe UI");
            }
            catch (Exception) { return new FontFamily("Segoe UI"); }
        }

        // WPF's bidi has no bracket pairing: in an Arabic line, "(...)" around English text ("Redistributable (x64).",
        // "Xbox 360 (Title ID: 4B4E0823) كملف") splits, each bracket ending up on a different side. A left-to-right mark on
        // both sides of a bracket pair with no Arabic inside keeps it in one left-to-right run with its English words.
        // Brackets with Arabic inside ("(مجاني، من GitHub)") are already right and are left alone.
        static readonly Regex LatinBrackets = new Regex(@"\([^()\u0600-\u06FF]*\)");

        static string KeepBrackets(string s) { return LatinBrackets.Replace(s, "\u200E$0\u200E"); }

        static readonly Regex ArabicLetter = new Regex(@"[\u0600-\u06FF]");

        public static TextBlock T(string text, double size, string brush, bool arabic, bool bold = false)
        {
            arabic = arabic && ArabicLetter.IsMatch(text);       // a name like ".NET Runtime" reads left to right
            var t = new TextBlock
            {
                Text = arabic ? KeepBrackets(text) : text, FontSize = size, Foreground = B(brush), TextWrapping = TextWrapping.Wrap,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                FlowDirection = arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                HorizontalAlignment = HorizontalAlignment.Left,     // the start side of the right-to-left page
            };
            return t;
        }

        public static StackPanel Two(Bi b, double ar, double en, string arBrush = "Text", string enBrush = "Muted", bool bold = false)
        {
            var p = new StackPanel();
            if (b.Ar != "") p.Children.Add(T(b.Ar, ar, arBrush, true, bold));
            if (b.En != "")
            {
                var e = T(b.En, en, enBrush, false);
                e.Margin = new Thickness(0, 1, 0, 0);
                p.Children.Add(e);
            }
            return p;
        }

        public static Button Btn(Bi label, string style, Action click, double ar = 14, double en = 11)
        {
            var b = new Button { Content = BtnText(label, style, ar, en), Style = S(style), Margin = new Thickness(0, 0, 10, 10) };
            b.Click += (s, e) => click();
            return b;
        }

        public static StackPanel BtnText(Bi label, string style, double ar = 14, double en = 11)
        {
            var p = new StackPanel();
            foreach (var line in new[] { label.Ar, label.En })
            {
                if (line == "") continue;
                bool first = line == label.Ar, arabic = first && ArabicLetter.IsMatch(line);
                p.Children.Add(new TextBlock
                {
                    Text = arabic ? KeepBrackets(line) : line, FontSize = first ? ar : en, HorizontalAlignment = HorizontalAlignment.Center,
                    FontWeight = first && style == "Primary" ? FontWeights.SemiBold : FontWeights.Normal,
                    FlowDirection = arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                    Opacity = first ? 1 : 0.8,
                });
            }
            return p;
        }

        public static Border Card(UIElement child, Thickness? margin = null)
        {
            return new Border
            {
                Background = B("Surface"), BorderBrush = B("Line"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12, 16, 12),
                Margin = margin ?? new Thickness(0, 0, 0, 8), Child = child,
            };
        }

        // a card with a marker (dot or number) on the start side
        public static Border Item(UIElement marker, UIElement body)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var m = new Border { Child = marker, Margin = new Thickness(0, 4, 0, 0), VerticalAlignment = VerticalAlignment.Top, Width = 34 };
            g.Children.Add(m);
            Grid.SetColumn((UIElement)body, 1);
            g.Children.Add(body);
            return Card(g);
        }

        public static UIElement Dot(string brush)
        {
            return new Ellipse { Width = 8, Height = 8, Fill = B(brush), Margin = new Thickness(0, 5, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        }

        public static UIElement Badge(int n)
        {
            return new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = B("AccentSoft"),
                BorderBrush = B("Accent"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, -2, 0, 0),
                Child = new TextBlock
                {
                    Text = Digits(n), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("Accent2"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }

        public static string Digits(int n)
        {
            var s = n.ToString().ToCharArray();
            for (int i = 0; i < s.Length; i++) s[i] = (char)('٠' + (s[i] - '0'));
            return new string(s);
        }

        public static System.Windows.Shapes.Path Check(double size, string brush, double thick)
        {
            return new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 0,5 L 3.6,8.6 L 10,1.6"), Stroke = B(brush), StrokeThickness = thick,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Width = size, Height = size * 0.86, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FlowDirection = FlowDirection.LeftToRight,
            };
        }

        public static LinearGradientBrush Fade(Point from, Point to, params double[] offsetAlpha)
        {
            var g = new LinearGradientBrush { StartPoint = from, EndPoint = to };
            for (int i = 0; i + 1 < offsetAlpha.Length; i += 2)
                g.GradientStops.Add(new GradientStop(C("#0D0F13", (byte)(offsetAlpha[i + 1] * 255)), offsetAlpha[i]));
            return g;
        }
    }
}
