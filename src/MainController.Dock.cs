using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace LiteTodo
{
    /// <summary>
    /// 贴边收起（类似 QQ）：拖到屏幕左 / 右 / 上边缘后自动收起成一条细边，
    /// 鼠标碰到细边滑出，移开后自动收回。所有坐标都用物理像素，避免多显示器 DPI 换算问题。
    /// 另外自己实现窗口拖动：不会触发 Windows 半屏贴靠，并支持磁吸和贴边提示。
    /// </summary>
    public partial class MainController
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int L, T, R, B; public int W => R - L; public int H => B - T; }
        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, int flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        const int MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTONEAREST = 2;
        static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        internal enum Edge { None, Left, Right, Top }

        Edge dock = Edge.None;
        bool dockShown = true, holdOpen;
        RECT dockWork;
        DispatcherTimer hideTimer, showTimer;
        EventHandler tween;
        static readonly Brush HitBrush = Freeze(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
        static Brush Freeze(Brush b) { b.Freeze(); return b; }

        // 自定义拖动
        bool dragging, dragMoved;
        POINT drag0;
        RECT win0;
        Edge dragHint = Edge.None;

        double Dpi => VisualTreeHelper.GetDpi(win).DpiScaleX;
        int MarginPx => (int)Math.Round(14 * Dpi);           // 窗口外围阴影边距
        int PeekPx => Math.Max(3, (int)Math.Round(4 * Dpi));  // 收起后露出的宽度
        int SnapPx => (int)Math.Round(16 * Dpi);              // 磁吸 / 贴边判定距离

        void InitDock()
        {
            hideTimer = new DispatcherTimer { Interval = Fx.T(380) };
            hideTimer.Tick += (s, e) => TryAutoHide();
            // 鼠标在细边上停留一小会儿才展开，避免甩鼠标到屏幕边缘时误触
            showTimer = new DispatcherTimer { Interval = Fx.T(110) };
            showTimer.Tick += (s, e) => { showTimer.Stop(); if (dock != Edge.None && !dockShown && CursorOverCard(0)) DockShow(); };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) =>
                win.Dispatcher.BeginInvoke(new Action(() => { if (dock != Edge.None) RestoreDock(false); }));
        }

        // ================= 几何 =================

        RECT WinRect()
        {
            GetWindowRect(hwnd, out var r);
            if (headless) { int w = r.W, h = r.H; r.L = fakeX; r.T = fakeY; r.R = fakeX + w; r.B = fakeY + h; }
            return r;
        }

        static void MonitorAt(POINT p, out RECT mon, out RECT work)
        {
            var h = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            GetMonitorInfo(h, ref mi);
            mon = mi.rcMonitor;
            work = mi.rcWork;
        }

        static bool HasMonitorAt(int x, int y) => MonitorFromPoint(new POINT(x, y), MONITOR_DEFAULTTONULL) != IntPtr.Zero;

        /// <summary>该边能否收起：工作区边缘就是屏幕边缘（那一侧没有任务栏），且外侧没有别的显示器</summary>
        static bool EdgeUsable(Edge e, RECT mon, RECT work)
        {
            switch (e)
            {
                case Edge.Left:
                    return work.L == mon.L && !Any(y => HasMonitorAt(mon.L - 1, y), mon.T, mon.B);
                case Edge.Right:
                    return work.R == mon.R && !Any(y => HasMonitorAt(mon.R, y), mon.T, mon.B);
                case Edge.Top:
                    return work.T == mon.T && !Any(x => HasMonitorAt(x, mon.T - 1), mon.L, mon.R);
            }
            return false;
        }

        static bool Any(Func<int, bool> test, int a, int b)
        {
            for (int i = 1; i <= 7; i++) if (test(a + (b - a) * i / 8)) return true;
            return false;
        }

        /// <summary>卡片（去掉阴影边距）贴近哪条可用的边</summary>
        Edge DetectEdge(RECT wr, RECT mon, RECT work)
        {
            int m = MarginPx, snap = SnapPx;
            int dl = wr.L + m - work.L, dr = work.R - (wr.R - m), dt = wr.T + m - work.T;
            Edge best = Edge.None;
            int bestD = int.MaxValue;
            void Try(Edge e, int d) { if (d <= snap && d < bestD && EdgeUsable(e, mon, work)) { best = e; bestD = d; } }
            Try(Edge.Left, dl);
            Try(Edge.Right, dr);
            Try(Edge.Top, dt);
            return best;
        }

        static int Clamp(int v, int min, int max) => max < min ? min : Math.Max(min, Math.Min(max, v));

        POINT DockPos(Edge e, bool shown, RECT wr)
        {
            int m = MarginPx, p = PeekPx, W = wr.W, H = wr.H;
            var work = dockWork;
            int x = wr.L, y = wr.T;
            switch (e)
            {
                case Edge.Left:
                    x = shown ? work.L - m : work.L + p - W + m;
                    y = Clamp(y, work.T - m, work.B - H + m);
                    break;
                case Edge.Right:
                    x = shown ? work.R - W + m : work.R - p - m;
                    y = Clamp(y, work.T - m, work.B - H + m);
                    break;
                case Edge.Top:
                    y = shown ? work.T - m : work.T + p - H + m;
                    x = Clamp(x, work.L - m, work.R - W + m);
                    break;
            }
            return new POINT(x, y);
        }

        // ================= 移动（带缓动） =================

        void SetPos(int x, int y)
        {
            if (headless) { fakeX = x; fakeY = y; return; }
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        void StopTween()
        {
            if (tween == null) return;
            CompositionTarget.Rendering -= tween;
            tween = null;
        }

        void MoveTo(POINT target, double ms, bool easeOut)
        {
            StopTween();
            var r = WinRect();
            if (ms <= 0 || (headless && !AnimateHeadless)) { SetPos(target.X, target.Y); return; }
            int x0 = r.L, y0 = r.T;
            var sw = Stopwatch.StartNew();
            tween = (s, e) =>
            {
                double t = Math.Min(1, sw.Elapsed.TotalMilliseconds / (ms * Fx.TimeScale));
                double k = easeOut ? 1 - Math.Pow(1 - t, 3) : t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
                SetPos((int)Math.Round(x0 + (target.X - x0) * k), (int)Math.Round(y0 + (target.Y - y0) * k));
                if (t >= 1) StopTween();
            };
            CompositionTarget.Rendering += tween;
        }

        // ================= 贴边 / 展开 / 收起 =================

        static Edge ParseEdge(string s) => s == "left" ? Edge.Left : s == "right" ? Edge.Right : s == "top" ? Edge.Top : Edge.None;
        static string EdgeName(Edge e) => e == Edge.Left ? "left" : e == Edge.Right ? "right" : e == Edge.Top ? "top" : "";

        void DockTo(Edge e, RECT work, bool hideNow)
        {
            dock = e;
            dockWork = work;
            vm.S.Dock = EdgeName(e);
            vm.Docked = true;
            dockShown = true;
            var shown = DockPos(e, true, WinRect());
            SetPos(shown.X, shown.Y);
            CaptureBounds(force: true);
            UpdateDockHandle();
            if (hideNow) DockHide(false);
            if (!vm.S.DockTipShown && !hideNow)
            {
                vm.S.DockTipShown = true;
                ShowHud("已贴边：鼠标移开会自动收起，碰到屏幕边缘展开", 3200);
            }
            vm.RequestSave();
        }

        void Undock()
        {
            if (dock == Edge.None) return;
            StopTween();
            hideTimer.Stop();
            dock = Edge.None;
            dockShown = true;
            vm.S.Dock = "";
            vm.Docked = false;
            win.Background = HitBrush;
            Fx.Fade(dockHandle, 0, 100);
            SetShadowVisible(true);
            ApplyLayerZOrder();
            vm.RequestSave();
        }

        /// <summary>收起时把窗口往屏幕内移一点再取消贴边（菜单里的“取消贴边”）</summary>
        void UndockInward()
        {
            if (dock == Edge.None) return;
            var e = dock;
            if (!dockShown) DockShow(false);
            var r = WinRect();
            int d = (int)Math.Round(24 * Dpi);
            Undock();
            int x = r.L + (e == Edge.Left ? d : e == Edge.Right ? -d : 0);
            int y = r.T + (e == Edge.Top ? d : 0);
            MoveTo(new POINT(x, y), 160, true);
            win.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => CaptureBounds()));
        }

        void DockShow(bool animate = true, bool hold = false)
        {
            if (dock == Edge.None) return;
            hideTimer.Stop();
            if (hold) holdOpen = true;
            if (dockShown && tween == null) return;
            dockShown = true;
            win.Background = HitBrush;
            Fx.Fade(dockHandle, 0, 100);
            SetShadowVisible(true);
            MoveTo(DockPos(dock, true, WinRect()), animate ? 190 : 0, true);
        }

        void DockHide(bool animate = true)
        {
            if (dock == Edge.None) return;
            hideTimer.Stop();
            holdOpen = false;
            dockShown = false;
            menu.IsOpen = false;
            winMenu.IsOpen = false;
            CommitEdit();
            SetHover(false);
            // 收起后只有细边可以接收鼠标，阴影边距变成鼠标穿透
            win.Background = Brushes.Transparent;
            SetShadowVisible(false);
            Fx.Fade(dockHandle, 1, 220);
            MoveTo(DockPos(dock, false, WinRect()), animate ? 230 : 0, false);
        }

        void SetShadowVisible(bool on)
        {
            if (bgLayer.Effect is DropShadowEffect fx)
                fx.Opacity = on ? (double)app.Resources["ShadowOpacity"] : 0;
        }

        void UpdateDockHandle()
        {
            switch (dock)
            {
                case Edge.Left:
                    dockHandle.HorizontalAlignment = HorizontalAlignment.Right; dockHandle.VerticalAlignment = VerticalAlignment.Center;
                    dockHandle.Width = 3.5; dockHandle.Height = 56; dockHandle.Margin = new Thickness(0, 0, 0.5, 0);
                    break;
                case Edge.Right:
                    dockHandle.HorizontalAlignment = HorizontalAlignment.Left; dockHandle.VerticalAlignment = VerticalAlignment.Center;
                    dockHandle.Width = 3.5; dockHandle.Height = 56; dockHandle.Margin = new Thickness(0.5, 0, 0, 0);
                    break;
                case Edge.Top:
                    dockHandle.HorizontalAlignment = HorizontalAlignment.Center; dockHandle.VerticalAlignment = VerticalAlignment.Bottom;
                    dockHandle.Width = 56; dockHandle.Height = 3.5; dockHandle.Margin = new Thickness(0, 0, 0, 0.5);
                    break;
            }
        }

        bool CursorOverCard(int tolerance = 2)
        {
            GetCursorPos(out var p);
            var r = WinRect();
            int m = MarginPx - tolerance;
            return p.X >= r.L + m && p.X < r.R - m && p.Y >= r.T + m && p.Y < r.B - m;
        }

        bool CanAutoHide =>
            dock != Edge.None && dockShown && !holdOpen && !dragging && !menu.IsOpen && !winMenu.IsOpen &&
            editing == null && draggingItem == null && !(input.IsKeyboardFocused && input.Text.Length > 0);

        void TryAutoHide()
        {
            if (dock == Edge.None || !dockShown) { hideTimer.Stop(); return; }
            if (CursorOverCard()) return;      // 鼠标回来了，继续等
            if (!CanAutoHide) return;          // 正在编辑 / 菜单打开：稍后再检查
            DockHide();
        }

        void OnDockMouseEnter()
        {
            if (dock == Edge.None) return;
            holdOpen = false;
            hideTimer.Stop();
            if (!dockShown) { showTimer.Stop(); showTimer.Start(); }
            else if (tween != null) DockShow();   // 正在收起时鼠标回来了：立即滑回
        }

        void OnDockMouseLeave()
        {
            showTimer.Stop();
            if (dock != Edge.None && dockShown) hideTimer.Start();
        }

        void OnDockDeactivated()
        {
            if (dock == Edge.None || !dockShown) return;
            holdOpen = false;
            if (!CursorOverCard()) hideTimer.Start();
        }

        /// <summary>启动或显示器变化后恢复贴边状态</summary>
        void RestoreDock(bool showFirst)
        {
            var e = ParseEdge(vm.S.Dock);
            if (e == Edge.None || hwnd == IntPtr.Zero) return;
            var r = WinRect();
            MonitorAt(new POINT((r.L + r.R) / 2, (r.T + r.B) / 2), out var mon, out var work);
            if (!EdgeUsable(e, mon, work)) { Undock(); vm.S.Dock = ""; return; }
            DockTo(e, work, hideNow: !showFirst);
            if (showFirst) holdOpen = true;
        }

        /// <summary>菜单 “收起到边缘”：选离卡片最近的可用边</summary>
        void DockToNearest()
        {
            var r = WinRect();
            MonitorAt(new POINT((r.L + r.R) / 2, (r.T + r.B) / 2), out var mon, out var work);
            int m = MarginPx;
            Edge best = Edge.None;
            int bestD = int.MaxValue;
            void Try(Edge e, int d) { if (d < bestD && EdgeUsable(e, mon, work)) { best = e; bestD = d; } }
            Try(Edge.Left, Math.Abs(r.L + m - work.L));
            Try(Edge.Right, Math.Abs(work.R - (r.R - m)));
            Try(Edge.Top, Math.Abs(r.T + m - work.T));
            if (best == Edge.None) { ShowHud("这块屏幕的边缘没法收起（有任务栏或相邻的显示器）", 2600); return; }
            dockWork = work;
            var target = DockPos(best, true, r);
            MoveTo(target, 180, true);
            win.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var t = new DispatcherTimer { Interval = Fx.T(200) };
                t.Tick += (s, e) => { t.Stop(); DockTo(best, work, hideNow: true); };
                t.Start();
            }));
        }

        // ================= 自定义拖动 =================

        void BeginDrag(UIElement handle, MouseButtonEventArgs e)
        {
            if (hwnd == IntPtr.Zero) return;
            StopTween();
            GetCursorPos(out drag0);
            win0 = WinRect();
            dragging = true;
            dragMoved = false;
            handle.CaptureMouse();
            e.Handled = true;
        }

        void DragTick()
        {
            if (!dragging) return;
            GetCursorPos(out var p);
            int dx = p.X - drag0.X, dy = p.Y - drag0.Y;
            if (!dragMoved)
            {
                if (Math.Abs(dx) + Math.Abs(dy) < 4) return;
                dragMoved = true;
                menu.IsOpen = false;
                winMenu.IsOpen = false;
                if (dock != Edge.None) Undock();
            }

            int x = win0.L + dx, y = win0.T + dy, W = win0.W, H = win0.H, m = MarginPx, snap = (int)Math.Round(10 * Dpi);
            MonitorAt(p, out var mon, out var work);
            // 磁吸：卡片边缘接近工作区边缘时对齐
            if (Math.Abs(x + m - work.L) < snap) x = work.L - m;
            else if (Math.Abs(x + W - m - work.R) < snap) x = work.R - W + m;
            if (Math.Abs(y + m - work.T) < snap) y = work.T - m;
            else if (Math.Abs(y + H - m - work.B) < snap) y = work.B - H + m;
            SetPos(x, y);

            var r = new RECT { L = x, T = y, R = x + W, B = y + H };
            SetDockHint(vm.S.EdgeDock ? DetectEdge(r, mon, work) : Edge.None);
        }

        void EndDrag(UIElement handle)
        {
            if (!dragging) return;
            dragging = false;
            if (handle.IsMouseCaptured) handle.ReleaseMouseCapture();
            var hint = dragHint;
            SetDockHint(Edge.None);
            if (!dragMoved) return;
            if (hint != Edge.None)
            {
                GetCursorPos(out var p);
                MonitorAt(p, out _, out var work);
                DockTo(hint, work, hideNow: false);
            }
            else CaptureBounds();
        }

        void SetDockHint(Edge e)
        {
            if (e == dragHint) return;
            dragHint = e;
            if (e == Edge.None) { Fx.Fade(dockHint, 0, 120, () => { if (dragHint == Edge.None) dockHint.Visibility = Visibility.Collapsed; }); return; }
            dockHintEdge.BorderThickness = e == Edge.Left ? new Thickness(3, 0, 0, 0) : e == Edge.Right ? new Thickness(0, 0, 3, 0) : new Thickness(0, 3, 0, 0);
            dockHintText.Text = e == Edge.Top ? "松开即可收起到顶部" : e == Edge.Left ? "松开即可收起到左侧" : "松开即可收起到右侧";
            dockHint.Visibility = Visibility.Visible;
            Fx.Fade(dockHint, 1, 140);
        }

        void WireDrag(UIElement handle, bool allowMini)
        {
            handle.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2 && allowMini) { EndDrag(handle); ToggleMini(); e.Handled = true; return; }
                BeginDrag(handle, e);
            };
            handle.MouseMove += (s, e) => DragTick();
            handle.MouseLeftButtonUp += (s, e) => EndDrag(handle);
            handle.LostMouseCapture += (s, e) => EndDrag(handle);
        }

        // ================= 窗口层级：置底 =================

        void ApplyLayerZOrder()
        {
            if (hwnd == IntPtr.Zero || headless) return;
            if (vm.S.BottomMost && dock == Edge.None)
                SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        /// <summary>置底模式下，所有 Z 序变化都改成放到最底层</summary>
        void KeepBottom(IntPtr lParam)
        {
            if (!vm.S.BottomMost || dock != Edge.None) return;
            var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((wp.flags & SWP_NOZORDER) != 0) return;
            wp.hwndInsertAfter = HWND_BOTTOM;
            Marshal.StructureToPtr(wp, lParam, false);
        }

        // ================= 测试用 =================
        /// <summary>录制视频时在无界面模式下也播放贴边动画（虚拟坐标）</summary>
        internal static bool AnimateHeadless;
        internal Edge DockEdge => dock;
        internal bool DockShown => dockShown;
        internal void DebugDock(string edge, bool hide)
        {
            var r = WinRect();
            MonitorAt(new POINT((r.L + r.R) / 2, (r.T + r.B) / 2), out var mon, out var work);
            DockTo(ParseEdge(edge), work, hide);
        }
        Rect DebugCardRect() { var r = WinRect(); int m = MarginPx; return new Rect(r.L + m, r.T + m, r.W - 2 * m, r.H - 2 * m); }
        Rect DebugWork() => new Rect(dockWork.L, dockWork.T, dockWork.W, dockWork.H);
        internal void DebugDockShow() => DockShow(false);
        // 录制视频：只切换收起 / 展开的视觉效果，位置由录制脚本控制
        internal void DebugDockHideVisual() { dockShown = false; win.Background = Brushes.Transparent; SetShadowVisible(false); Fx.Fade(dockHandle, 1, 220); }
        internal void DebugDockShowVisual() { dockShown = true; win.Background = HitBrush; SetShadowVisible(true); Fx.Fade(dockHandle, 0, 100); }
        internal void DebugDockHide() => DockHide(false);
        internal void DebugUndock() => Undock();
    }
}
