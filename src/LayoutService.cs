using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WindowHome
{
    public sealed class LearningService
    {
        private readonly AppData data;
        private string observed = "", committed = "";
        private DateTime changedAt, holdUntil;
        public LearningService(AppData data) { this.data = data; }
        public void Hold(DateTime until) { holdUntil = until; observed = ""; }
        public void Baseline(LayoutSnapshot current, DateTime now)
        {
            observed = Geometry.Signature(current); committed = observed; changedAt = now;
        }
        public bool Observe(LayoutSnapshot current, DateTime now)
        {
            if (data.Options.Paused || !data.Options.Learning || now < holdUntil || String.IsNullOrEmpty(current.DesktopId)) return false;
            string signature = Geometry.Signature(current);
            if (signature != observed) { observed = signature; changedAt = now; return false; }
            if ((now - changedAt).TotalSeconds < 5 || signature == committed) return false;
            Commit(current); committed = signature; return true;
        }
        public void Commit(LayoutSnapshot current)
        {
            var snapshot = current.Copy(); snapshot.Name = "自动记忆"; snapshot.Updated = DateTime.UtcNow;
            snapshot.Windows = snapshot.Windows.Where(w => !w.Fullscreen && !String.IsNullOrEmpty(w.DesktopId)).ToList();
            data.Memories.RemoveAll(x => x.Context == snapshot.Context); data.Memories.Add(snapshot);
            var past = data.Habits.Where(h => h.Context == current.Context).ToList();
            var matches = Matcher.Match(past.Select(h => h.Window).ToList(), snapshot.Windows, false);
            var used = new HashSet<long>();
            foreach (var match in matches.Where(m => m.Live != null))
            {
                var habit = past.First(h => Object.ReferenceEquals(h.Window, match.Saved));
                habit.Window = match.Live.Copy(); habit.Window.Updated = snapshot.Updated; used.Add(match.Live.Handle);
            }
            foreach (var window in snapshot.Windows.Where(w => !used.Contains(w.Handle)))
                data.Habits.Add(new HabitRecord { Context = current.Context, Window = window.Copy() });
            // Keep bounded, most recently learned history; named layouts are never pruned.
            if (data.Habits.Count > 1000) data.Habits = data.Habits.OrderByDescending(h => h.Window.Updated).Take(1000).ToList();
        }
    }
    public sealed class LayoutService
    {
        private readonly AppData data;
        private readonly IWindowSystem system;
        public bool Busy { get; private set; }
        public LayoutService(AppData data, IWindowSystem system) { this.data = data; this.system = system; }
        public Task<LayoutSnapshot> CaptureAsync() { return Task.Run(() => system.Capture(data.Options)); }
        public LayoutSnapshot Target(LayoutSnapshot current)
        {
            var pick = data.Defaults.FirstOrDefault(d => d.Context == current.Context);
            return (pick == null ? null : data.Layouts.FirstOrDefault(l => l.Id == pick.LayoutId)) ?? data.Memories.FirstOrDefault(l => l.Context == current.Context);
        }
        public LayoutSnapshot SaveNamed(LayoutSnapshot current, string name)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentException("请输入布局名称。");
            var layout = current.Copy(); layout.Id = Guid.NewGuid().ToString("N"); layout.Name = name.Trim(); layout.Updated = DateTime.UtcNow;
            layout.Windows = layout.Windows.Where(w => !w.Fullscreen && !String.IsNullOrEmpty(w.DesktopId)).ToList();
            if (layout.Windows.Count == 0) throw new InvalidOperationException("当前没有可记录的普通窗口。请先打开软件并退出全屏。");
            data.Layouts.Add(layout); return layout;
        }
        public void SetDefault(LayoutSnapshot layout, string context)
        {
            data.Defaults.RemoveAll(d => d.Context == context);
            data.Defaults.Add(new DefaultLayout { Context = context, LayoutId = layout.Id });
        }
        private bool Excluded(WindowRecord w)
        {
            return AppRules.Excludes(w.Path, w.App, data.Options.Excluded);
        }
        private sealed class Pending
        {
            public WindowRecord Saved, Live;
            public PixelRect Rect;
            public DisplayRecord Display;
            public RestoreItem Item;
            public bool Done;
        }
        public async Task<RestoreReport> RestoreAsync(LayoutSnapshot requested, bool manual, bool takeUndo, string trigger)
        {
            if (Busy) throw new InvalidOperationException("正在恢复，请等待本次操作完成。");
            if (requested == null) throw new InvalidOperationException("还没有可恢复的布局。请先记住当前布局。");
            Busy = true;
            var report = new RestoreReport { LayoutName = requested.Name, Trigger = trigger };
            try
            {
                var live = await CaptureAsync(); var before = live.Copy(); before.Name = "恢复前的位置";
                if (live.Displays.Count == 0) throw new InvalidOperationException("未检测到显示器。");
                var eligible = requested.Windows.Where(w => !Excluded(w)).ToList();
                foreach (var w in requested.Windows.Where(Excluded)) report.Items.Add(new RestoreItem { App = w.App, Title = w.Title, Status = "已排除", Detail = "此应用已在设置中排除。" });
                var matches = Matcher.Match(eligible, live.Windows, manual);
                var pending = new List<Pending>();
                foreach (var match in matches)
                {
                    var item = new RestoreItem { App = match.Saved.App, Title = match.Saved.Title };
                    report.Items.Add(item);
                    if (match.Live == null) { item.Status = match.Reason.Contains("多个") ? "匹配不确定" : "窗口未打开"; item.Detail = match.Reason; continue; }
                    var mapped = Geometry.Map(match.Saved, live.Displays);
                    item.Target = mapped;
                    pending.Add(new Pending { Saved = match.Saved, Live = match.Live, Rect = mapped,
                        Display = Geometry.TargetDisplay(match.Saved, live.Displays), Item = item });
                }
                var clock = Stopwatch.StartNew();
                for (int round = 0; round < 3 && clock.ElapsedMilliseconds < 5000 && pending.Any(p => !p.Done); round++)
                {
                    await Task.Run(() =>
                    {
                        foreach (var p in pending.Where(p => !p.Done))
                        {
                            if (clock.ElapsedMilliseconds >= 5000) break;
                            string error = system.Apply(p.Live, p.Saved, p.Rect, p.Display);
                            if (error.Length != 0) { p.Done = true; p.Item.Status = "未恢复"; p.Item.Detail = error; }
                        }
                    });
                    if (pending.All(p => p.Done)) break;
                    await Task.Delay(round == 0 ? 250 : round == 1 ? 450 : 650);
                    await Task.Run(() =>
                    {
                        foreach (var p in pending.Where(p => !p.Done))
                        {
                            if (system.Verify(p.Live, p.Saved, p.Rect, p.Display))
                            {
                                p.Done = true; p.Item.Success = true; p.Item.Status = "已核验";
                                p.Item.Detail = p.Rect + (requested.Topology != live.Topology ? "；已适配当前显示器配置。" : "；位置、大小和窗口状态符合目标。");
                            }
                        }
                    });
                }
                foreach (var p in pending.Where(p => !p.Done))
                {
                    p.Item.Status = "未确认";
                    p.Item.Detail = "已发送恢复请求，但读回结果未达到目标。应用可能限制尺寸、无响应、已关闭或切换了桌面。";
                }
                if (manual && pending.Count(p => p.Item.Success) > 1)
                {
                    var ordered = pending.Where(p => p.Item.Success).OrderBy(p => p.Saved.Z).Select(p => p.Live).ToList();
                    await Task.Run(() => system.Order(ordered));
                    await Task.Delay(150);
                    var orderedCapture = await CaptureAsync();
                    var positions = ordered.Select(w => orderedCapture.Windows.FirstOrDefault(x => x.Handle == w.Handle)).Where(w => w != null).ToList();
                    if (positions.Count == ordered.Count && positions.Select(w => w.Topmost).Distinct().Count() <= 1 && !positions.Select(w => w.Z).SequenceEqual(positions.Select(w => w.Z).OrderBy(z => z)))
                        foreach (var p in pending.Where(p => p.Item.Success)) p.Item.Detail += " 遮挡顺序未完全确认。";
                }
                if (takeUndo && pending.Any(p => p.Item.Success))
                {
                    var touched = new HashSet<long>(pending.Where(p => p.Item.Success).Select(p => p.Live.Handle));
                    before.Windows = before.Windows.Where(w => touched.Contains(w.Handle)).ToList(); data.Undo = before;
                }
                ReportHistory.Add(data, report);
                return report;
            }
            finally { Busy = false; }
        }
        public async Task<RestoreReport> UndoAsync()
        {
            if (data.Undo == null) throw new InvalidOperationException("还没有可撤销的恢复。");
            var old = data.Undo;
            var r = await RestoreAsync(old, true, false, "撤销");
            if (r.SuccessCount == old.Windows.Count) data.Undo = null;
            return r;
        }
        public LayoutSnapshot NewWindowTarget(LayoutSnapshot current, IList<WindowRecord> newlyOpened)
        {
            var habits = data.Habits.Where(h => h.Context == current.Context).Select(h => h.Window).ToList();
            var matches = Matcher.Match(habits, newlyOpened, false).Where(m => m.Live != null).ToList();
            if (matches.Count == 0) return null;
            var s = current.Copy(); s.Name = "新窗口的最近位置"; s.Windows = matches.Select(m => m.Saved.Copy()).ToList();
            // Bind each selected preference to this exact new instance before restoring.
            for (int i = 0; i < matches.Count; i++) { s.Windows[i].Handle = matches[i].Live.Handle; s.Windows[i].Pid = matches[i].Live.Pid; s.Windows[i].ProcessStarted = matches[i].Live.ProcessStarted; }
            return s;
        }
    }
}
