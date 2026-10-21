using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("窗归 WindowHome")]
[assembly: AssemblyDescription("本地窗口布局记忆与核验恢复工具")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("WindowHome.Tests")]

namespace WindowHome
{
    public static class Program
    {
        [STAThread] public static int Main(string[] args)
        {
            WindowsSystem.EnableDpi();
            string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowHome");
            bool isolated = false;
            if (args.Contains("--portable")) folder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            int di = Array.IndexOf(args, "--data-dir");
            if (di >= 0)
            {
                if (di + 1 >= args.Length) { MessageBox.Show("--data-dir 后需要指定数据目录。", "窗归"); return 2; }
                folder = System.IO.Path.GetFullPath(args[di + 1]); isolated = true;
            }
            HashSet<int> testProcesses = null;
            int ti = Array.IndexOf(args, "--test-processes");
            if (ti >= 0)
            {
                if (!isolated || ti + 1 >= args.Length) { MessageBox.Show("--test-processes 仅供 --data-dir 独立测试使用。", "窗归"); return 2; }
                try { testProcesses = new HashSet<int>(args[ti + 1].Split(',').Select(Int32.Parse)); }
                catch { MessageBox.Show("测试进程参数无效。", "窗归"); return 2; }
            }
            string key = Geometry.Topology(new[] { new DisplayRecord { Id = folder.ToLowerInvariant() } });
            bool created;
            using (var mutex = new Mutex(true, "Local\\WindowHome-" + key, out created))
            {
                uint showMessage = Native.RegisterWindowMessage("WindowHome.Show." + key);
                if (!created) { Native.PostMessage(new IntPtr(0xFFFF), showMessage, IntPtr.Zero, IntPtr.Zero); return 0; }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    var controller = new MainController(app, folder, isolated, args.Contains("--background"), showMessage, testProcesses);
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                    { controller.Error(e.Exception); e.Handled = true; };
                    app.Run(); controller.Dispose();
                }
                catch (Exception ex)
                {
                    try { Directory.CreateDirectory(folder); File.WriteAllText(System.IO.Path.Combine(folder, "startup-error.txt"), ex.ToString()); } catch { }
                    MessageBox.Show("窗归启动失败：\n" + ex.GetBaseException().Message + "\n\n诊断数据目录：" + folder, "窗归", MessageBoxButton.OK, MessageBoxImage.Error); return 1;
                }
                return 0;
            }
        }
    }
    internal sealed class MainController : IDisposable
    {
        private readonly Application app;
        private readonly Window window;
        private readonly DataStore store;
        private readonly AppData data;
        private readonly LayoutService service;
        private readonly LearningService learning;
        private readonly DispatcherTimer timer;
        private readonly Forms.NotifyIcon tray;
        private readonly bool isolated;
        private readonly uint showMessage;
        private HwndSource source;
        private Native.WinEventProc moveCallback;
        private IntPtr moveHook;
        private bool exiting, tickBusy, actionBusy, suspended, locked, firstClose = true, pendingAuto;
        private bool hasBaseline;
        private DateTime transitionUntil;
        private string lastTopology = "", lastDesktop = "", transitionReason = "";
        private readonly NewWindowTracker newWindows = new NewWindowTracker();
        private LayoutSnapshot current;
        private T Find<T>(string name) where T : class { return window.FindName(name) as T; }
        public MainController(Application app, string path, bool isolated, bool background, uint showMessage, HashSet<int> testProcesses)
        {
            this.app = app; this.isolated = isolated; this.showMessage = showMessage;
            store = new DataStore(path); data = store.Load();
            service = new LayoutService(data, new WindowsSystem { OnlyProcesses = testProcesses }); learning = new LearningService(data);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WindowHome.Main.xaml")) window = (Window)XamlReader.Load(stream);
            app.MainWindow = window;
            tray = new Forms.NotifyIcon { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location), Text = "窗归 · 自动管理", Visible = true };
            tray.MouseClick += delegate(object s, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) Show(); };
            window.Closing += Closing;
            window.SourceInitialized += delegate
            {
                source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle); source.AddHook(MessageHook);
                RegisterKeys();
                moveCallback = delegate(IntPtr h, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time)
                {
                    if (!service.Busy && !data.Options.Paused && DateTime.UtcNow >= transitionUntil && !actionBusy)
                        window.Dispatcher.BeginInvoke(new Action(async delegate { await Poll(); }));
                };
                moveHook = Native.SetWinEventHook(0x000B, 0x000B, IntPtr.Zero, moveCallback, 0, 0, 2);
            };
            SystemEvents.SessionSwitch += SessionSwitch;
            BindButtons(); LoadSettings(); RefreshSavedLists(null); Navigate(0);
            Find<TextBlock>("DataPathLabel").Text = "数据目录：" + store.DirectoryPath + (isolated ? "\n独立数据模式：已禁用开机启动设置。" : "") + (testProcesses != null ? "\n受控测试模式：仅管理指定测试进程。" : "");
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += async delegate { await Poll(); };
            window.Loaded += async delegate
            {
                if (background) window.Hide();
                await Poll(); timer.Start();
                if (!String.IsNullOrEmpty(store.Warning)) Message(store.Warning);
            };
            window.SizeChanged += delegate { if (current != null) Draw(Find<Canvas>("DesktopCanvas"), current); RenderLayout(); };
            Find<Canvas>("DesktopCanvas").SizeChanged += delegate { Draw(Find<Canvas>("DesktopCanvas"), current); };
            Find<Canvas>("LayoutCanvas").SizeChanged += delegate { Draw(Find<Canvas>("LayoutCanvas"), SelectedLayout()); };
            window.Show();
        }
        private void OnClick(string name, Func<Task> action)
        {
            Find<Button>(name).Click += async delegate
            {
                if (actionBusy || service.Busy) { Message("正在处理，请稍候。"); return; }
                actionBusy = true; RefreshStatus();
                try { await action(); } catch (Exception ex) { Error(ex); }
                finally { actionBusy = false; RefreshStatus(); }
            };
        }
        private void BindButtons()
        {
            string[] nav = { "NavLayouts", "NavReports", "NavSettings" };
            for (int i = 0; i < nav.Length; i++) { int page = i; Find<Button>(nav[i]).Click += delegate { Navigate(page); }; }
            OnClick("PauseButton", async delegate
            {
                data.Options.Paused = !data.Options.Paused;
                if (current != null) { current = await service.CaptureAsync(); RenderCurrent(); learning.Baseline(current, DateTime.UtcNow); ResetKnown(current); }
                pendingAuto = false; Persist();
                Message(data.Options.Paused ? "已暂停学习和自动恢复；手动保存、恢复仍可使用。" : "已恢复自动管理。现有窗口保持原位，下一次调整后开始学习。"); await Task.FromResult(0);
            });
            OnClick("RestoreButton", async delegate { await Restore(service.Target(current), true, "一键恢复"); });
            OnClick("SaveButton", SaveNamed);
            OnClick("CurrentDesktopButton", async delegate { Find<ListBox>("LayoutList").SelectedIndex = -1; RenderLayout(); await Task.FromResult(0); });
            OnClick("UndoButton", async delegate
            {
                learning.Hold(DateTime.UtcNow.AddSeconds(8)); var r = await service.UndoAsync(); await Finished(r);
            });
            OnClick("RefreshButton", async delegate { await RefreshCurrent(); });
            OnClick("ExcludeButton", async delegate
            {
                var selected = Find<DataGrid>("WindowGrid").SelectedItem as WindowRecord;
                if (selected == null) { Message("先从窗口列表选择一个应用。"); return; }
                if (!data.Options.Excluded.Contains(selected.App + ".exe")) data.Options.Excluded.Add(selected.App + ".exe");
                LoadSettings(); Persist(); Message("已排除 " + selected.App + "。可在偏好设置中移除排除规则。"); await RefreshCurrent();
            });
            OnClick("RestoreLayoutButton", async delegate { await Restore(SelectedLayout(), true, "手动布局"); });
            OnClick("DefaultButton", async delegate
            {
                var layout = RequireLayout(); if (current == null || String.IsNullOrEmpty(current.DesktopId)) throw new InvalidOperationException("无法确认当前桌面，请激活一个普通应用后重试。");
                service.SetDefault(layout, current.Context); Persist(); RenderLayout(); RefreshStatus(); Message("当前显示器组合的默认布局：" + layout.Name); await Task.FromResult(0);
            });
            OnClick("UpdateLayoutButton", async delegate
            {
                RequireLayout(); await SaveNamed();
            });
            OnClick("RenameButton", async delegate
            {
                var layout = RequireLayout(); bool makeDefault;
                string name = NameDialog("重命名布局", layout.Name, false, out makeDefault);
                if (name != null) { layout.Name = name; Persist(); RefreshSavedLists(layout.Id); Message("布局已重命名。"); }
                await Task.FromResult(0);
            });
            OnClick("DeleteLayoutButton", async delegate
            {
                var layout = RequireLayout();
                if (MessageBox.Show(window, "删除布局“" + layout.Name + "”？自动记忆和其他布局会保留。", "删除布局", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                { data.Layouts.Remove(layout); data.Defaults.RemoveAll(d => d.LayoutId == layout.Id); Persist(); RefreshSavedLists(null); Message("布局已删除。"); }
                await Task.FromResult(0);
            });
            OnClick("LaunchButton", LaunchMissing);
            OnClick("ApplySettingsButton", async delegate { ApplySettings(); await RefreshCurrent(); });
            OnClick("OpenDataButton", async delegate { Directory.CreateDirectory(store.DirectoryPath); Process.Start("explorer.exe", "\"" + store.DirectoryPath + "\""); await Task.FromResult(0); });
            Find<ListBox>("LayoutList").SelectionChanged += delegate { RenderLayout(); };
            Find<ListBox>("ReportList").SelectionChanged += delegate { RenderReport(); };
            Find<TextBox>("ReportSearch").TextChanged += delegate { RefreshReports(); };
            Find<ComboBox>("ReportOutcome").SelectionChanged += delegate { RefreshReports(); };
            Find<ComboBox>("ReportTrigger").SelectionChanged += delegate { RefreshReports(); };
        }
        private void Navigate(int page)
        {
            var pages = Find<Grid>("Pages");
            for (int i = 0; i < pages.Children.Count; i++) pages.Children[i].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;
            string[] titles = { "布局", "恢复记录", "设置" };
            Find<TextBlock>("PageTitle").Text = titles[page];
            string[] nav = { "NavLayouts", "NavReports", "NavSettings" };
            for (int i = 0; i < nav.Length; i++)
            {
                Find<Button>(nav[i]).Background = Brush(i == page ? "#254039" : "#142238");
                Find<Button>(nav[i]).Foreground = Brush(i == page ? "#78E0CC" : "#BDCADB");
            }
            if (page == 0) RenderLayout(); if (page == 1) RefreshReports();
        }
        private async Task Poll()
        {
            if (tickBusy || service.Busy || actionBusy || suspended || locked || exiting) return;
            tickBusy = true;
            try
            {
                var snap = await service.CaptureAsync();
                if (service.Busy || actionBusy) return;
                current = snap; RenderCurrent();
                DateTime now = DateTime.UtcNow;
                if (!hasBaseline)
                {
                    hasBaseline = true; ResetKnown(snap);
                    if (data.Memories.Any(m => m.Context == snap.Context)) learning.Baseline(snap, now);
                }
                else if (lastTopology != snap.Topology) Transition("显示器配置变化", data.Options.RestoreDisplay);
                else if (lastDesktop != snap.DesktopId)
                { pendingAuto = false; ResetKnown(snap); learning.Baseline(snap, now); Message("已切换桌面，窗口保持原位，使用独立的自动记忆。"); }
                lastTopology = snap.Topology; lastDesktop = snap.DesktopId;
                if (data.Options.Paused || now < transitionUntil) { ResetKnown(snap); return; }
                if (!data.Options.CanRestore) pendingAuto = false;
                if (pendingAuto)
                {
                    pendingAuto = false;
                    var memory = data.Memories.FirstOrDefault(m => m.Context == snap.Context);
                    if (memory != null && memory.Windows.Count > 0)
                    { await Restore(memory, false, transitionReason); return; }
                    Message("当前显示器与桌面组合尚无历史，将在窗口稳定后开始记录。");
                }
                var ready = newWindows.Observe(snap, now, data.Options.NewWindowDelaySeconds);
                if (data.Options.CanRestore && data.Options.RestoreNew && ready.Count > 0)
                {
                    var preference = service.NewWindowTarget(snap, ready.Where(w => !w.Fullscreen && w.State != 2).ToList());
                    var skipped = ready.Where(w => data.Habits.Any(h => h.Context == snap.Context && h.Window.AppKey == w.AppKey) &&
                        (preference == null || !preference.Windows.Any(p => Matcher.SameInstance(p, w)))).Select(w => new RestoreItem
                        { App = w.App, Title = w.Title, Status = w.Fullscreen ? "全屏已跳过" : w.State == 2 ? "最小化已跳过" : "匹配不确定", Detail = w.Fullscreen ? "请退出全屏后手动恢复。" : w.State == 2 ? "保留新窗口当前的最小化状态。" : "存在该应用的历史，但无法唯一对应新窗口。已保留当前位置，可手动保存新的布局。" }).ToList();
                    if (preference != null) { await Restore(preference, false, "新窗口", skipped); return; }
                    if (skipped.Count > 0)
                    {
                        ReportHistory.Add(data, new RestoreReport { LayoutName = "新窗口的最近位置", Trigger = "新窗口", Items = skipped });
                        Persist(); RefreshSavedLists(null); Message("新窗口没有自动移动，具体原因已写入恢复记录。");
                    }
                }
                // Do not learn a startup window before its delayed restore has finished.
                if (data.Options.CanRestore && data.Options.RestoreNew && newWindows.HasWaiting) return;
                if (learning.Observe(snap, now)) { Persist(); RefreshSavedLists(null); Message("日常布局已更新 · " + now.ToLocalTime().ToString("HH:mm:ss")); }
            }
            catch (Exception ex) { Error(ex); }
            finally { tickBusy = false; }
        }
        private void ResetKnown(LayoutSnapshot snapshot) { newWindows.Reset(snapshot); }
        private void Transition(string reason, bool restoreEnabled)
        {
            transitionUntil = DateTime.UtcNow.AddSeconds(6); learning.Hold(transitionUntil.AddSeconds(2));
            pendingAuto = restoreEnabled && data.Options.CanRestore; transitionReason = reason;
            Message(reason + "，等待配置稳定后" + (pendingAuto ? "恢复最近现场。" : "继续记录。"));
        }
        private async Task RefreshCurrent() { current = await service.CaptureAsync(); RenderCurrent(); }
        private async Task Restore(LayoutSnapshot target, bool manual, string reason, List<RestoreItem> skipped = null)
        {
            if (target == null) throw new InvalidOperationException("尚无可恢复布局。先摆好窗口，再点击“记住当前布局”。");
            learning.Hold(DateTime.UtcNow.AddSeconds(8)); Message("正在恢复“" + target.Name + "”，并逐窗口核验…"); RefreshStatus();
            var r = await service.RestoreAsync(target, manual, true, reason);
            if (skipped != null) r.Items.AddRange(skipped);
            await Finished(r);
        }
        private async Task Finished(RestoreReport report)
        {
            Persist(); await RefreshCurrent(); if (report.Trigger != "新窗口") ResetKnown(current); learning.Baseline(current, DateTime.UtcNow);
            RefreshSavedLists(null);
            Message(report.Items.Count > 0 && report.SuccessCount == report.Items.Count ? "就绪" : "有项目未恢复，详见恢复记录。");
        }
        private async Task SaveNamed()
        {
            await RefreshCurrent();
            var dialog = new Window { Owner = window, Title = "保存布局", Width = 480, Height = 460, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = window.FontFamily, FontSize = 13, Background = Brush("#F4F6FA") };
            var panel = new StackPanel { Margin = new Thickness(24) }; dialog.Content = panel;
            panel.Children.Add(new TextBlock { Text = "保存方式", Margin = new Thickness(0, 0, 0, 7) });
            var mode = new ComboBox { Name = "SaveMode", Padding = new Thickness(9), Margin = new Thickness(0, 0, 0, 14) };
            mode.Items.Add(new ComboBoxItem { Content = "合并到已有布局", IsEnabled = data.Layouts.Count > 0 });
            mode.Items.Add(new ComboBoxItem { Content = "覆盖已有布局", IsEnabled = data.Layouts.Count > 0 });
            mode.Items.Add(new ComboBoxItem { Content = "另存为新布局" }); panel.Children.Add(mode);
            var label = new TextBlock { Text = "目标布局", Margin = new Thickness(0, 0, 0, 7) }; panel.Children.Add(label);
            var targets = new ComboBox { Name = "SaveTarget", ItemsSource = data.Layouts, DisplayMemberPath = "DisplayName", Padding = new Thickness(9) }; panel.Children.Add(targets);
            targets.SelectedItem = SelectedLayout() ?? data.Layouts.FirstOrDefault(l => l.Name == "日常布局" && l.Context == current.Context) ?? data.Layouts.FirstOrDefault();
            var nameBox = new TextBox { Name = "SaveName", Text = data.Layouts.Count == 0 ? "日常布局" : "布局 " + DateTime.Now.ToString("MM-dd HH:mm"), MaxLength = 60 }; panel.Children.Add(nameBox);
            var hint = new TextBlock { Foreground = Brush("#728096"), TextWrapping = TextWrapping.Wrap, MinHeight = 40, Margin = new Thickness(0, 9, 0, 0) }; panel.Children.Add(hint);
            var check = new CheckBox { Content = "设为当前显示器组合的默认布局", IsChecked = !data.Defaults.Any(d => d.Context == current.Context), Margin = new Thickness(0, 8, 0, 0) }; panel.Children.Add(check);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) }; panel.Children.Add(buttons);
            buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(0, 0, 10, 0) });
            var save = new Button { Name = "SaveConfirm", Content = "保存", IsDefault = true, Padding = new Thickness(18, 7, 18, 7) }; buttons.Children.Add(save);
            mode.SelectionChanged += delegate
            {
                bool create = mode.SelectedIndex == 2; label.Text = create ? "新布局名称" : "目标布局";
                targets.Visibility = create ? Visibility.Collapsed : Visibility.Visible; nameBox.Visibility = create ? Visibility.Visible : Visibility.Collapsed;
                hint.Text = mode.SelectedIndex == 0 ? "更新已打开的窗口，保留未打开软件的记录。" : mode.SelectedIndex == 1 ? "覆盖后只保留当前打开的窗口。" : "保存为一套独立布局。";
            };
            mode.SelectedIndex = data.Layouts.Count > 0 ? 0 : 2;
            save.Click += delegate
            {
                if (mode.SelectedIndex == 2 && (String.IsNullOrWhiteSpace(nameBox.Text) || data.Layouts.Any(l => l.Context == current.Context && String.Equals(l.Name, nameBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))))
                { hint.Text = "名称不能为空或重复；同名布局请选择合并或覆盖。"; return; }
                if (mode.SelectedIndex != 2 && targets.SelectedItem == null) return;
                dialog.DialogResult = true;
            };
            if (dialog.ShowDialog() != true) return;
            await RefreshCurrent();
            var existing = mode.SelectedIndex == 2 ? null : (LayoutSnapshot)targets.SelectedItem;
            var layout = service.SaveNamed(current, existing == null ? nameBox.Text.Trim() : existing.Name, existing, mode.SelectedIndex == 0);
            bool makeDefault = check.IsChecked == true;
            if (makeDefault) service.SetDefault(layout, current.Context);
            learning.Baseline(current, DateTime.UtcNow); Persist(); RefreshSavedLists(layout.Id);
            Message("已保存“" + layout.Name + "” · " + layout.Windows.Count + " 个窗口");
        }
        private LayoutSnapshot SelectedLayout() { return Find<ListBox>("LayoutList").SelectedItem as LayoutSnapshot; }
        private LayoutSnapshot RequireLayout() { var s = SelectedLayout(); if (s == null) throw new InvalidOperationException("先选择一套布局。"); return s; }
        private void RefreshSavedLists(string selectedId)
        {
            var list = Find<ListBox>("LayoutList"); string id = selectedId ?? (SelectedLayout() == null ? "" : SelectedLayout().Id);
            list.ItemsSource = null; list.ItemsSource = data.Layouts;
            list.SelectedItem = data.Layouts.FirstOrDefault(l => l.Id == id);
            RefreshStatus(); RenderLayout(); RefreshReports();
        }
        private void RenderCurrent()
        {
            RenderLayout(); RefreshStatus();
        }
        private void RefreshStatus()
        {
            bool busy = actionBusy || service.Busy;
            Find<TextBlock>("StatusLabel").Text = busy ? "● 正在处理" : data.Options.Paused ? "● 自动管理已暂停" : "记忆 " + (data.Options.Learning ? "开" : "关") + " · 恢复 " + (data.Options.AutoRestore ? "开" : "关");
            Find<TextBlock>("StatusLabel").Foreground = Brush(data.Options.Paused ? "#B27A2A" : "#087F73");
            Find<Button>("PauseButton").Content = data.Options.Paused ? "继续" : "暂停";
            var target = current == null ? null : service.Target(current);
            Find<TextBlock>("TargetLabel").Text = target == null ? "尚无恢复目标" : "恢复目标：" + target.Name;
            Find<TextBlock>("TargetHint").Text = target == null ? "" : target.Description;
            Find<Button>("RestoreButton").IsEnabled = !busy && target != null;
            Find<Button>("UndoButton").IsEnabled = !busy && data.Undo != null;
            Find<Button>("LaunchButton").IsEnabled = !busy && data.Layouts.Count > 0;
            Find<TextBlock>("CurrentSummary").Text = (current == null ? 0 : current.Windows.Count) + " 个当前窗口 · " + data.Layouts.Count + " 套布局";
            tray.Text = data.Options.Paused ? "窗归 · 自动管理已暂停" : busy ? "窗归 · 正在处理" : "窗归 · 自动管理";
            RebuildTray(target);
        }
        private void RebuildTray(LayoutSnapshot target)
        {
            if (tray.ContextMenuStrip != null && tray.ContextMenuStrip.Visible) return;
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开窗归", null, delegate { Show(); });
            var restore = menu.Items.Add("一键恢复 · " + (target == null ? "尚无布局" : target.Name), null, async delegate { await TrayAction(async delegate { await Restore(current == null ? null : service.Target(current), true, "托盘"); }); }); restore.Enabled = target != null && !service.Busy && !actionBusy;
            menu.Items.Add("记住当前布局…", null, async delegate { Show(); await TrayAction(SaveNamed); });
            var undo = menu.Items.Add("撤销恢复", null, async delegate { await TrayAction(async delegate { var r = await service.UndoAsync(); await Finished(r); }); }); undo.Enabled = data.Undo != null && !service.Busy && !actionBusy;
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(data.Options.Paused ? "继续自动管理" : "暂停自动管理", null, delegate { Find<Button>("PauseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
            menu.Items.Add("退出窗归", null, delegate { Exit(); });
            var old = tray.ContextMenuStrip; tray.ContextMenuStrip = menu; if (old != null && !old.Visible) old.Dispose();
        }
        private async Task TrayAction(Func<Task> action)
        {
            if (service.Busy || actionBusy) return; actionBusy = true; RefreshStatus();
            try { await action(); } catch (Exception ex) { Error(ex); }
            finally { actionBusy = false; RefreshStatus(); }
        }
        private void RenderLayout()
        {
            var s = SelectedLayout();
            var view = s ?? current;
            string[] buttons = { "RestoreLayoutButton", "DefaultButton", "UpdateLayoutButton", "RenameButton", "DeleteLayoutButton" };
            foreach (string b in buttons) Find<Button>(b).IsEnabled = s != null;
            Find<TextBlock>("LayoutTitle").Text = s == null ? "当前桌面" : s.Name;
            Find<TextBlock>("LayoutInfo").Text = s == null ? "" : s.Description +
                (current != null && data.Defaults.Any(d => d.Context == current.Context && d.LayoutId == s.Id) ? " · 默认" : "") +
                (current != null && s.Topology != current.Topology ? " · 适配当前屏幕" : "");
            Find<WrapPanel>("CurrentActions").Visibility = s == null ? Visibility.Visible : Visibility.Collapsed;
            Find<WrapPanel>("LayoutActions").Visibility = s == null ? Visibility.Collapsed : Visibility.Visible;
            Find<Canvas>("DesktopCanvas").Visibility = s == null ? Visibility.Visible : Visibility.Collapsed;
            Find<Canvas>("LayoutCanvas").Visibility = s == null ? Visibility.Collapsed : Visibility.Visible;
            Find<Button>("CurrentDesktopButton").Background = Brush(s == null ? "#E3F3EE" : "#FFFFFF");
            var grid = Find<DataGrid>("WindowGrid"); var selected = grid.SelectedItem as WindowRecord;
            grid.ItemsSource = view == null ? null : view.Windows;
            if (selected != null && view != null) grid.SelectedItem = view.Windows.FirstOrDefault(w => w.Handle == selected.Handle);
            Find<TextBlock>("EmptyLabel").Visibility = view == null || view.Windows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Find<TextBlock>("DisplayLabel").Text = view == null ? "" : String.Join(" / ", view.Displays.Select(d => d.Caption));
            Draw(Find<Canvas>("DesktopCanvas"), current);
            Draw(Find<Canvas>("LayoutCanvas"), s);
        }
        private static string SelectedText(ComboBox box) { var item = box.SelectedItem as ComboBoxItem; return item == null ? "" : Convert.ToString(item.Content); }
        private void RefreshReports()
        {
            var list = Find<ListBox>("ReportList"); var previous = list.SelectedItem as RestoreReport;
            var reports = ReportHistory.Filter(data.Reports, Find<TextBox>("ReportSearch").Text,
                SelectedText(Find<ComboBox>("ReportOutcome")), SelectedText(Find<ComboBox>("ReportTrigger")));
            list.ItemsSource = reports; list.SelectedItem = previous != null && reports.Contains(previous) ? previous : reports.FirstOrDefault();
            Find<TextBlock>("ReportCount").Text = reports.Count + " / " + data.Reports.Count + " 条（上限 100）";
            RenderReport();
        }
        private void RenderReport()
        {
            var report = Find<ListBox>("ReportList").SelectedItem as RestoreReport;
            Find<TextBlock>("ReportSummary").Text = report == null ? "无匹配记录" : report.Summary + " · " + report.Trigger;
            Find<DataGrid>("ReportGrid").ItemsSource = report == null ? null : report.Items;
        }
        private static Brush Brush(string color) { return (Brush)new BrushConverter().ConvertFromString(color); }
        private static void Draw(Canvas canvas, LayoutSnapshot snapshot)
        {
            canvas.Children.Clear(); if (snapshot == null || snapshot.Displays.Count == 0 || canvas.ActualWidth <= 10) return;
            var ds = snapshot.Displays; int left = ds.Min(d => d.Bounds.X), top = ds.Min(d => d.Bounds.Y);
            int width = ds.Max(d => d.Bounds.Right) - left, height = ds.Max(d => d.Bounds.Bottom) - top;
            double scale = Math.Min((canvas.ActualWidth - 34) / Math.Max(1, width), (canvas.Height - 30) / Math.Max(1, height));
            double ox = (canvas.ActualWidth - width * scale) / 2, oy = (canvas.Height - height * scale) / 2;
            foreach (var d in ds)
            {
                var frame = new Border { Width = d.Bounds.Width * scale, Height = d.Bounds.Height * scale, Background = Brush("#E7EEF5"), BorderBrush = Brush("#91A8BC"), BorderThickness = new Thickness(1.2), CornerRadius = new CornerRadius(4) };
                Canvas.SetLeft(frame, ox + (d.Bounds.X - left) * scale); Canvas.SetTop(frame, oy + (d.Bounds.Y - top) * scale); canvas.Children.Add(frame);
            }
            string[] colors = { "#4CAFA1", "#7297C7", "#A28BD0", "#CBAB65", "#76A8AC" }; int c = 0;
            foreach (var w in snapshot.Windows.OrderByDescending(w => w.Z).Take(40))
            {
                if (w.State == 2) continue;
                var display = ds.FirstOrDefault(d => d.Id == w.DisplayId); var r = w.State == 3 && display != null ? display.Work : w.Rect;
                var color = colors[c++ % colors.Length];
                var box = new Border { Width = Math.Max(3, r.Width * scale), Height = Math.Max(3, r.Height * scale), Background = Brush(color), BorderBrush = Brushes.White, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Opacity = 0.88, ToolTip = w.App + " · " + w.Title };
                if (box.Width > 60 && box.Height > 20) box.Child = new TextBlock { Text = w.App, Foreground = Brushes.White, FontSize = 10, Margin = new Thickness(7, 5, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                Canvas.SetLeft(box, ox + (r.X - left) * scale); Canvas.SetTop(box, oy + (r.Y - top) * scale); canvas.Children.Add(box);
            }
        }
        private string NameDialog(string title, string initial, bool allowDefault, out bool makeDefault)
        {
            var dialog = new Window { Owner = window, Title = title, Width = 435, Height = allowDefault ? 255 : 220, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = window.FontFamily, FontSize = 13, Background = Brush("#F4F6FA") };
            var panel = new StackPanel { Margin = new Thickness(25) }; dialog.Content = panel;
            panel.Children.Add(new TextBlock { Text = "布局名称", Margin = new Thickness(0, 0, 0, 10) });
            var box = new TextBox { Text = initial, MaxLength = 60, Padding = new Thickness(10, 8, 10, 8) }; panel.Children.Add(box);
            var check = new CheckBox { Content = "设为当前显示器组合的默认布局", IsChecked = data.Layouts.Count == 0, Margin = new Thickness(0, 15, 0, 0), Visibility = allowDefault ? Visibility.Visible : Visibility.Collapsed }; panel.Children.Add(check);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) }; panel.Children.Add(buttons);
            var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(0, 0, 10, 0) };
            var save = new Button { Content = "保存", IsDefault = true, Padding = new Thickness(18, 7, 18, 7) };
            buttons.Children.Add(cancel); buttons.Children.Add(save); save.Click += delegate { if (!String.IsNullOrWhiteSpace(box.Text)) dialog.DialogResult = true; };
            dialog.Loaded += delegate { box.Focus(); box.SelectAll(); };
            bool ok = dialog.ShowDialog() == true; makeDefault = allowDefault && check.IsChecked == true; return ok ? box.Text.Trim() : null;
        }
        private void LoadSettings()
        {
            Find<CheckBox>("LearningCheck").IsChecked = data.Options.Learning;
            Find<CheckBox>("AutoRestoreCheck").IsChecked = data.Options.AutoRestore;
            Find<TextBox>("LearningIntervalBox").Text = data.Options.LearningIntervalSeconds.ToString();
            Find<TextBox>("StableSecondsBox").Text = data.Options.StableSeconds.ToString();
            Find<TextBox>("NewWindowDelayBox").Text = data.Options.NewWindowDelaySeconds.ToString();
            Find<CheckBox>("DisplayCheck").IsChecked = data.Options.RestoreDisplay;
            Find<CheckBox>("WakeCheck").IsChecked = data.Options.RestoreWake;
            Find<CheckBox>("NewCheck").IsChecked = data.Options.RestoreNew;
            Find<TextBox>("RestoreKeyBox").Text = data.Options.RestoreHotkey; Find<TextBox>("SaveKeyBox").Text = data.Options.SaveHotkey;
            Find<TextBox>("ExcludedBox").Text = String.Join(Environment.NewLine, data.Options.Excluded);
            using (var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")) Find<CheckBox>("StartupCheck").IsChecked = !isolated && key != null && key.GetValue("WindowHome") != null;
            Find<CheckBox>("StartupCheck").IsEnabled = !isolated;
        }
        private void ApplySettings()
        {
            int interval, stable, delay;
            if (!Int32.TryParse(Find<TextBox>("LearningIntervalBox").Text.Trim(), out interval) || interval < 5 || interval > 3600)
                throw new InvalidOperationException("记忆间隔需为 5–3600 秒的整数。");
            if (!Int32.TryParse(Find<TextBox>("StableSecondsBox").Text.Trim(), out stable) || stable < 1 || stable > 120)
                throw new InvalidOperationException("稳定等待需为 1–120 秒的整数。");
            if (!Int32.TryParse(Find<TextBox>("NewWindowDelayBox").Text.Trim(), out delay) || delay < 1 || delay > 30)
                throw new InvalidOperationException("新窗口稳定等待需为 1–30 秒的整数。");
            uint restoreMods, restoreKey, saveMods, saveKey;
            string restore = Find<TextBox>("RestoreKeyBox").Text.Trim(), save = Find<TextBox>("SaveKeyBox").Text.Trim();
            if (!Hotkey.Parse(restore, out restoreMods, out restoreKey) || !Hotkey.Parse(save, out saveMods, out saveKey)) throw new InvalidOperationException("快捷键格式不正确，例如 Ctrl+Alt+R；需包含 Ctrl 或 Alt。");
            if (restoreKey != 0 && restoreKey == saveKey && restoreMods == saveMods) throw new InvalidOperationException("保存与恢复需要使用不同的快捷键。");
            if (!isolated)
            {
                using (var run = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
                {
                    if (Find<CheckBox>("StartupCheck").IsChecked == true)
                    {
                        string command = "\"" + Assembly.GetExecutingAssembly().Location + "\" --background";
                        if (System.IO.Path.GetFullPath(store.DirectoryPath) == System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data"))) command += " --portable";
                        run.SetValue("WindowHome", command);
                    }
                    else run.DeleteValue("WindowHome", false);
                }
            }
            data.Options.Learning = Find<CheckBox>("LearningCheck").IsChecked == true;
            data.Options.AutoRestore = Find<CheckBox>("AutoRestoreCheck").IsChecked == true;
            data.Options.LearningIntervalSeconds = interval; data.Options.StableSeconds = stable; data.Options.NewWindowDelaySeconds = delay;
            data.Options.RestoreDisplay = Find<CheckBox>("DisplayCheck").IsChecked == true;
            data.Options.RestoreWake = Find<CheckBox>("WakeCheck").IsChecked == true;
            data.Options.RestoreNew = Find<CheckBox>("NewCheck").IsChecked == true;
            data.Options.RestoreHotkey = restore; data.Options.SaveHotkey = save;
            data.Options.Excluded = Find<TextBox>("ExcludedBox").Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (current != null) { learning.Baseline(current, DateTime.UtcNow); ResetKnown(current); }
            pendingAuto = false;
            Persist(); string result = RegisterKeys(); Message("设置已保存。" + result);
        }
        private string RegisterKeys()
        {
            if (source == null) return "";
            Native.UnregisterHotKey(source.Handle, 1); Native.UnregisterHotKey(source.Handle, 2);
            string warning = "";
            string[] keys = { data.Options.RestoreHotkey, data.Options.SaveHotkey };
            for (int i = 0; i < keys.Length; i++)
            {
                uint mods, key;
                if (!Hotkey.Parse(keys[i], out mods, out key)) { warning += "快捷键格式无效：" + keys[i] + "。"; continue; }
                if (key != 0 && !Native.RegisterHotKey(source.Handle, i + 1, mods | 0x4000, key)) warning += "快捷键被占用，未启用：" + keys[i] + "。界面按钮仍可使用。";
            }
            if (warning.Length != 0) Message(warning); return warning;
        }
        private async Task LaunchMissing()
        {
            if (data.Layouts.Count == 0) { Message("先保存一套布局。"); return; }
            await RefreshCurrent();
            var dialog = new Window { Owner = window, Title = "启动软件", Width = 610, Height = 500, MinWidth = 500, MinHeight = 390,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = window.FontFamily, FontSize = 13, Background = Brush("#F4F6FA") };
            var panel = new DockPanel { Margin = new Thickness(22) }; dialog.Content = panel;
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 15) }; DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
            header.Children.Add(new TextBlock { Text = "方案", Margin = new Thickness(0, 0, 0, 7) });
            var plans = new ComboBox { Name = "LaunchPlanPicker", ItemsSource = data.Layouts, DisplayMemberPath = "DisplayName", Padding = new Thickness(9), MinHeight = 36 }; header.Children.Add(plans);
            var footer = new DockPanel { Margin = new Thickness(0, 15, 0, 0), LastChildFill = false }; DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
            var selectedCount = new TextBlock { Text = "已选 0 个软件", VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(selectedCount, Dock.Left); footer.Children.Add(selectedCount);
            var go = new Button { Name = "LaunchGoButton", Content = "启动并恢复所选", IsEnabled = false, Padding = new Thickness(16, 9, 16, 9), Background = Brush("#087F73"), Foreground = Brushes.White }; DockPanel.SetDock(go, Dock.Right); footer.Children.Add(go);
            var cancel = new Button { Name = "LaunchCancelButton", Content = "取消", IsCancel = true, Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0, 0, 9, 0) }; DockPanel.SetDock(cancel, Dock.Right); footer.Children.Add(cancel);
            var list = new StackPanel(); panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var checks = new List<CheckBox>();
            Action selectionChanged = delegate { int count = checks.Count(c => c.IsChecked == true); selectedCount.Text = "已选 " + count + " 个软件"; go.IsEnabled = count > 0; };
            plans.SelectionChanged += delegate
            {
                list.Children.Clear(); checks.Clear(); var layout = plans.SelectedItem as LayoutSnapshot;
                if (layout != null)
                {
                    foreach (var choice in LaunchSelection.Choices(layout, current, data.Options, File.Exists))
                    {
                        var label = new StackPanel(); label.Children.Add(new TextBlock { Text = choice.Caption, FontSize = 14, FontWeight = FontWeights.SemiBold });
                        label.Children.Add(new TextBlock { Text = choice.Status, Foreground = Brush("#728096"), FontSize = 11, Margin = new Thickness(0, 5, 0, 0) });
                        var check = new CheckBox { Name = "LaunchChoice_" + checks.Count, Content = label, IsChecked = false, IsEnabled = choice.CanSelect,
                            ToolTip = choice.Path, Margin = new Thickness(4, 10, 0, 10), Tag = choice, VerticalContentAlignment = VerticalAlignment.Center };
                        System.Windows.Automation.AutomationProperties.SetName(check, choice.Caption + " · " + choice.Status);
                        check.Checked += delegate { selectionChanged(); }; check.Unchecked += delegate { selectionChanged(); };
                        checks.Add(check); list.Children.Add(check);
                    }
                }
                selectionChanged();
            };
            var preferred = SelectedLayout() ?? service.Target(current);
            plans.SelectedItem = preferred != null && data.Layouts.Contains(preferred) ? preferred : data.Layouts.First();
            go.Click += delegate { dialog.DialogResult = true; };
            if (dialog.ShowDialog() != true) return;
            var chosen = checks.Where(c => c.IsChecked == true).Select(c => (LaunchChoice)c.Tag).ToList();
            var target = LaunchSelection.Subset((LayoutSnapshot)plans.SelectedItem, chosen.Select(c => c.Path));
            if (target.Windows.Count == 0) return;
            await RefreshCurrent(); learning.Hold(DateTime.UtcNow.AddSeconds(20));
            var errors = new List<RestoreItem>(); var failedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); bool launched = false;
            foreach (var choice in chosen)
            {
                if (current.Windows.Any(w => String.Equals(w.Path, choice.Path, StringComparison.OrdinalIgnoreCase))) continue;
                try
                {
                    if (AppRules.Excludes(choice.Path, choice.App, data.Options.Excluded)) throw new InvalidOperationException("此应用已排除。");
                    using (var process = Process.Start(new ProcessStartInfo(choice.Path) { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(choice.Path) })) { }
                    launched = true;
                }
                catch (Exception ex)
                {
                    failedPaths.Add(choice.Path);
                    errors.Add(new RestoreItem { App = choice.App, Title = "", Status = "启动失败", Detail = ex.GetBaseException().Message });
                }
            }
            if (launched)
            {
                Message("等待所选软件的窗口…"); var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 12000)
                {
                    await Task.Delay(500); var snap = await service.CaptureAsync();
                    if (chosen.All(c => failedPaths.Contains(c.Path) || snap.Windows.Any(w => String.Equals(w.Path, c.Path, StringComparison.OrdinalIgnoreCase)))) { await Task.Delay(800); break; }
                }
            }
            target.Windows.RemoveAll(w => failedPaths.Contains(w.Path));
            RestoreReport report;
            if (target.Windows.Count > 0) report = await service.RestoreAsync(target, true, true, "启动软件");
            else { report = new RestoreReport { LayoutName = target.Name, Trigger = "启动软件" }; ReportHistory.Add(data, report); }
            report.Items.AddRange(errors); await Finished(report);
        }
        private IntPtr MessageHook(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
        {
            if ((uint)message == showMessage) { Show(); handled = true; }
            if (message == 0x0312)
            {
                int id = w.ToInt32(); window.Dispatcher.BeginInvoke(new Action(async delegate
                { if (id == 2) Show(); await TrayAction(id == 1 ? (Func<Task>)(async delegate { await Restore(current == null ? null : service.Target(current), true, "快捷键"); }) : SaveNamed); })); handled = true;
            }
            if (message == 0x007E) Transition("显示器配置变化", data.Options.RestoreDisplay);
            if (message == 0x0218)
            {
                if (w.ToInt32() == 4) { suspended = true; learning.Hold(DateTime.UtcNow.AddMinutes(10)); }
                if (w.ToInt32() == 7 || w.ToInt32() == 18) { suspended = false; Transition("系统唤醒", data.Options.RestoreWake); }
            }
            return IntPtr.Zero;
        }
        private void SessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (exiting) return;
            window.Dispatcher.BeginInvoke(new Action(delegate
            {
                if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.RemoteDisconnect) { locked = true; return; }
                if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.RemoteConnect) { locked = false; Transition("会话恢复", data.Options.RestoreWake); }
            }));
        }
        private void Show() { window.Show(); if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; Native.ShowWindowAsync(new WindowInteropHelper(window).Handle, 9); window.Activate(); }
        private void Closing(object sender, CancelEventArgs e)
        {
            if (exiting) return; e.Cancel = true; window.Hide();
            if (firstClose) { firstClose = false; tray.BalloonTipTitle = "窗归继续在托盘运行"; tray.BalloonTipText = "单击图标打开；右键可暂停或退出。"; tray.ShowBalloonTip(3000); }
        }
        private void Exit() { exiting = true; timer.Stop(); try { Persist(); } catch (Exception ex) { Error(ex); } tray.Visible = false; app.Shutdown(); }
        private void Persist() { store.Save(data); }
        private void Message(string message) { Find<TextBlock>("MessageLabel").Text = message; }
        public void Error(Exception ex)
        {
            Message(ex.GetBaseException().Message);
            try
            {
                Directory.CreateDirectory(store.DirectoryPath); string log = System.IO.Path.Combine(store.DirectoryPath, "errors.log");
                if (File.Exists(log) && new FileInfo(log).Length > 1000000) File.Move(log, log + "." + DateTime.UtcNow.Ticks);
                File.AppendAllText(log, DateTime.Now.ToString("s") + " " + ex + Environment.NewLine);
            }
            catch { }
        }
        public void Dispose()
        {
            timer.Stop(); SystemEvents.SessionSwitch -= SessionSwitch;
            if (moveHook != IntPtr.Zero) Native.UnhookWinEvent(moveHook);
            if (source != null) { Native.UnregisterHotKey(source.Handle, 1); Native.UnregisterHotKey(source.Handle, 2); source.RemoveHook(MessageHook); }
            tray.Dispose();
        }
    }
    public static class Hotkey
    {
        public static bool Parse(string text, out uint modifiers, out uint key)
        {
            modifiers = 0; key = 0; if (String.IsNullOrWhiteSpace(text)) return true;
            var parts = text.Split('+').Select(s => s.Trim().ToUpperInvariant()).ToList();
            foreach (var p in parts.Take(parts.Count - 1))
            { if (p == "CTRL" || p == "CONTROL") modifiers |= 2; else if (p == "ALT") modifiers |= 1; else if (p == "SHIFT") modifiers |= 4; else return false; }
            string k = parts.Last();
            if (k.Length == 1 && ((k[0] >= 'A' && k[0] <= 'Z') || (k[0] >= '0' && k[0] <= '9'))) key = k[0];
            else { int f; if (k.StartsWith("F") && Int32.TryParse(k.Substring(1), out f) && f >= 1 && f <= 12) key = (uint)(0x70 + f - 1); }
            return key != 0 && (modifiers & 3) != 0;
        }
    }
}
