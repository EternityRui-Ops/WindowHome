using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class RenderUi
{
    [STAThread] private static int Main(string[] args)
    {
        string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-renders"); Directory.CreateDirectory(folder);
        int errors = 0;
        foreach (var scenario in new[] { new[] { 1160, 790, 96 }, new[] { 1160, 790, 144 }, new[] { 1160, 790, 192 }, new[] { 990, 660, 96 } })
        {
            Window window;
            using (var stream = typeof(WindowHome.Program).Assembly.GetManifestResourceStream("WindowHome.Main.xaml")) window = (Window)XamlReader.Load(stream);
            var root = (FrameworkElement)window.Content; window.Content = null; root.Resources = window.Resources;
            ((Panel)root).Background = window.Background;
            root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, window.FontFamily);
            root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, window.FontSize);
            root.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, window.Foreground);
            var pages = (Grid)window.FindName("Pages");
            for (int i = 0; i < pages.Children.Count; i++) pages.Children[i].Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)window.FindName("TargetLabel")).Text = "恢复目标：日常工作布局";
            ((TextBlock)window.FindName("TargetHint")).Text = "4 个窗口 · 2 块屏幕 · 固定布局";
            root.Width = scenario[0]; root.Height = scenario[1];
            root.Measure(new Size(root.Width, root.Height)); root.Arrange(new Rect(0, 0, root.Width, root.Height)); root.UpdateLayout();
            foreach (string name in new[] { "RestoreButton", "SaveButton", "UndoButton", "LaunchButton", "PauseButton" })
            {
                var button = (FrameworkElement)window.FindName(name);
                Rect r = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                if (r.X < 0 || r.Y < 0 || r.Right > root.Width || r.Bottom > root.Height) { errors++; Console.WriteLine("FAIL clipped " + name); }
            }
            double scale = scenario[2] / 96.0;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.Width * scale), (int)Math.Ceiling(root.Height * scale), scenario[2], scenario[2], PixelFormats.Pbgra32);
            bitmap.Render(root); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            string namePart = scenario[0] + "x" + scenario[1] + "-" + scenario[2] + "dpi";
            using (var file = File.Create(Path.Combine(folder, namePart + ".png"))) png.Save(file);
            Console.WriteLine("RENDER " + namePart);
        }
        Console.WriteLine("Layout errors: " + errors); return errors == 0 ? 0 : 1;
    }
}
