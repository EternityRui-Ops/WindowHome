using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace WindowHome
{
    public class PixelRect
    {
        public int X, Y, Width, Height;
        public PixelRect() { }
        public PixelRect(int x, int y, int w, int h) { X = x; Y = y; Width = w; Height = h; }
        [XmlIgnore] public int Right { get { return X + Width; } }
        [XmlIgnore] public int Bottom { get { return Y + Height; } }
        public PixelRect Copy() { return new PixelRect(X, Y, Width, Height); }
        public bool Near(PixelRect r, int tolerance)
        {
            return r != null && Math.Abs((long)X - r.X) <= tolerance && Math.Abs((long)Y - r.Y) <= tolerance &&
                Math.Abs((long)Width - r.Width) <= tolerance && Math.Abs((long)Height - r.Height) <= tolerance;
        }
        public override string ToString() { return String.Format("{0} × {1}  ·  ({2}, {3})", Width, Height, X, Y); }
    }
    public class DisplayRecord
    {
        public string Id = "", Name = "";
        public PixelRect Bounds = new PixelRect(), Work = new PixelRect();
        public uint Dpi = 96;
        public bool Primary;
        [XmlIgnore] public string Caption { get { return Name + (Primary ? " · 主屏" : "") + "  " + Bounds.Width + " × " + Bounds.Height + "  " + Math.Round(Dpi / 96.0 * 100) + "%"; } }
    }
    public class WindowRecord
    {
        public long Handle, ProcessStarted;
        public int Pid, Z;
        public int AppWindowCount = 1;
        public string Path = "", App = "", ClassName = "", Title = "", DesktopId = "", DisplayId = "";
        public PixelRect Rect = new PixelRect(), Work = new PixelRect();
        public uint Dpi = 96;
        public int State = 1;
        public bool RestoreToMax, Topmost, Fullscreen;
        public DateTime Updated = DateTime.UtcNow;
        [XmlIgnore] public string AppKey { get { return (String.IsNullOrEmpty(Path) ? App : Path).ToLowerInvariant() + "|" + ClassName; } }
        [XmlIgnore] public string Geometry { get { return Rect + (State == 3 ? "  ·  最大化" : State == 2 ? "  ·  最小化" : ""); } }
        [XmlIgnore] public string DisplayTitle { get { return String.IsNullOrEmpty(Title) ? App : Title; } }
        [XmlIgnore] public string AppName { get { return App; } }
        public WindowRecord Copy() { var n = (WindowRecord)MemberwiseClone(); n.Rect = Rect.Copy(); n.Work = Work.Copy(); return n; }
    }
    public class LayoutSnapshot
    {
        public string Id = Guid.NewGuid().ToString("N"), Name = "", Topology = "", DesktopId = "";
        public DateTime Updated = DateTime.UtcNow;
        public List<DisplayRecord> Displays = new List<DisplayRecord>();
        public List<WindowRecord> Windows = new List<WindowRecord>();
        [XmlIgnore] public string Context { get { return Topology + "|" + DesktopId; } }
        [XmlIgnore] public string Description { get { return Windows.Count + " 个窗口 · " + Displays.Count + " 块屏幕 · " + Updated.ToLocalTime().ToString("MM-dd HH:mm"); } }
        [XmlIgnore] public string DisplayName { get { return Name; } }
        public LayoutSnapshot Copy()
        {
            var n = (LayoutSnapshot)MemberwiseClone(); n.Displays = new List<DisplayRecord>(Displays); n.Windows = Windows.Select(w => w.Copy()).ToList(); return n;
        }
    }
    public class HabitRecord
    {
        public string Context = "";
        public WindowRecord Window = new WindowRecord();
    }
    public class DefaultLayout
    {
        public string Context = "", LayoutId = "";
    }
    public class AppOptions
    {
        public bool Learning = true, RestoreWake = true, RestoreDisplay = true, RestoreNew = true, Paused;
        public string RestoreHotkey = "Ctrl+Alt+R", SaveHotkey = "Ctrl+Alt+S";
        public List<string> Excluded = new List<string>();
    }
    public static class AppRules
    {
        public static bool Excludes(string appPath, string appName, IEnumerable<string> rules)
        {
            return rules.Any(rule =>
            {
                string r = rule.Trim().Trim('"');
                if (r.Length == 0) return false;
                if (r.Contains("\\") || r.Contains("/")) return String.Equals(r, appPath, StringComparison.OrdinalIgnoreCase);
                if (r.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) r = r.Substring(0, r.Length - 4);
                return String.Equals(r, appName, StringComparison.OrdinalIgnoreCase);
            });
        }
    }
    public class RestoreItem
    {
        public string App = "", Title = "", Status = "", Detail = "";
        public bool Success;
        public PixelRect Target;
        [XmlIgnore] public string Label { get { return App + "  ·  " + Title; } }
        [XmlIgnore] public string AppName { get { return App; } }
        [XmlIgnore] public string WindowTitle { get { return Title; } }
        [XmlIgnore] public string ResultStatus { get { return Status; } }
        [XmlIgnore] public string ResultDetail { get { return Detail; } }
    }
    public class RestoreReport
    {
        public DateTime At = DateTime.UtcNow;
        public string LayoutName = "", Trigger = "";
        public List<RestoreItem> Items = new List<RestoreItem>();
        [XmlIgnore] public int SuccessCount { get { return Items.Count(x => x.Success); } }
        [XmlIgnore] public string Summary { get { return Items.Count == 0 ? "没有可恢复的窗口" : SuccessCount + " / " + Items.Count + " 个窗口已核验恢复"; } }
        [XmlIgnore] public string Caption { get { return At.ToLocalTime().ToString("MM-dd HH:mm:ss") + "  ·  " + LayoutName + "  ·  " + Summary; } }
        [XmlIgnore] public string Outcome { get { return Items.Count > 0 && SuccessCount == Items.Count ? "全部成功" : SuccessCount > 0 ? "部分成功" : "未成功"; } }
        [XmlIgnore] public string TriggerGroup
        {
            get
            {
                if (Trigger == "撤销") return "撤销";
                if (Trigger == "启动缺失软件" || Trigger == "启动软件") return "启动软件";
                return new[] { "新窗口", "系统唤醒", "会话恢复", "显示器配置变化" }.Contains(Trigger) ? "自动恢复" : "手动恢复";
            }
        }
    }
    public static class ReportHistory
    {
        public const int Limit = 100;
        public static void Trim(AppData data)
        {
            if (data.Reports.Count <= Limit) return;
            data.Reports = data.Reports.OrderByDescending(r => r.At).Take(Limit).ToList();
        }
        public static void Add(AppData data, RestoreReport report) { data.Reports.Insert(0, report); Trim(data); }
        public static List<RestoreReport> Filter(IEnumerable<RestoreReport> reports, string query, string outcome, string trigger)
        {
            query = (query ?? "").Trim();
            return reports.Where(r => (String.IsNullOrEmpty(outcome) || outcome == "全部结果" || r.Outcome == outcome) &&
                (String.IsNullOrEmpty(trigger) || trigger == "全部方式" || r.TriggerGroup == trigger) &&
                (query.Length == 0 || String.Join("\n", new[] { r.LayoutName, r.Trigger, r.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    String.Join("\n", r.Items.Select(i => i.App + " " + i.Title + " " + i.Status + " " + i.Detail)) }).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        }
    }
    public class LaunchChoice
    {
        public string Path, App;
        public bool IsOpen, CanSelect;
        public string Status;
        public List<WindowRecord> Windows;
        public string Caption { get { return App + (Windows.Count > 1 ? " · " + Windows.Count + " 个窗口" : ""); } }
    }
    public static class LaunchSelection
    {
        public static List<LaunchChoice> Choices(LayoutSnapshot layout, LayoutSnapshot current, AppOptions options, Func<string, bool> exists)
        {
            return layout.Windows.GroupBy(w => String.IsNullOrEmpty(w.Path) ? w.AppKey : w.Path, StringComparer.OrdinalIgnoreCase).Select(g =>
            {
                var w = g.First(); bool excluded = AppRules.Excludes(w.Path, w.App, options.Excluded);
                bool supported = !String.IsNullOrEmpty(w.Path) && w.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                bool available = supported && exists(w.Path);
                bool open = current.Windows.Any(l => String.Equals(l.Path, w.Path, StringComparison.OrdinalIgnoreCase) && !String.IsNullOrEmpty(w.Path));
                return new LaunchChoice { Path = w.Path, App = w.App, Windows = g.ToList(), IsOpen = open, CanSelect = available && !excluded,
                    Status = excluded ? "已排除" : !supported ? "不支持启动" : !available ? "路径失效" : open ? "已打开，将使用现有窗口" : "未打开" };
            }).ToList();
        }
        public static LayoutSnapshot Subset(LayoutSnapshot layout, IEnumerable<string> selectedPaths)
        {
            var paths = new HashSet<string>(selectedPaths.Where(p => !String.IsNullOrEmpty(p)), StringComparer.OrdinalIgnoreCase);
            var subset = layout.Copy(); subset.Name = layout.Name + " · 自选软件";
            subset.Windows = subset.Windows.Where(w => paths.Contains(w.Path)).ToList(); return subset;
        }
    }
    public class AppData
    {
        public int Version = 1;
        public AppOptions Options = new AppOptions();
        public List<LayoutSnapshot> Layouts = new List<LayoutSnapshot>(), Memories = new List<LayoutSnapshot>();
        public List<HabitRecord> Habits = new List<HabitRecord>();
        public List<DefaultLayout> Defaults = new List<DefaultLayout>();
        public List<RestoreReport> Reports = new List<RestoreReport>();
        public LayoutSnapshot Undo;
    }
    public static class Geometry
    {
        public static string Topology(IEnumerable<DisplayRecord> displays)
        {
            string s = String.Join(";", displays.OrderBy(d => d.Id, StringComparer.Ordinal).Select(d =>
                d.Id + ":" + d.Bounds.X + "," + d.Bounds.Y + "," + d.Bounds.Width + "," + d.Bounds.Height + "," + d.Dpi + "," + d.Work.X + "," + d.Work.Y + "," + d.Work.Width + "," + d.Work.Height));
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "").Substring(0, 24);
        }
        public static DisplayRecord TargetDisplay(WindowRecord w, IList<DisplayRecord> displays)
        {
            return displays.FirstOrDefault(d => d.Id == w.DisplayId) ?? displays.FirstOrDefault(d => d.Primary) ?? displays.First();
        }
        public static PixelRect Map(WindowRecord w, IList<DisplayRecord> displays)
        {
            var d = TargetDisplay(w, displays); var a = w.Work; var b = d.Work;
            bool unchanged = a.Near(b, 0) && w.DisplayId == d.Id && w.Dpi == d.Dpi;
            var r = w.Rect.Copy();
            if (!unchanged && a.Width > 0 && a.Height > 0)
            {
                r = new PixelRect(b.X + (int)Math.Round((w.Rect.X - a.X) * (double)b.Width / a.Width),
                    b.Y + (int)Math.Round((w.Rect.Y - a.Y) * (double)b.Height / a.Height),
                    (int)Math.Round(w.Rect.Width * (double)b.Width / a.Width), (int)Math.Round(w.Rect.Height * (double)b.Height / a.Height));
            }
            r.Width = Math.Min(b.Width, Math.Max(Math.Min(160, b.Width), r.Width));
            r.Height = Math.Min(b.Height, Math.Max(Math.Min(100, b.Height), r.Height));
            r.X = Math.Max(b.X, Math.Min(b.Right - r.Width, r.X)); r.Y = Math.Max(b.Y, Math.Min(b.Bottom - r.Height, r.Y));
            return r;
        }
        public static string Signature(LayoutSnapshot s)
        {
            return s.Context + ":" + String.Join(";", s.Windows.OrderBy(w => w.Handle).Select(w => w.Handle + ":" + w.Rect + ":" + w.State));
        }
    }
    public class MatchResult
    {
        public WindowRecord Saved, Live;
        public string Reason;
    }
    public static class Matcher
    {
        public static bool SameInstance(WindowRecord a, WindowRecord b)
        {
            return a.Handle != 0 && a.Handle == b.Handle && a.Pid == b.Pid && a.ProcessStarted != 0 && a.ProcessStarted == b.ProcessStarted && a.AppKey == b.AppKey;
        }
        public static List<MatchResult> Match(IList<WindowRecord> saved, IList<WindowRecord> live, bool allowDifferentDesktop)
        {
            var result = saved.Select(s => new MatchResult { Saved = s }).ToList();
            var used = new HashSet<long>();
            Func<WindowRecord, WindowRecord, bool> compatible = (s, l) => s.AppKey == l.AppKey &&
                (allowDifferentDesktop || s.DesktopId == l.DesktopId);
            // Resolve all strong identities before considering weaker fallbacks.
            foreach (var r in result)
            {
                var c = live.Where(l => !used.Contains(l.Handle) && compatible(r.Saved, l) && SameInstance(r.Saved, l)).ToList();
                if (c.Count == 1) { r.Live = c[0]; used.Add(c[0].Handle); }
            }
            foreach (var r in result.Where(x => x.Live == null))
            {
                var c = live.Where(l => !used.Contains(l.Handle) && compatible(r.Saved, l) && !String.IsNullOrEmpty(r.Saved.Title) && r.Saved.Title == l.Title).ToList();
                int competing = result.Count(x => x.Live == null && x.Saved.AppKey == r.Saved.AppKey && x.Saved.Title == r.Saved.Title &&
                    (allowDifferentDesktop || x.Saved.DesktopId == r.Saved.DesktopId));
                if (c.Count == 1 && competing == 1) { r.Live = c[0]; used.Add(c[0].Handle); }
            }
            foreach (var r in result.Where(x => x.Live == null))
            {
                var c = live.Where(l => !used.Contains(l.Handle) && compatible(r.Saved, l)).ToList();
                int remaining = result.Count(x => x.Live == null && x.Saved.AppKey == r.Saved.AppKey &&
                    (allowDifferentDesktop || x.Saved.DesktopId == r.Saved.DesktopId));
                bool singleton = r.Saved.AppWindowCount == 1 && c.Count == 1 && c[0].AppWindowCount == 1 &&
                    saved.Count(s => s.AppKey == r.Saved.AppKey) == 1 && live.Count(l => compatible(r.Saved, l)) == 1;
                if (singleton && remaining == 1) { r.Live = c[0]; used.Add(c[0].Handle); }
                else r.Reason = c.Count == 0 ? "未找到当前桌面的对应窗口" : "同一软件有多个窗口，无法唯一匹配，已跳过";
            }
            return result;
        }
    }
    public class DataStore
    {
        public readonly string DirectoryPath;
        public string Warning = "";
        public bool ReadOnly;
        private readonly XmlSerializer serializer = new XmlSerializer(typeof(AppData));
        public DataStore(string path) { DirectoryPath = path; }
        public string FilePath { get { return System.IO.Path.Combine(DirectoryPath, "state.xml"); } }
        public AppData Load()
        {
            foreach (string p in new[] { FilePath, FilePath + ".bak" })
            {
                if (!File.Exists(p)) continue;
                try
                {
                    using (var reader = XmlReader.Create(p, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 20000000 }))
                    {
                        var data = (AppData)serializer.Deserialize(reader);
                        if (data.Version != 1) { ReadOnly = true; Warning = "数据来自其他版本，已进入只读模式，原文件不会被覆盖。"; return new AppData(); }
                        ReportHistory.Trim(data);
                        if (p != FilePath) Warning = "主数据文件无法读取，已从备份恢复。";
                        return data;
                    }
                }
                catch (Exception ex) { Warning = "数据读取失败：" + ex.GetBaseException().Message; }
            }
            if (File.Exists(FilePath)) { ReadOnly = true; Warning += " 原文件已保留，当前为只读模式。"; }
            return new AppData();
        }
        public void Save(AppData data)
        {
            if (ReadOnly) throw new InvalidOperationException("数据为只读模式，请先备份并修复或更换数据目录。");
            ReportHistory.Trim(data);
            Directory.CreateDirectory(DirectoryPath);
            string temp = FilePath + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { serializer.Serialize(stream, data); stream.Flush(true); }
            if (File.Exists(FilePath))
            {
                // When recovering a corrupt primary, preserve the known-good backup.
                if (Warning.StartsWith("主数据")) { File.Replace(temp, FilePath, null, true); Warning = ""; }
                else File.Replace(temp, FilePath, FilePath + ".bak", true);
            }
            else File.Move(temp, FilePath);
        }
    }
}
