using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal sealed class Fixture : Form
{
    private readonly string folder;
    private string last = "";
    private readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 80 };
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    protected override bool ShowWithoutActivation { get { return true; } }
    [STAThread] private static void Main(string[] args)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        Application.EnableVisualStyles(); Application.Run(new Fixture(args[0], args[1]));
    }
    private Fixture(string path, string name)
    {
        folder = path; Directory.CreateDirectory(folder); Text = "窗归受控测试 · " + name;
        StartPosition = FormStartPosition.Manual; Bounds = new Rectangle(120, 130, 650, 420); ShowInTaskbar = true;
        Controls.Add(new Label { Text = "这是窗归的受控验证窗口\n\n仅测试这些窗口，不调整用户其他软件。", Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 15), TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.FromArgb(234, 246, 242) });
        Shown += delegate { File.WriteAllText(Path.Combine(folder, "ready.txt"), Handle.ToInt64().ToString()); poll.Start(); };
        poll.Tick += delegate
        {
            string file = Path.Combine(folder, "cmd.txt"); if (!File.Exists(file)) return;
            string text; try { text = File.ReadAllText(file); } catch (IOException) { return; }
            if (last == text || text.Length == 0) return; last = text; var p = text.Split('|');
            try
            {
                switch (p[1])
                {
                    case "move": WindowState = FormWindowState.Normal; Bounds = new Rectangle(Int32.Parse(p[2]), Int32.Parse(p[3]), Int32.Parse(p[4]), Int32.Parse(p[5])); break;
                    case "max": WindowState = FormWindowState.Maximized; break;
                    case "min": WindowState = FormWindowState.Minimized; break;
                    case "normal": WindowState = FormWindowState.Normal; break;
                    case "title": Text = p[2]; break;
                    case "front": BringToFront(); break;
                }
                File.WriteAllText(Path.Combine(folder, "ack.txt"), p[0]);
                if (p[1] == "hang") Thread.Sleep(15000);
                if (p[1] == "close") Close();
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "error.txt"), ex.ToString()); }
        };
    }
}
