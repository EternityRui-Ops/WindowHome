using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowHome
{
    public interface IWindowSystem
    {
        LayoutSnapshot Capture(AppOptions options);
        bool IsCurrent(WindowRecord window);
        string Apply(WindowRecord live, WindowRecord saved, PixelRect target, DisplayRecord display);
        bool Verify(WindowRecord live, WindowRecord saved, PixelRect target, DisplayRecord display);
        void Order(IList<WindowRecord> frontToBack);
    }
    public sealed class WindowsSystem : IWindowSystem
    {
        public HashSet<int> OnlyProcesses;
        private readonly int ownPid = Process.GetCurrentProcess().Id;
        [ThreadStatic] private static IVirtualDesktopManager desktops;
        private static IVirtualDesktopManager Desktops
        {
            get
            {
                if (desktops == null) desktops = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")));
                return desktops;
            }
        }
        public static void EnableDpi()
        {
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { try { Native.SetProcessDpiAwareness(2); } catch { Native.SetProcessDPIAware(); } }
        }
        public static List<DisplayRecord> Displays()
        {
            var list = new List<DisplayRecord>();
            Native.MonitorEnum callback = delegate(IntPtr h, IntPtr dc, ref Native.RECT r, IntPtr p)
            {
                var info = new Native.MONITORINFOEX(); info.cbSize = Marshal.SizeOf(info);
                if (Native.GetMonitorInfo(h, ref info))
                {
                    var device = new Native.DISPLAY_DEVICE(); device.cb = Marshal.SizeOf(device);
                    string id = info.szDevice, name = info.szDevice.Replace("\\\\.\\", "");
                    if (Native.EnumDisplayDevices(info.szDevice, 0, ref device, 1))
                    { if (!String.IsNullOrEmpty(device.DeviceID)) id = device.DeviceID; if (!String.IsNullOrEmpty(device.DeviceString)) name = device.DeviceString; }
                    uint x = 96, y;
                    try { Native.GetDpiForMonitor(h, 0, out x, out y); } catch { x = 96; }
                    list.Add(new DisplayRecord { Id = id, Name = name, Bounds = info.rcMonitor.ToPixel(), Work = info.rcWork.ToPixel(), Dpi = x == 0 ? 96 : x, Primary = (info.dwFlags & 1) != 0 });
                }
                return true;
            };
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            return list;
        }
        private static string DesktopId(IntPtr hwnd)
        {
            try
            {
                bool current; Guid id;
                if (Desktops.IsWindowOnCurrentVirtualDesktop(hwnd, out current) >= 0 && current && Desktops.GetWindowDesktopId(hwnd, out id) >= 0 && id != Guid.Empty)
                    return id.ToString("D");
            }
            catch (COMException) { } catch (InvalidCastException) { }
            return "";
        }
        public LayoutSnapshot Capture(AppOptions options)
        {
            var s = new LayoutSnapshot { Name = "自动记忆", Displays = Displays() };
            s.Topology = Geometry.Topology(s.Displays); s.DesktopId = DesktopId(Native.GetForegroundWindow());
            int z = 0;
            var processCache = new Dictionary<int, ProcessIdentity>();
            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
            {
                int rank = z++; uint pid; Native.GetWindowThreadProcessId(hwnd, out pid);
                if ((int)pid == ownPid || (OnlyProcesses != null && !OnlyProcesses.Contains((int)pid))) return true;
                if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, 4) != IntPtr.Zero) return true;
                long ex = Native.GetWindowLongPtr(hwnd, -20).ToInt64(), style = Native.GetWindowLongPtr(hwnd, -16).ToInt64();
                if ((ex & 0x80) != 0 || (style & 0x40000000) != 0) return true;
                var cls = new StringBuilder(256); Native.GetClassName(hwnd, cls, cls.Capacity);
                if (new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "#32770", "tooltips_class32", "Windows.UI.Core.CoreWindow" }.Contains(cls.ToString())) return true;
                int cloaked;
                if (Native.DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                var title = new StringBuilder(1024); Native.GetWindowText(hwnd, title, title.Capacity);
                if (title.Length == 0) return true;
                ProcessIdentity identity;
                if (!processCache.TryGetValue((int)pid, out identity)) { identity = GetIdentity((int)pid); processCache[(int)pid] = identity; }
                if (identity == null || AppRules.Excludes(identity.Path, identity.Name, options.Excluded)) return true;
                var placement = Native.Placement(); Native.RECT rect;
                if (!Native.GetWindowPlacement(hwnd, ref placement) || !Native.GetWindowRect(hwnd, out rect)) return true;
                var monitorInfo = new Native.MONITORINFOEX(); monitorInfo.cbSize = Marshal.SizeOf(monitorInfo);
                if (!Native.GetMonitorInfo(Native.MonitorFromWindow(hwnd, 2), ref monitorInfo)) return true;
                var display = s.Displays.FirstOrDefault(d => d.Bounds.Near(monitorInfo.rcMonitor.ToPixel(), 0));
                if (display == null) return true;
                int state = Native.IsIconic(hwnd) ? 2 : Native.IsZoomed(hwnd) ? 3 : 1;
                PixelRect normal = state == 1 ? rect.ToPixel() : PlacementToScreen(placement.rcNormalPosition.ToPixel(), display);
                if (normal.Width < 60 || normal.Height < 40) return true;
                string desktop = DesktopId(hwnd);
                // Unknown desktop identity remains visible in the UI, but cannot be managed.
                if (String.IsNullOrEmpty(s.DesktopId) && !String.IsNullOrEmpty(desktop)) s.DesktopId = desktop;
                s.Windows.Add(new WindowRecord { Handle = hwnd.ToInt64(), Pid = (int)pid, ProcessStarted = identity.Started,
                    Path = identity.Path, App = identity.Name, Title = title.ToString(), ClassName = cls.ToString(),
                    DesktopId = desktop, DisplayId = display.Id, Work = display.Work.Copy(), Dpi = display.Dpi,
                    Rect = normal, State = state, RestoreToMax = (placement.flags & 2) != 0, Z = rank, Topmost = (ex & 8) != 0,
                    Fullscreen = state == 1 && (style & 0xC00000) == 0 && rect.ToPixel().Near(display.Bounds, 4) });
                return true;
            }, IntPtr.Zero);
            // Do not mix a pinned/unknown window into another desktop's automatic history.
            s.Windows = s.Windows.Where(w => w.DesktopId == s.DesktopId || String.IsNullOrEmpty(w.DesktopId)).ToList();
            foreach (var group in s.Windows.GroupBy(w => w.AppKey)) foreach (var w in group) w.AppWindowCount = group.Count();
            return s;
        }
        private sealed class ProcessIdentity { public string Path, Name; public long Started; }
        private static ProcessIdentity GetIdentity(int pid)
        {
            var h = Native.OpenProcess(0x1000, false, pid);
            string path = "", name = ""; long started = 0;
            if (h != IntPtr.Zero)
            {
                try
                {
                    var b = new StringBuilder(2048); int size = b.Capacity;
                    if (Native.QueryFullProcessImageName(h, 0, b, ref size)) path = b.ToString();
                    long e, k, u; Native.GetProcessTimes(h, out started, out e, out k, out u);
                }
                finally { Native.CloseHandle(h); }
            }
            if (!String.IsNullOrEmpty(path)) name = System.IO.Path.GetFileNameWithoutExtension(path);
            else { try { using (var p = Process.GetProcessById(pid)) name = p.ProcessName; } catch { return null; } }
            return new ProcessIdentity { Name = name, Path = path, Started = started };
        }
        public static PixelRect PlacementToScreen(PixelRect r, DisplayRecord d)
        { return new PixelRect(r.X + d.Work.X - d.Bounds.X, r.Y + d.Work.Y - d.Bounds.Y, r.Width, r.Height); }
        public static PixelRect ScreenToPlacement(PixelRect r, DisplayRecord d)
        { return new PixelRect(r.X - d.Work.X + d.Bounds.X, r.Y - d.Work.Y + d.Bounds.Y, r.Width, r.Height); }
        public bool IsCurrent(WindowRecord window)
        {
            var h = new IntPtr(window.Handle); uint pid; Native.GetWindowThreadProcessId(h, out pid);
            if (!Native.IsWindow(h) || pid != window.Pid || String.IsNullOrEmpty(window.DesktopId) || DesktopId(h) != window.DesktopId) return false;
            var identity = GetIdentity((int)pid);
            return identity != null && identity.Started != 0 && identity.Started == window.ProcessStarted;
        }
        public string Apply(WindowRecord live, WindowRecord saved, PixelRect target, DisplayRecord display)
        {
            IntPtr h = new IntPtr(live.Handle);
            if (!Native.IsWindow(h)) return "窗口已关闭";
            if (!IsCurrent(live)) return "窗口身份或所属桌面已变化，已停止操作";
            if (Native.IsHungAppWindow(h)) return "窗口无响应，已跳过";
            if (live.Fullscreen) return "全屏窗口已跳过，请退出全屏后重试";
            uint thread = Native.GetWindowThreadProcessId(h, out Native.unusedPid);
            var gui = new Native.GUITHREADINFO(); gui.cbSize = Marshal.SizeOf(gui);
            if (Native.GetGUIThreadInfo(thread, ref gui) && (gui.flags & 2) != 0) return "窗口正在拖动或缩放，已跳过";
            var p = Native.Placement(); p.flags = 4u | (saved.RestoreToMax && saved.State == 2 ? 2u : 0u);
            p.showCmd = saved.State == 3 ? 3u : saved.State == 2 ? 7u : 4u;
            p.ptMinPosition = new Native.POINT(-1, -1); p.ptMaxPosition = new Native.POINT(-1, -1);
            p.rcNormalPosition = Native.RECT.From(ScreenToPlacement(target, display));
            if (!Native.SetWindowPlacement(h, ref p)) return Error("设置窗口状态");
            if (saved.State == 1 && !Native.SetWindowPos(h, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, 0x4000 | 0x10 | 0x4 | 0x200)) return Error("设置位置");
            return "";
        }
        private static string Error(string action)
        {
            int code = Marshal.GetLastWin32Error();
            return code == 5 ? "权限不足：请手动以管理员身份运行窗归后重试" : action + "失败（" + code + "）：" + new Win32Exception(code).Message;
        }
        public bool Verify(WindowRecord live, WindowRecord saved, PixelRect target, DisplayRecord display)
        {
            if (!IsCurrent(live)) return false;
            IntPtr h = new IntPtr(live.Handle); var p = Native.Placement();
            if (!Native.GetWindowPlacement(h, ref p)) return false;
            int state = Native.IsIconic(h) ? 2 : Native.IsZoomed(h) ? 3 : 1;
            if (state != saved.State || (state == 2 && saved.RestoreToMax != ((p.flags & 2) != 0))) return false;
            var mi = new Native.MONITORINFOEX(); mi.cbSize = Marshal.SizeOf(mi);
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(h, 2), ref mi) || !display.Bounds.Near(mi.rcMonitor.ToPixel(), 0)) return false;
            Native.RECT actual;
            if (state == 1) return Native.GetWindowRect(h, out actual) && target.Near(actual.ToPixel(), 4);
            return target.Near(PlacementToScreen(p.rcNormalPosition.ToPixel(), display), 4);
        }
        public void Order(IList<WindowRecord> frontToBack)
        {
            foreach (var w in frontToBack.Reverse()) if (IsCurrent(w) && !Native.IsHungAppWindow(new IntPtr(w.Handle)))
                Native.SetWindowPos(new IntPtr(w.Handle), IntPtr.Zero, 0, 0, 0, 0, 0x4000 | 0x10 | 0x1 | 0x2 | 0x200);
        }
    }
    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] out bool current);
        [PreserveSig] int GetWindowDesktopId(IntPtr hWnd, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr hWnd, ref Guid desktopId);
    }
    internal static class Native
    {
        internal static uint unusedPid;
        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] internal struct RECT
        {
            public int Left, Top, Right, Bottom;
            public PixelRect ToPixel() { return new PixelRect(Left, Top, Right - Left, Bottom - Top); }
            public static RECT From(PixelRect r) { return new RECT { Left = r.X, Top = r.Y, Right = r.Right, Bottom = r.Bottom }; }
        }
        [StructLayout(LayoutKind.Sequential)] internal struct WINDOWPLACEMENT { public int length; public uint flags, showCmd; public POINT ptMinPosition, ptMaxPosition; public RECT rcNormalPosition; }
        internal static WINDOWPLACEMENT Placement() { var p = new WINDOWPLACEMENT(); p.length = Marshal.SizeOf(p); return p; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct MONITORINFOEX
        { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct GUITHREADINFO
        { public int cbSize; public uint flags; public IntPtr active, focus, capture, menuOwner, moveSize, caret; public RECT caretRect; }
        internal delegate bool EnumWindowProc(IntPtr hwnd, IntPtr p);
        internal delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr p);
        internal delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowProc callback, IntPtr p);
        [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool EnumDisplayDevices(string name, uint i, ref DISPLAY_DEVICE device, uint flags);
        [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(IntPtr h, int type, out uint x, out uint y);
        [DllImport("shcore.dll")] internal static extern int SetProcessDpiAwareness(int mode);
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool IsHungAppWindow(IntPtr h);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr h, StringBuilder b, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr h, StringBuilder b, int max);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int height, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] internal static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("kernel32.dll")] internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder b, ref int size);
        [DllImport("kernel32.dll")] internal static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr h);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string message);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(IntPtr h, int cmd);
    }
}
