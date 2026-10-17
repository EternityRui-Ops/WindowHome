using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowHome;

namespace WindowHome.Tests
{
    internal static class Runner
    {
        private static int passed, failed;
        private static readonly List<string> results = new List<string>();
        private static string root;
        private static void Check(string name, bool value, string detail)
        {
            string line = (value ? "PASS " : "FAIL ") + name + (String.IsNullOrEmpty(detail) ? "" : " — " + detail);
            Console.WriteLine(line); results.Add(line); if (value) passed++; else failed++;
        }
        private static void Check(string name, bool value) { Check(name, value, ""); }
        private static DisplayRecord Display(string id, int x, int y, int w, int h, uint dpi, bool primary)
        { return new DisplayRecord { Id = id, Name = id, Bounds = new PixelRect(x, y, w, h), Work = new PixelRect(x, y, w, h - 40), Dpi = dpi, Primary = primary }; }
        private static WindowRecord W(long handle, string title)
        { return new WindowRecord { Handle = handle, Pid = (int)handle + 100, ProcessStarted = handle + 5000, App = "test", Path = "C:\\test.exe", ClassName = "TestClass", Title = title, DesktopId = "desk-a", Rect = new PixelRect(100, 120, 600, 400), Work = new PixelRect(0, 0, 1920, 1040), DisplayId = "one", State = 1 }; }
        private static LayoutSnapshot Snap(params WindowRecord[] windows)
        { var s = new LayoutSnapshot { Displays = new List<DisplayRecord> { Display("one", 0, 0, 1920, 1080, 96, true) }, DesktopId = "desk-a", Windows = windows.ToList() }; s.Topology = Geometry.Topology(s.Displays); return s; }
        [STAThread] public static int Main(string[] args)
        {
            WindowsSystem.EnableDpi(); root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(root);
            try { UnitTests().GetAwaiter().GetResult(); RevisionTests(); if (!args.Contains("--unit-only")) NativeTests().GetAwaiter().GetResult(); else results.Add("SKIP 原生窗口测试（--unit-only）"); }
            catch (Exception ex) { Check("测试运行器异常", false, ex.ToString()); }
            string report = "WindowHome verification\r\nUTC " + DateTime.UtcNow.ToString("s") + "\r\n" + String.Join("\r\n", results) + "\r\n\r\nPassed: " + passed + "; Failed: " + failed + "\r\n";
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-results.txt"), report, System.Text.Encoding.UTF8);
            Console.WriteLine("Passed: " + passed + "; Failed: " + failed); return failed == 0 ? 0 : 1;
        }
        private static async Task UnitTests()
        {
            var one = Display("one", 0, 0, 1920, 1080, 96, true); var second = Display("two", -2560, -200, 2560, 1440, 144, false);
            var w = W(1, "alpha");
            Check("同配置精确映射", Geometry.Map(w, new[] { one }).Near(w.Rect, 0));
            w.DisplayId = "two"; w.Work = second.Work.Copy(); w.Rect = new PixelRect(-2400, -100, 1200, 800); w.Dpi = 144;
            Check("多屏负坐标", Geometry.Map(w, new[] { one, second }).Near(w.Rect, 0));
            var missing = Geometry.Map(w, new[] { one }); Check("移除显示器后完整可见", missing.X >= 0 && missing.Y >= 0 && missing.Right <= one.Work.Right && missing.Bottom <= one.Work.Bottom);
            var scaled = Display("two", -1920, 0, 1920, 1080, 120, false); var mapped = Geometry.Map(w, new[] { one, scaled });
            Check("混合 DPI 与分辨率变化映射", mapped.X >= scaled.Work.X && mapped.Right <= scaled.Work.Right && mapped.Width == 900);
            var taskbar = Display("topbar", -1920, 0, 1920, 1080, 96, false); taskbar.Work = new PixelRect(-1880, 60, 1880, 1020);
            var coordinates = new PixelRect(-1800, 150, 500, 350);
            Check("顶部及左侧任务栏坐标往返", WindowsSystem.PlacementToScreen(WindowsSystem.ScreenToPlacement(coordinates, taskbar), taskbar).Near(coordinates, 0));
            Check("显示配置排列顺序无关", Geometry.Topology(new[] { one, second }) == Geometry.Topology(new[] { second, one }));
            Check("显示配置包含 DPI", Geometry.Topology(new[] { one, second }) != Geometry.Topology(new[] { one, scaled }));
            var a = W(1, "a"); var b = W(2, "b"); var changed = a.Copy(); changed.Title = "new title";
            Check("同进程实例标题变化", Matcher.Match(new[] { a }, new[] { changed }, false)[0].Live != null);
            var reused = a.Copy(); reused.ProcessStarted += 1; reused.Title = "other"; reused.Path = "C:\\other.exe";
            Check("复用 HWND 不误认其他进程", Matcher.Match(new[] { a }, new[] { reused }, false)[0].Live == null);
            var newA = W(10, "a"); var newB = W(20, "b");
            var exact = Matcher.Match(new[] { a, b }, new[] { newB, newA }, false);
            Check("重建窗口按唯一标题匹配", exact[0].Live == newA && exact[1].Live == newB);
            var x = W(11, "same"); var y = W(12, "same");
            Check("同标题多窗拒绝猜测", Matcher.Match(new[] { a, b }, new[] { x, y }, false).All(m => m.Live == null));
            var partial = b.Copy(); partial.AppWindowCount = 2;
            Check("关闭多窗之一不会错配剩余窗口", Matcher.Match(new[] { partial }, new[] { changed }, false)[0].Live == null);
            var otherDesktop = newA.Copy(); otherDesktop.DesktopId = "desk-b";
            Check("自动匹配隔离虚拟桌面", Matcher.Match(new[] { a }, new[] { otherDesktop }, false)[0].Live == null);
            Check("显式手动适配当前桌面", Matcher.Match(new[] { a }, new[] { otherDesktop }, true)[0].Live != null);
            var data = new AppData(); var learn = new LearningService(data); var snapshot = Snap(a); var now = DateTime.UtcNow;
            learn.Observe(snapshot, now); learn.Observe(snapshot, now.AddSeconds(4)); Check("未满 5 秒不学习", data.Memories.Count == 0);
            Check("稳定 5 秒建立记忆", learn.Observe(snapshot, now.AddSeconds(5)) && data.Memories.Count == 1);
            var fake = new FakeSystem(snapshot); var service = new LayoutService(data, fake); var named = service.SaveNamed(snapshot, "日常"); service.SetDefault(named, snapshot.Context);
            var moved = snapshot.Copy(); moved.Windows[0].Rect.X = 250; learn.Observe(moved, now.AddSeconds(6)); learn.Observe(moved, now.AddSeconds(11));
            Check("自动学习不覆盖命名布局", named.Windows[0].Rect.X == 100 && data.Memories[0].Windows[0].Rect.X == 250);
            Check("主按钮优先默认布局", service.Target(snapshot).Id == named.Id);
            data.Options.Paused = true; var temporary = moved.Copy(); temporary.Windows[0].Rect.X = 800;
            learn.Observe(temporary, now.AddSeconds(20)); learn.Observe(temporary, now.AddSeconds(30)); Check("暂停冻结学习", data.Memories[0].Windows[0].Rect.X == 250);
            data.Options.Paused = false; learn.Baseline(temporary, now.AddSeconds(31)); learn.Observe(temporary, now.AddSeconds(40)); Check("继续后临时位置不直接覆盖", data.Memories[0].Windows[0].Rect.X == 250);
            learn.Hold(now.AddSeconds(100)); learn.Observe(snapshot, now.AddSeconds(50)); learn.Observe(snapshot, now.AddSeconds(80)); Check("显示切换保护历史", data.Memories[0].Windows[0].Rect.X == 250);
            var deskB = Snap(a.Copy()); deskB.DesktopId = "desk-b"; deskB.Windows[0].DesktopId = "desk-b"; learn.Commit(deskB); Check("同屏不同桌面的历史独立", data.Memories.Count == 2);
            var store = new DataStore(Path.Combine(root, "persistence")); store.Save(data); data.Options.Paused = true; store.Save(data);
            var loaded = new DataStore(store.DirectoryPath).Load(); Check("布局与设置跨重启读取", loaded.Layouts.Count == 1 && loaded.Options.Paused && loaded.Habits.Count > 0);
            File.WriteAllText(store.FilePath, "broken XML"); var recoveredStore = new DataStore(store.DirectoryPath); var recovered = recoveredStore.Load(); Check("主文件损坏时读取备份", recovered.Layouts.Count == 1 && recoveredStore.Warning.StartsWith("主数据")); recoveredStore.Save(recovered); Check("修复写入保留可读数据", new DataStore(store.DirectoryPath).Load().Layouts.Count == 1);
            var futureStore = new DataStore(Path.Combine(root, "future-version")); var futureData = new AppData { Version = 2 }; futureStore.Save(futureData);
            var futureReader = new DataStore(futureStore.DirectoryPath); futureReader.Load(); bool refused = false;
            try { futureReader.Save(new AppData()); } catch (InvalidOperationException) { refused = true; }
            Check("未知数据版本禁止覆盖", futureReader.ReadOnly && refused && File.ReadAllText(futureStore.FilePath).Contains("<Version>2</Version>"));
            var newlyOpened = W(300, "a"); var preference = service.NewWindowTarget(snapshot, new[] { newlyOpened });
            Check("新窗口取最近稳定位置", preference != null && preference.Windows[0].Rect.X == 250 && preference.Windows[0].Handle == 300);
            uint mods, key; Check("快捷键解析", Hotkey.Parse("Ctrl+Alt+R", out mods, out key) && mods == 3 && key == 'R');
            Check("快捷键修饰键顺序归一", Hotkey.Parse("Alt+Ctrl+R", out mods, out key) && mods == 3 && key == 'R');
            Check("无修饰键被拒绝", !Hotkey.Parse("R", out mods, out key)); Check("快捷键可关闭", Hotkey.Parse("", out mods, out key) && key == 0);
            Check("按程序名排除忽略大小写", AppRules.Excludes("C:\\apps\\Editor.exe", "Editor", new[] { "EDITOR.exe" }));
            Check("完整路径排除不影响同名程序", !AppRules.Excludes("D:\\other\\Editor.exe", "Editor", new[] { "C:\\apps\\Editor.exe" }));
            fake.Current = moved; data.Options.Paused = false;
            var report = await service.RestoreAsync(named, true, true, "fake"); Check("读回核验成功", report.SuccessCount == 1 && fake.Current.Windows[0].Rect.X == 100);
            var undo = await service.UndoAsync(); Check("一级撤销恢复原位置", undo.SuccessCount == 1 && fake.Current.Windows[0].Rect.X == 250 && data.Undo == null);
            fake.Error = "权限不足"; report = await service.RestoreAsync(named, true, true, "fake"); Check("权限失败不报告成功", report.SuccessCount == 0 && report.Items[0].Detail.Contains("权限"));
            fake.Error = ""; fake.IgnoreApply = true; fake.Current.Windows[0].Rect.X = 600;
            report = await service.RestoreAsync(named, false, true, "fake"); Check("API 请求成功但不生效不算成功", report.SuccessCount == 0 && report.Items[0].Status == "未确认");
            fake.IgnoreApply = false; data.Options.Excluded.Add("test.exe"); report = await service.RestoreAsync(named, true, true, "fake"); Check("手动恢复遵守应用排除", report.SuccessCount == 0 && report.Items[0].Status == "已排除");
            data.Options.Excluded.Clear(); var full = snapshot.Copy(); full.Windows[0].Fullscreen = true; learn.Commit(full); Check("全屏摆放不进入习惯记录", data.Memories.First(m => m.Context == full.Context).Windows.Count == 0);
        }
        private static void RevisionTests()
        {
            var data = new AppData(); var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 121; i++) ReportHistory.Add(data, new RestoreReport { At = epoch.AddMinutes(i), LayoutName = "布局 " + i });
            Check("恢复记录最多保留 100 条", data.Reports.Count == 100);
            Check("超过上限删除最旧记录", data.Reports.First().LayoutName == "布局 120" && data.Reports.Last().LayoutName == "布局 21");
            var success = new RestoreReport { LayoutName = "写作", Trigger = "一键恢复", Items = new List<RestoreItem> { new RestoreItem { App = "Editor", Title = "章节 A", Success = true } } };
            var partial = new RestoreReport { LayoutName = "写作", Trigger = "系统唤醒", Items = new List<RestoreItem> { new RestoreItem { App = "Editor", Success = true }, new RestoreItem { App = "Browser", Title = "参考资料", Detail = "权限不足" } } };
            var failed = new RestoreReport { LayoutName = "娱乐", Trigger = "启动缺失软件", Items = new List<RestoreItem> { new RestoreItem { App = "Player", Detail = "路径失效" } } };
            var reports = new[] { success, partial, failed };
            Check("恢复结果区分成功部分成功与失败", success.Outcome == "全部成功" && partial.Outcome == "部分成功" && failed.Outcome == "未成功");
            Check("旧记录自动获得触发分类", success.TriggerGroup == "手动恢复" && partial.TriggerGroup == "自动恢复" && failed.TriggerGroup == "启动软件");
            Check("关键词检索应用忽略大小写", ReportHistory.Filter(reports, "editor", "全部结果", "全部方式").Count == 2);
            Check("分类与关键词共同筛选", ReportHistory.Filter(reports, "写作", "部分成功", "自动恢复").Single() == partial);
            Check("检索窗口标题及错误说明", ReportHistory.Filter(reports, "章节 A", "", "").Single() == success && ReportHistory.Filter(reports, "权限不足", "", "").Single() == partial);
            Check("分类检索无匹配返回空列表", ReportHistory.Filter(reports, "", "全部成功", "自动恢复").Count == 0);
            var imported = new AppData(); imported.Layouts.Add(Snap(W(1, "旧方案")));
            for (int i = 0; i < 135; i++) imported.Reports.Add(new RestoreReport { At = epoch.AddMinutes(i) });
            var storage = new DataStore(Path.Combine(root, "legacy-report-limit")); Directory.CreateDirectory(storage.DirectoryPath);
            using (var file = File.Create(storage.FilePath)) new System.Xml.Serialization.XmlSerializer(typeof(AppData)).Serialize(file, imported);
            var loaded = storage.Load(); Check("旧数据兼容加载并裁剪超额记录", loaded.Version == 1 && loaded.Layouts.Count == 1 && loaded.Reports.Count == 100 && loaded.Reports.Last().At == epoch.AddMinutes(35));
            var a = W(1, "应用 A 一"); a.Path = "C:\\apps\\a.exe"; a.App = "App A";
            var a2 = a.Copy(); a2.Handle = 2; a2.Title = "应用 A 二";
            var b = W(3, "应用 B"); b.Path = "C:\\apps\\b.exe"; b.App = "App B";
            var lost = W(4, "失效"); lost.Path = "C:\\apps\\lost.exe";
            var excluded = W(5, "排除"); excluded.Path = "C:\\apps\\excluded.exe"; excluded.App = "Excluded";
            var layout = Snap(a, a2, b, lost, excluded); var live = Snap(b.Copy());
            var choices = LaunchSelection.Choices(layout, live, new AppOptions { Excluded = new List<string> { "excluded.exe" } }, p => !p.EndsWith("lost.exe"));
            Check("启动列表同一软件合并为一项", choices.Count == 4 && choices.First(c => c.Path == a.Path).Windows.Count == 2);
            Check("已打开软件标记为复用现有窗口", choices.First(c => c.Path == b.Path).IsOpen && choices.First(c => c.Path == b.Path).CanSelect);
            Check("无效路径与排除应用不可启动", !choices.First(c => c.Path == lost.Path).CanSelect && !choices.First(c => c.Path == excluded.Path).CanSelect);
            var subset = LaunchSelection.Subset(layout, new[] { "c:\\APPS\\A.exe" });
            Check("自选启动只恢复选中软件", subset.Windows.Count == 2 && subset.Windows.All(w => w.Path == a.Path));
            Check("自选启动不修改原方案", layout.Windows.Count == 5 && subset.Windows[0] != layout.Windows[0]);
            Check("未勾选软件不会默认启动全部", LaunchSelection.Subset(layout, new string[0]).Windows.Count == 0);
        }
        private sealed class FakeSystem : IWindowSystem
        {
            public LayoutSnapshot Current; public string Error = ""; public bool IgnoreApply;
            public FakeSystem(LayoutSnapshot s) { Current = s.Copy(); }
            public LayoutSnapshot Capture(AppOptions o) { return Current.Copy(); }
            public bool IsCurrent(WindowRecord w) { return true; }
            public string Apply(WindowRecord live, WindowRecord saved, PixelRect rect, DisplayRecord display)
            { if (Error.Length != 0) return Error; if (!IgnoreApply) { var w = Current.Windows.First(x => x.Handle == live.Handle); w.Rect = rect.Copy(); w.State = saved.State; } return ""; }
            public bool Verify(WindowRecord live, WindowRecord saved, PixelRect rect, DisplayRecord display)
            { var w = Current.Windows.First(x => x.Handle == live.Handle); return w.Rect.Near(rect, 4) && w.State == saved.State; }
            public void Order(IList<WindowRecord> windows) { for (int i = 0; i < windows.Count; i++) Current.Windows.First(w => w.Handle == windows[i].Handle).Z = i; }
        }
        private sealed class Child : IDisposable
        {
            public string Folder; public Process Process;
            public Child(string name)
            {
                Folder = Path.Combine(root, name + Guid.NewGuid().ToString("N").Substring(0, 6)); Directory.CreateDirectory(Folder);
                Process = Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WindowHome.Fixture.exe"), "\"" + Folder + "\" " + name) { UseShellExecute = false });
                Wait(() => File.Exists(Path.Combine(Folder, "ready.txt")), 8000);
            }
            public void Command(string command)
            {
                string serial = DateTime.UtcNow.Ticks.ToString(); File.WriteAllText(Path.Combine(Folder, "cmd.txt"), serial + "|" + command);
                Wait(() => { try { return File.ReadAllText(Path.Combine(Folder, "ack.txt")) == serial; } catch (IOException) { return false; } }, 4000);
                Thread.Sleep(150);
            }
            public void Dispose() { try { if (!Process.HasExited) { Process.Kill(); Process.WaitForExit(3000); } Process.Dispose(); } catch { } }
        }
        private static void Wait(Func<bool> ready, int ms)
        { var watch = Stopwatch.StartNew(); while (!ready()) { if (watch.ElapsedMilliseconds > ms) throw new TimeoutException("受控窗口等待超时"); Thread.Sleep(60); } }
        private static async Task NativeTests()
        {
            using (var alpha = new Child("Alpha")) using (var beta = new Child("Beta"))
            {
                alpha.Command("move|100|120|650|420"); beta.Command("move|500|350|700|450");
                var system = new WindowsSystem { OnlyProcesses = new HashSet<int> { alpha.Process.Id, beta.Process.Id } };
                var data = new AppData(); var service = new LayoutService(data, system); var saved = system.Capture(data.Options); saved.Name = "真实窗口布局";
                Check("真实枚举仅限两个受控窗口", saved.Windows.Count == 2, "windows=" + saved.Windows.Count + "; desktop=" + saved.DesktopId);
                if (saved.Windows.Count != 2 || String.IsNullOrEmpty(saved.DesktopId)) throw new InvalidOperationException("无法获得受控窗口或当前桌面标识");
                alpha.Command("move|700|100|500|320"); beta.Command("move|200|500|520|340");
                var before = system.Capture(data.Options); var result = await service.RestoreAsync(saved, true, true, "native");
                Check("真实双窗口位置与尺寸恢复", result.SuccessCount == 2, String.Join("; ", result.Items.Select(x => x.Status + ": " + x.Detail)));
                var actual = system.Capture(data.Options); var ordered = Matcher.Match(saved.Windows, actual.Windows, false);
                Check("真实遮挡顺序", ordered.All(m => m.Live != null) && ordered.OrderBy(m => m.Saved.Z).Select(m => m.Live.Z).SequenceEqual(ordered.Select(m => m.Live.Z).OrderBy(z => z)));
                result = await service.UndoAsync(); actual = system.Capture(data.Options);
                Check("真实撤销恢复前位置", result.SuccessCount == 2 && Matcher.Match(before.Windows, actual.Windows, false).All(m => m.Live != null && m.Live.Rect.Near(m.Saved.Rect, 4)));
                alpha.Command("max"); beta.Command("min"); var states = system.Capture(data.Options); states.Name = "窗口状态";
                alpha.Command("normal"); beta.Command("normal"); alpha.Command("move|350|200|600|360"); beta.Command("move|800|450|550|320");
                var foreground = Native.GetForegroundWindow(); result = await service.RestoreAsync(states, false, true, "native");
                Check("真实最大化和最小化状态恢复", result.SuccessCount == 2, String.Join("; ", result.Items.Select(x => x.Status + ": " + x.Detail)));
                Check("自动恢复不夺取前台", Native.GetForegroundWindow() == foreground);
                alpha.Command("max"); alpha.Command("min"); var minimizedMax = system.Capture(data.Options);
                minimizedMax.Name = "最小化前为最大化"; minimizedMax.Windows = minimizedMax.Windows.Where(w => w.Pid == alpha.Process.Id).ToList();
                alpha.Command("normal"); alpha.Command("move|350|250|600|380");
                result = await service.RestoreAsync(minimizedMax, false, true, "native"); Check("保留最小化前的最大化标记", result.SuccessCount == 1, String.Join("; ", result.Items.Select(x => x.Detail)));
                Native.ShowWindowAsync(new IntPtr(minimizedMax.Windows[0].Handle), 9); await Task.Delay(200);
                Check("从最小化展开回到最大化", system.Capture(data.Options).Windows.First(w => w.Pid == alpha.Process.Id).State == 3);
                alpha.Command("normal"); beta.Command("normal"); alpha.Command("title|窗归受控测试 · 新标题");
                result = await service.RestoreAsync(saved, false, true, "native"); Check("真实标题改变后仍识别同一窗口", result.SuccessCount == 2);
                var betaSaved = saved.Copy(); betaSaved.Windows = saved.Windows.Where(w => w.Pid == beta.Process.Id).ToList(); beta.Command("close"); beta.Process.WaitForExit(3000);
                using (var replacement = new Child("Beta"))
                {
                    system.OnlyProcesses.Add(replacement.Process.Id); replacement.Command("move|900|100|500|300");
                    result = await service.RestoreAsync(betaSaved, false, true, "native"); Check("真实进程重启后按身份匹配", result.SuccessCount == 1, String.Join("; ", result.Items.Select(x => x.Detail)));
                    replacement.Command("hang"); Thread.Sleep(5500); var watch = Stopwatch.StartNew();
                    result = await service.RestoreAsync(betaSaved, false, true, "native"); Check("真实无响应窗口不会卡住恢复", watch.ElapsedMilliseconds < 5000 && result.SuccessCount == 0, watch.ElapsedMilliseconds + " ms; " + String.Join("; ", result.Items.Select(x => x.Detail)));
                }
                result = await service.RestoreAsync(betaSaved, false, true, "native"); Check("真实已关闭窗口不报告成功", result.SuccessCount == 0);
                Check("原生测试始终限制进程集合", system.OnlyProcesses.Count == 3 && system.OnlyProcesses.Contains(alpha.Process.Id) && system.OnlyProcesses.Contains(beta.Process.Id) && !system.OnlyProcesses.Contains(Process.GetCurrentProcess().Id));
            }
        }
    }
}
