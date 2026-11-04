using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace LiteTodo
{
    public partial class MainController
    {
        readonly Application app;
        readonly MainVM vm;
        readonly Window win;
        readonly bool headless;
        readonly DispatcherTimer saveTimer, toastTimer, moveTimer;

        readonly FrameworkElement menuLink;
        readonly FrameworkElement header, ringBox, winButtons, listArea, composer, composerTools, settingsPanel, settingsHeader, menuActions, menuToTop;
        readonly Ellipse ring;
        readonly ScrollViewer scroller;
        readonly ItemsControl activeList, doneList, prioList, colorList;
        readonly Border toast, bgLayer;
        readonly TextBlock toastText, menuDoneText;
        readonly Button undoBtn, sendBtn;
        readonly TextBox input;
        readonly Popup menu, winMenu;
        readonly FrameworkElement dockHandle, dockHint;
        readonly Border dockHintEdge;
        readonly TextBlock dockHintText, winMenuDockText, winMenuMiniText;

        WinForms.NotifyIcon tray;
        bool hovering, exiting, chromeOn, settingsOpen, toolsOpen, sendShown;
        DateTime lastDeactivated = DateTime.MinValue;

        T F<T>(string name) where T : class => (T)win.FindName(name) ?? throw new InvalidOperationException("XAML 中缺少 " + name);

        public MainController(Application app, bool silent, bool headless = false)
        {
            this.app = app;
            this.headless = headless;
            vm = new MainVM(Store.Load());

            // 先写入主题资源，再解析窗口 XAML
            Themes.Apply(app.Resources, vm.S, false);
            win = Program.LoadXaml<Window>("MainWindow.xaml");
            win.DataContext = vm;

            header = F<FrameworkElement>("Header");
            ringBox = F<FrameworkElement>("RingBox");
            ring = F<Ellipse>("Ring");
            winButtons = F<FrameworkElement>("WinButtons");
            listArea = F<FrameworkElement>("ListArea");
            scroller = F<ScrollViewer>("Scroller");
            activeList = F<ItemsControl>("ActiveList");
            doneList = F<ItemsControl>("DoneList");
            toast = F<Border>("Toast");
            toastText = F<TextBlock>("ToastText");
            undoBtn = F<Button>("UndoBtn");
            composer = F<FrameworkElement>("Composer");
            composerTools = F<FrameworkElement>("ComposerTools");
            input = F<TextBox>("Input");
            sendBtn = F<Button>("SendBtn");
            settingsPanel = F<FrameworkElement>("SettingsPanel");
            settingsHeader = F<FrameworkElement>("SettingsHeader");
            menu = F<Popup>("Menu");
            prioList = F<ItemsControl>("PrioList");
            colorList = F<ItemsControl>("ColorList");
            menuActions = F<FrameworkElement>("MenuActions");
            menuDoneText = F<TextBlock>("MenuDoneText");
            menuToTop = F<FrameworkElement>("MenuToTop");
            menuLink = F<FrameworkElement>("MenuLink");
            bgLayer = F<Border>("BgLayer");
            winMenu = F<Popup>("WinMenu");
            winMenuDockText = F<TextBlock>("WinMenuDockText");
            winMenuMiniText = F<TextBlock>("WinMenuMiniText");
            dockHandle = F<FrameworkElement>("DockHandle");
            dockHint = F<FrameworkElement>("DockHint");
            dockHintEdge = F<Border>("DockHintEdge");
            dockHintText = F<TextBlock>("DockHintText");

            prioList.ItemsSource = vm.PrioChoices;
            colorList.ItemsSource = vm.ColorChoices;

            var icon = Program.LoadIconFrame();
            if (icon != null) win.Icon = icon;

            RestoreBounds();
            ApplyAppearance();
            ApplyWinOpacity(false);
            Fx.SetRingProgress(ring, vm.Progress);

            vm.AppearanceChanged += ApplyAppearance;
            vm.WinOpacityChanged += () => ApplyWinOpacity(true);
            vm.DoneToggled += OnDoneToggled;
            vm.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(MainVM.Progress)) AnimateRing(); };

            saveTimer = new DispatcherTimer { Interval = Fx.T(600) };
            saveTimer.Tick += (s, e) => SaveNow();
            vm.SaveRequested += () => { saveTimer.Stop(); saveTimer.Start(); };

            toastTimer = new DispatcherTimer { Interval = Fx.T(5000) };
            toastTimer.Tick += (s, e) => HideToast();

            moveTimer = new DispatcherTimer { Interval = Fx.T(520) };
            moveTimer.Tick += (s, e) => FlushMoves();

            InitDock();
            WireEvents();
            win.SourceInitialized += (s, e) => InitHwnd();
            vm.LayerChanged += ApplyLayerZOrder;
            vm.AltTabChanged += () => { ApplyToolWindow(); RefreshShellButton(); };
            win.Closing += (s, e) => { if (!exiting) { e.Cancel = true; Exit(); } };
            app.SessionEnding += (s, e) => SaveNow();

            if (headless)
            {
                BindingOperations.ClearBinding(win, Window.ShowInTaskbarProperty);
                BindingOperations.ClearBinding(win, Window.TopmostProperty);
                win.ShowInTaskbar = false;
                win.ShowActivated = false;
                win.Left = -20000;
                win.Show();
                // 无界面测试：贴边逻辑使用虚拟坐标（不移动真实窗口）
                var wa = SystemParameters.WorkArea;
                fakeX = (int)(wa.Left + wa.Width / 3); fakeY = (int)(wa.Top + 60);
                if (vm.Mini) ApplyMini();
                return;
            }

            SetupTray();
            win.SourceInitialized += (s, e) => RegisterHotkey();

            win.ShowActivated = !silent;
            win.Show();
            if (vm.Mini) ApplyMini();
            if (!silent)
            {
                win.Activate();
                input.Focus();
            }
            ApplyLayerZOrder();
            // 上次是贴边状态：开机自启直接收起；手动启动先展开，鼠标离开后再收起
            if (vm.S.Dock != "") RestoreDock(showFirst: !silent);

            // 启动淡入
            win.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TargetOpacity, Fx.T(220)));

            var trim = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            trim.Tick += (s, e) => { trim.Stop(); Program.TrimMemory(); };
            trim.Start();
        }

        // ================= 外观 =================

        double TargetOpacity => (hovering && vm.S.HoverOpaque) ? 1.0 : vm.S.WinOpacity;

        void ApplyAppearance()
        {
            Themes.Apply(app.Resources, vm.S, hovering && vm.S.HoverOpaque);
            if (bgLayer.Effect is DropShadowEffect fx) fx.Opacity = (double)app.Resources["ShadowOpacity"];
        }

        void ApplyWinOpacity(bool animate)
        {
            if (!animate)
            {
                win.BeginAnimation(UIElement.OpacityProperty, null);
                win.Opacity = TargetOpacity;
                return;
            }
            win.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(TargetOpacity, Fx.T(180)));
        }

        void AnimateRing()
        {
            ring.BeginAnimation(Fx.RingProgressProperty, new DoubleAnimation(vm.Progress, Fx.T(480))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        void SetHover(bool on)
        {
            SetChrome(on || menu.IsOpen);
            if (hovering == on) return;
            hovering = on;
            if (!vm.S.HoverOpaque) return;
            ApplyAppearance();
            ApplyWinOpacity(true);
        }

        /// <summary>鼠标移入时显示窗口按钮，移出时显示进度环</summary>
        void SetChrome(bool on)
        {
            if (chromeOn == on) return;
            chromeOn = on;
            winButtons.IsHitTestVisible = on;
            Fx.Fade(winButtons, on ? 1 : 0, 150);
            Fx.Fade(ringBox, on ? 0 : 1, 150);
        }

        // ================= 设置页 =================

        void ShowSettings(bool open)
        {
            if (open == settingsOpen) return;
            settingsOpen = open;
            CommitEdit();
            menu.IsOpen = false;
            if (open)
            {
                if (vm.Mini) { vm.Mini = false; ApplyMini(); }
                settingsPanel.Visibility = Visibility.Visible;
                settingsPanel.Opacity = 0;
                Fx.Fade(settingsPanel, 1, 200);
                Fx.SlideX(settingsPanel, 26, 0, 240);
            }
            else
            {
                Fx.SlideX(settingsPanel, 0, 26, 160);
                Fx.Fade(settingsPanel, 0, 160, () => { if (!settingsOpen) settingsPanel.Visibility = Visibility.Collapsed; });
                input.Focus();
            }
        }

        // ================= 折叠为标题栏 =================

        void ToggleMini()
        {
            CommitEdit();
            menu.IsOpen = false;
            if (settingsOpen) ShowSettings(false);
            if (!vm.Mini) CaptureBounds();
            vm.Mini = !vm.Mini;
            ApplyMini();
        }

        void ApplyMini()
        {
            if (vm.Mini)
            {
                listArea.Visibility = Visibility.Collapsed;
                composer.Visibility = Visibility.Collapsed;
                win.MinHeight = 0;
                win.SizeToContent = SizeToContent.Height;
                win.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (!vm.Mini) return;
                    double h = win.ActualHeight;
                    win.SizeToContent = SizeToContent.Manual;
                    win.MinHeight = win.MaxHeight = h;
                }));
            }
            else
            {
                win.SizeToContent = SizeToContent.Manual;
                win.MaxHeight = double.PositiveInfinity;
                win.MinHeight = 220;
                win.Height = Math.Max(220, vm.S.Height);
                listArea.Visibility = Visibility.Visible;
                composer.Visibility = Visibility.Visible;
                Fx.SlideIn(listArea, -6, 220);
            }
        }

        // ================= 窗口位置 / 保存 =================

        void RestoreBounds()
        {
            var s = vm.S;
            win.Width = s.Width;
            win.Height = s.Height;
            win.WindowStartupLocation = WindowStartupLocation.Manual;

            var vl = SystemParameters.VirtualScreenLeft;
            var vt = SystemParameters.VirtualScreenTop;
            var vr = vl + SystemParameters.VirtualScreenWidth;
            var vb = vt + SystemParameters.VirtualScreenHeight;
            bool visible = s.HasPosition &&
                           s.Left + s.Width - 80 > vl && s.Left + 80 < vr &&
                           s.Top + 20 > vt && s.Top + 60 < vb;
            if (visible)
            {
                win.Left = s.Left;
                win.Top = s.Top;
            }
            else
            {
                var wa = SystemParameters.WorkArea;
                win.Left = wa.Right - s.Width - 32;
                win.Top = wa.Top + 48;
            }
        }

        void CaptureBounds(bool force = false)
        {
            if (headless || win.WindowState != WindowState.Normal || !win.IsLoaded) return;
            // 贴边时窗口位置由贴边逻辑控制，只记录尺寸（位置保存的是展开时的位置）
            if (dock == Edge.None || force)
            {
                vm.S.Left = win.Left;
                vm.S.Top = win.Top;
            }
            vm.S.Width = win.ActualWidth > 0 ? win.ActualWidth : win.Width;
            if (!vm.Mini && win.SizeToContent == SizeToContent.Manual)
                vm.S.Height = win.ActualHeight > 0 ? win.ActualHeight : win.Height;
            vm.S.HasPosition = true;
            vm.RequestSave();
        }

        void SaveNow()
        {
            saveTimer?.Stop();
            if (headless) return;
            try
            {
                vm.Data.Items = vm.Items.ToList();
                Store.Save(vm.Data);
            }
            catch (Exception ex)
            {
                Program.Log("保存失败: " + ex);
                if (!exiting) ShowToast("保存失败：" + ex.Message, false);
            }
        }

        // ================= 显示 / 隐藏 / 退出 =================

        void Minimize()
        {
            menu.IsOpen = false;
            winMenu.IsOpen = false;
            if (dock != Edge.None) { DockHide(); return; }
            if (vm.ShowInTaskbar) win.WindowState = WindowState.Minimized;
            else win.Hide();
            Program.TrimMemory();
        }

        public void ShowWindow()
        {
            if (!win.IsVisible) win.Show();
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            if (dock != Edge.None) DockShow(true, hold: true);
            win.Activate();
            if (!win.Topmost)
            {
                // 用 SetCurrentValue 以免破坏 Topmost 绑定
                win.SetCurrentValue(Window.TopmostProperty, true);
                win.SetCurrentValue(Window.TopmostProperty, false);
            }
            if (!vm.Mini && !settingsOpen) input.Focus();
        }

        void ToggleFromTray()
        {
            bool justActive = win.IsActive || (DateTime.Now - lastDeactivated).TotalMilliseconds < 400;
            bool visible = win.IsVisible && win.WindowState == WindowState.Normal && (dock == Edge.None || dockShown);
            if (visible && justActive) Minimize();
            else ShowWindow();
        }

        void ToggleFromHotkey()
        {
            bool visible = win.IsVisible && win.WindowState == WindowState.Normal && (dock == Edge.None || dockShown);
            if (visible && win.IsActive) Minimize();
            else ShowWindow();
        }

        public void RequestExit() => Exit();

        void Exit()
        {
            if (exiting) return;
            exiting = true;
            CommitEdit();
            CaptureBounds();
            SaveNow();
            UnregisterHotkey();
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            app.Shutdown();
        }

        void SetupTray()
        {
            try
            {
                tray = new WinForms.NotifyIcon
                {
                    Icon = Program.LoadTrayIcon() ?? System.Drawing.SystemIcons.Application,
                    Visible = true
                };
                var m = new WinForms.ContextMenuStrip
                {
                    ShowImageMargin = false,
                    ShowCheckMargin = true,
                    Font = new System.Drawing.Font("Microsoft YaHei UI", 9f)
                };
                m.Items.Add("显示 / 隐藏", null, (s, e) => ToggleFromTray());
                m.Items.Add("新建任务", null, (s, e) => { ShowWindow(); if (vm.Mini) ToggleMini(); if (settingsOpen) ShowSettings(false); input.Focus(); });
                var top = new WinForms.ToolStripMenuItem("窗口置顶");
                top.Click += (s, e) => vm.Topmost = !vm.Topmost;
                m.Items.Add(top);
                m.Items.Add("设置", null, (s, e) => { ShowWindow(); ShowSettings(true); });
                m.Items.Add(new WinForms.ToolStripSeparator());
                m.Items.Add("退出", null, (s, e) => Exit());
                m.Opening += (s, e) => top.Checked = vm.Topmost;
                tray.ContextMenuStrip = m;
                tray.MouseClick += (s, e) => { if (e.Button == WinForms.MouseButtons.Left) ToggleFromTray(); };
                UpdateTrayText();
                vm.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(MainVM.Subtitle)) UpdateTrayText(); };
            }
            catch (Exception ex) { Program.Log("托盘初始化失败: " + ex); }
        }

        void UpdateTrayText()
        {
            if (tray == null) return;
            int left = vm.Items.Count(i => !i.IsDone);
            var text = left == 0 ? "LiteTodo · 没有待办" : "LiteTodo · 还有 " + left + " 项待办";
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        // ================= 全局热键 Ctrl+Alt+T =================

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] static extern int SetWindowLong(IntPtr h, int index, int value);
        [StructLayout(LayoutKind.Sequential)] struct STYLESTRUCT { public int styleOld, styleNew; }
        const int HotkeyId = 0x4C54;
        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
        IntPtr hwnd;

        void InitHwnd()
        {
            hwnd = new WindowInteropHelper(win).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            ApplyToolWindow();
        }

        /// <summary>工具窗口样式 = 不出现在 Alt+Tab（也没有任务栏按钮）</summary>
        int FixExStyle(int ex) => vm.HideAltTab ? (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW : ex & ~WS_EX_TOOLWINDOW;

        void ApplyToolWindow()
        {
            if (hwnd == IntPtr.Zero) return;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            int fixedEx = FixExStyle(ex);
            if (fixedEx != ex) SetWindowLong(hwnd, GWL_EXSTYLE, fixedEx);
        }

        internal bool IsToolWindow => hwnd != IntPtr.Zero && (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0;

        /// <summary>任务栏只在窗口显示时读取样式，切换后隐藏再显示一次让它刷新</summary>
        void RefreshShellButton()
        {
            if (headless || !win.IsVisible) return;
            win.Hide();
            win.Show();
            win.Activate();
        }

        void RegisterHotkey()
        {
            const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
            if (!RegisterHotKey(hwnd, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x54 /* T */))
                Program.Log("全局热键 Ctrl+Alt+T 注册失败（可能被其他程序占用）");
        }

        void UnregisterHotkey()
        {
            if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, HotkeyId);
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312, WM_STYLECHANGING = 0x007C, WM_WINDOWPOSCHANGING = 0x0046;
            if (msg == WM_WINDOWPOSCHANGING) { KeepBottom(lParam); return IntPtr.Zero; }
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                ToggleFromHotkey();
                handled = true;
            }
            else if (msg == WM_STYLECHANGING && (int)wParam.ToInt64() == GWL_EXSTYLE)
            {
                // WPF 内部改扩展样式时（如切换任务栏显示）保持工具窗口设置不被覆盖
                var ss = Marshal.PtrToStructure<STYLESTRUCT>(lParam);
                int want = FixExStyle(ss.styleNew);
                if (want != ss.styleNew)
                {
                    ss.styleNew = want;
                    Marshal.StructureToPtr(ss, lParam, false);
                }
            }
            return IntPtr.Zero;
        }

        int fakeX, fakeY; // 无界面测试时的虚拟窗口位置

        /// <summary>短暂提示（无撤销按钮）</summary>
        void ShowHud(string text, int ms = 1400)
        {
            lastHudUntil = DateTime.Now.AddMilliseconds(ms);
            toastText.Text = text;
            undoBtn.Visibility = Visibility.Collapsed;
            if (toast.Visibility != Visibility.Visible || toast.Opacity < 0.5)
            {
                toast.Visibility = Visibility.Visible;
                Fx.SlideIn(toast, 8, 180);
            }
            toastTimer.Stop();
            toastTimer.Interval = Fx.T(ms);
            toastTimer.Start();
        }
        DateTime lastHudUntil;

        // ================= 截图工具用（tools/snap） =================

        internal Window Win => win;
        internal MainVM VM => vm;
        internal Popup MenuPopup => menu;
        internal void DebugChrome(bool on) => SetChrome(on);
        internal void DebugSettings(bool open) => ShowSettings(open);
        internal void DebugComposer(bool open) { toolsOpen = open; Fx.Expand(composerTools, open, 1); sendBtn.Visibility = open ? Visibility.Visible : Visibility.Collapsed; }
        internal void DebugMenuFor(TodoItem it) => OpenMenu(it, null);
        internal void DebugMenu(int index) => OpenMenu(vm.ActiveView.Cast<TodoItem>().ElementAt(index), null);
        internal void DebugSelect(int index) => Select(NavOrder()[index], false);
        internal void DebugMini() => ToggleMini();
        internal void DebugToast(string text) => ShowToast(text, true);
    }
}
