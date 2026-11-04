// 开发用：离屏渲染 LiteTodo 界面为 PNG（不显示窗口、不抢焦点）
// 用法：Snap.exe <data.json> <输出目录> <前缀>
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiteTodo;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

static partial class Snap
{
    static string outDir, prefix;
    static MainController ctl;

    [STAThread]
    static void Main(string[] a)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Environment.SetEnvironmentVariable("LITETODO_DATA", a[0]);
        outDir = a[1];
        prefix = a.Length > 2 ? a[2] : "snap";
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (s, e) => { Console.Error.WriteLine(e.Exception); e.Handled = true; app.Shutdown(2); };
        app.Resources.MergedDictionaries.Add(Program.LoadXaml<ResourceDictionary>("Styles.xaml"));
        if (prefix == "--video")
        {
            Fx.TimeScale = SLOW;   // 必须在创建控制器之前设置，定时器才会一起放慢
            ctl = new MainController(app, true, true);
            RunVideo(app, Path.Combine(outDir, a.Length > 3 ? a[3] : "demo.mp4"));
            app.Run();
            return;
        }
        if (prefix == "--gallery") { ctl = new MainController(app, true, true); RunSteps(Gallery(app), 0); app.Run(); return; }
        if (prefix == "--real") { ctl = new MainController(app, true, false); RunSteps(RealDock(app), 0); app.Run(); return; }
        ctl = new MainController(app, true, true);

        if (prefix == "--test") { RunSteps(Tests(app), 0); app.Run(); return; }

        var steps = new List<KeyValuePair<int, Action>>
        {
            Step(700, () => Shot("main")),
            Step(0, () => { ctl.DebugChrome(true); ctl.DebugSelect(1); ctl.DebugComposer(true); ctl.DebugToast("已删除「示例条目」"); }),
            Step(600, () => Shot("active")),
            Step(0, () => { ctl.DebugComposer(false); ctl.DebugMenu(0); }),
            Step(600, () => Shot("menu", ctl.MenuPopup.Child as FrameworkElement)),
            Step(0, () => { ctl.MenuPopup.IsOpen = false; Call("OpenWinMenu"); }),
            Step(600, () => Shot("winmenu", ((System.Windows.Controls.Primitives.Popup)ctl.Win.FindName("WinMenu")).Child as FrameworkElement)),
            Step(0, () => { ((System.Windows.Controls.Primitives.Popup)ctl.Win.FindName("WinMenu")).IsOpen = false; Call("SetDockHint", MainController.Edge.Right); }),
            Step(500, () => Shot("dockhint")),
            Step(0, () => { Call("SetDockHint", MainController.Edge.None); ctl.DebugSettings(true); }),
            Step(700, () => Shot("settings")),
            Step(0, () => { ScrollSettings(0.62); }),
            Step(400, () => Shot("settings2")),
            Step(0, () => ctl.DebugSettings(false)),
            Step(400, () => { ctl.DebugChrome(false); ctl.DebugMini(); }),
            Step(600, () => Shot("mini")),
            Step(0, () => app.Shutdown()),
        };
        RunSteps(steps, 0);
        app.Run();
    }

    static KeyValuePair<int, Action> Step(int delay, Action act) => new KeyValuePair<int, Action>(delay, act);

    // ================= 真实窗口贴边截图（会在屏幕边缘短暂出现窗口，不抢焦点） =================
    static void ScreenShot(string name, int x, int y, int w, int h)
    {
        using (var bmp = new System.Drawing.Bitmap(w, h))
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
            var path = Path.Combine(outDir, "real_" + name + ".png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine(path);
        }
    }

    static List<KeyValuePair<int, Action>> RealDock(Application app)
    {
        var sw = (int)SystemParameters.PrimaryScreenWidth;
        return new List<KeyValuePair<int, Action>>
        {
            Step(900, () => ctl.DebugDock("right", false)),
            Step(500, () => ScreenShot("right_shown", sw - 440, 0, 440, 700)),
            Step(0, () => ctl.DebugDockHide()),
            Step(500, () => ScreenShot("right_hidden", sw - 120, 0, 120, 700)),
            Step(0, () => ctl.DebugDock("top", true)),
            Step(500, () => ScreenShot("top_hidden", sw - 520, 0, 520, 120)),
            Step(0, () => { ctl.DebugUndock(); ctl.Win.Hide(); }),
            Step(300, () => app.Shutdown()),
        };
    }

    // ================= 逻辑测试 =================
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    static int pass, fail;
    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) pass++; else fail++;
        Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name + (ok ? "" : "  -> " + detail));
    }
    static object Call(string method, params object[] args)
    {
        var m = typeof(MainController).GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return m.Invoke(ctl, args);
    }
    static object Field(string name) => typeof(MainController).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(ctl);
    static void Key(System.Windows.Input.Key k)
    {
        var target = (UIElement)ctl.Win.FindName("Scroller");
        var e = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(ctl.Win), 0, k)
        { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(e);
    }
    static void Click(string tag, object dc)
    {
        var b = new Button { Tag = tag, DataContext = dc };
        Call("OnClick", b, new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, b));
    }
    static List<TodoItem> Active => System.Linq.Enumerable.ToList(System.Linq.Enumerable.Cast<TodoItem>(ctl.VM.ActiveView));
    static List<TodoItem> Done => System.Linq.Enumerable.ToList(System.Linq.Enumerable.Cast<TodoItem>(ctl.VM.DoneView));
    static TodoItem Sel => (TodoItem)Field("selected");

    static List<KeyValuePair<int, Action>> Tests(Application app)
    {
        var vm = ctl.VM;
        TodoItem added = null, first = null;
        int total0 = 0;
        return new List<KeyValuePair<int, Action>>
        {
            Step(600, () =>
            {
                Check("旧配置升级：默认开启鼠标移入不透明", vm.S.HoverOpaque && vm.S.Version == 2);
                Func<byte> bgAlpha = () => ((LinearGradientBrush)app.Resources["BgBrush"]).GradientStops[0].Color.A;
                Call("SetHover", true);
                Check("鼠标移入：背景变为不透明", bgAlpha() >= 240, "alpha=" + bgAlpha());
                Call("SetHover", false);
                Check("鼠标移出：恢复设定透明度", Math.Abs(bgAlpha() - vm.S.BgOpacity * 255) < 2, "alpha=" + bgAlpha());

                Check("默认出现在 Alt+Tab", !ctl.IsToolWindow);
                vm.HideAltTab = true;
                Check("开启后为工具窗口（不在 Alt+Tab）且不显示任务栏", ctl.IsToolWindow && !vm.ShowInTaskbar);
                var h = new System.Windows.Interop.WindowInteropHelper(ctl.Win).Handle;
                SetWindowLong(h, -20, GetWindowLong(h, -20) & ~0x80);   // 模拟其他代码改写扩展样式
                Check("样式被改写时自动保持", ctl.IsToolWindow);
                vm.HideAltTab = false;
                Check("关闭后恢复 Alt+Tab 与任务栏", !ctl.IsToolWindow && vm.ShowInTaskbar);
                total0 = vm.Items.Count;
                added = vm.Add("买牛奶 !! #蓝");
                Check("解析 !! 与 #蓝", added != null && added.Priority == 2 && added.Color == "#60A5FA" && added.Text == "买牛奶",
                      added == null ? "null" : added.Priority + " " + added.Color + " [" + added.Text + "]");
                Check("只有符号不添加", vm.Add("  !!! ") == null);
                var a2 = vm.Add("!!! 紧急");
                Check("开头 !!! 为高", a2 != null && a2.Priority == 3 && a2.Text == "紧急");
                Check("新条目进入进行中列表", Active.Contains(added) && Active.Contains(a2));
                vm.SetDraftPriority(1); vm.SetDraftColor("#F87171");
                var a3 = vm.Add("草稿属性");
                Check("使用输入栏选择的优先级/颜色并重置", a3.Priority == 1 && a3.Color == "#F87171" && vm.Draft.Priority == 0 && vm.Draft.Color == "");
            }),
            Step(100, () =>
            {
                // —— 贴边（无界面模式使用虚拟坐标，不移动真实窗口）——
                ctl.DebugDock("left", false);
                Check("贴边：状态与强制置顶", ctl.DockEdge.ToString() == "Left" && vm.Docked && vm.EffectiveTopmost && vm.S.Dock == "left");
                var wr = (System.Windows.Rect)Call("DebugCardRect");
                var work = (System.Windows.Rect)Call("DebugWork");
                Check("贴边展开：卡片紧贴左边缘", Math.Abs(wr.Left - work.Left) <= 1, wr + " / " + work);
                ctl.DebugDockHide();
                wr = (System.Windows.Rect)Call("DebugCardRect");
                Check("贴边收起：只露出细边", !ctl.DockShown && wr.Right - work.Left > 0 && wr.Right - work.Left <= 8, "露出 " + (wr.Right - work.Left));
                Check("收起后阴影边距鼠标穿透", ctl.Win.Background == System.Windows.Media.Brushes.Transparent);
                ctl.DebugDockShow();
                wr = (System.Windows.Rect)Call("DebugCardRect");
                Check("再次展开", ctl.DockShown && Math.Abs(wr.Left - work.Left) <= 1);
                ctl.DebugDock("top", true);
                wr = (System.Windows.Rect)Call("DebugCardRect");
                Check("贴顶收起", wr.Bottom - work.Top > 0 && wr.Bottom - work.Top <= 8, "露出 " + (wr.Bottom - work.Top));
                ctl.DebugUndock();
                Check("取消贴边", !vm.Docked && !vm.EffectiveTopmost && vm.S.Dock == "" && ctl.Win.Background != System.Windows.Media.Brushes.Transparent);

                Check("识别链接", MainController.FindUrl("看下 https://github.com/a/b，然后回复") == "https://github.com/a/b"
                      && MainController.FindUrl("没有链接") == null);
                vm.Mini = true;
                Check("折叠时显示下一项", vm.HeaderSubtitle.StartsWith("下一项：") && vm.HeaderSubtitle.Contains(Active[0].Text), vm.HeaderSubtitle);
                vm.Mini = false;
                Check("展开时显示日期", !vm.HeaderSubtitle.StartsWith("下一项"));

                // —— 窗口层级 ——
                vm.SelectLayer("bottom");
                Check("置底", vm.S.BottomMost && !vm.S.Topmost && vm.LayerChoices[2].IsSelected);
                vm.Topmost = true;
                Check("置顶会取消置底", vm.S.Topmost && !vm.S.BottomMost && vm.LayerChoices[0].IsSelected);
                vm.SelectLayer("normal");
                Check("普通", !vm.S.Topmost && !vm.S.BottomMost);

                // —— 批量添加 ——
                int before = vm.Items.Count;
                int n = (int)Call("AddLines", "- 第一项\n* 第二项\n1. 第三项\n[ ] 第四项\n\n  - [x] 第五项  \n");
                var last5 = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Skip(vm.Items, before));
                Check("多行批量添加并去掉列表符号", n == 5 && last5.Count == 5 && last5[0].Text == "第一项" && last5[3].Text == "第四项" && last5[4].Text == "第五项",
                      string.Join("|", System.Linq.Enumerable.Select(last5, x => x.Text)));
                foreach (var x in last5) vm.Items.Remove(x);
                int b2 = vm.Items.Count;
                vm.AddTutorial();
                Check("添加使用示例", vm.Items.Count == b2 + 7);
                foreach (var x in System.Linq.Enumerable.ToList(System.Linq.Enumerable.Skip(vm.Items, b2))) vm.Items.Remove(x);
            }),
            Step(100, () =>
            {
                first = Active[0];
                Call("ToggleDone", first);
                Check("勾选后暂留在原分组（等动画）", Active.Contains(first) && first.IsDone);
            }),
            Step(1600, () =>
            {
                Check("稍后移动到已完成", !Active.Contains(first) && Done.Contains(first), "active=" + Active.Contains(first));
                Check("已完成按完成时间倒序（最新在前）", Done.Count > 0 && Done[0] == first);
                Call("ToggleDone", first);
            }),
            Step(1600, () =>
            {
                Check("取消完成后回到进行中", Active.Contains(first) && !Done.Contains(first));
                ctl.DebugSelect(0);
                Key(System.Windows.Input.Key.Down);
                Check("↓ 选择下一项", Sel == Active[1], Sel?.Text);
                Key(System.Windows.Input.Key.Up);
                Check("↑ 选择上一项", Sel == Active[0], Sel?.Text);
                Key(System.Windows.Input.Key.D1);
                Check("数字 1 设为高优先级", Active[0].Priority == 3);
                Key(System.Windows.Input.Key.D0);
                Check("数字 0 取消优先级", Active[0].Priority == 0);
                Key(System.Windows.Input.Key.Space);
                Check("空格完成", Sel != null && Sel.IsDone);
            }),
            Step(1600, () =>
            {
                Check("空格完成后已移入已完成且仍选中", Done.Contains(Sel) && Sel.IsSelected);
                Key(System.Windows.Input.Key.Space);
            }),
            Step(1600, () =>
            {
                var target = Active[1];
                ctl.DebugSelect(1);
                Key(System.Windows.Input.Key.Delete);
                Check("Del 删除：动画期间仍在列表", vm.Items.Contains(target));
                first = target;
            }),
            Step(600, () =>
            {
                Check("Del 删除：动画后移除", !vm.Items.Contains(first));
                Check("删除后自动选中相邻项", Sel != null && Sel != first && Sel.IsSelected, Sel?.Text);
                Check("出现撤销提示", ((FrameworkElement)ctl.Win.FindName("Toast")).Visibility == Visibility.Visible);
                Call("Undo");
                Check("撤销恢复到原位置", vm.Items.Contains(first) && Active.IndexOf(first) == 1, "idx=" + Active.IndexOf(first));
            }),
            Step(300, () =>
            {
                var it = Active[0];
                Call("BeginEdit", it);
                Check("进入编辑", it.IsEditing && it.EditText == it.Text);
                it.EditText = "  改过的标题  ";
                Call("CommitEdit", false);
                Check("保存编辑（去空格）", !it.IsEditing && it.Text == "改过的标题", it.Text);
                Call("BeginEdit", it);
                it.EditText = "";
                Call("CommitEdit", false);
                Check("空内容不覆盖", it.Text == "改过的标题");
                Call("BeginEdit", it);
                it.EditText = "取消的修改";
                Call("CommitEdit", true);
                Check("取消编辑", it.Text == "改过的标题");
            }),
            Step(100, () =>
            {
                var a0 = Active[0]; var a1 = Active[1];
                Check("向下移动一位", vm.MoveWithin(a0, 1) && Active[0] == a1 && Active[1] == a0);
                vm.MoveWithin(a0, -1);
                // 菜单：设置优先级/颜色
                ctl.DebugMenu(0);
                var t = Active[0];
                Click("setPrio", vm.PrioChoices[0]);
                Click("setColor", vm.ColorChoices[5]);
                Check("菜单设置优先级与颜色", t.Priority == vm.PrioChoices[0].Level && t.Color == vm.ColorChoices[5].Key);
                Check("菜单选中状态同步", vm.PrioChoices[0].IsSelected && vm.ColorChoices[5].IsSelected);
                ctl.MenuPopup.IsOpen = false;
                Click("draftPrio", vm.DraftPrios[0]);
                Check("输入栏优先级切换", vm.Draft.Priority == 3 && vm.DraftPrios[0].IsSelected);
                Click("draftPrio", vm.DraftPrios[0]);
                Check("再次点击取消", vm.Draft.Priority == 0 && !vm.DraftPrios[0].IsSelected);
                vm.SortByPriority = true;
                Check("按优先级排序", Active[0].Priority >= Active[Active.Count - 1].Priority);
                vm.SortByPriority = false;
            }),
            Step(100, () =>
            {
                double h = ctl.Win.ActualHeight;
                ctl.DebugMini();
                first = new TodoItem { Text = h.ToString() };
            }),
            Step(500, () =>
            {
                Check("折叠为标题栏", ctl.VM.Mini && ctl.Win.ActualHeight < 160, "h=" + ctl.Win.ActualHeight);
                ctl.DebugMini();
            }),
            Step(500, () =>
            {
                Check("展开恢复高度", !ctl.VM.Mini && Math.Abs(ctl.Win.ActualHeight - double.Parse(first.Text)) < 2, "h=" + ctl.Win.ActualHeight + " expect " + first.Text);
                Click("clearDone", null);
            }),
            Step(700, () =>
            {
                Check("清除已完成", Done.Count == 0 && !vm.HasDone);
                Check("统计", vm.DoneCount == 0 && vm.ShowEmpty == (Active.Count == 0));
                Console.WriteLine($"RESULT pass={pass} fail={fail}");
                app.Shutdown(fail);
            }),
        };
    }

    static void RunSteps(List<KeyValuePair<int, Action>> steps, int i)
    {
        if (i >= steps.Count) return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, steps[i].Key)) };
        t.Tick += (s, e) =>
        {
            t.Stop();
            try { steps[i].Value(); } catch (Exception ex) { Console.Error.WriteLine(ex); }
            RunSteps(steps, i + 1);
        };
        t.Start();
    }

    static void ScrollSettings(double frac)
    {
        var sv = FindAll<ScrollViewer>(ctl.Win);
        foreach (var s in sv) if (s.ScrollableHeight > 0 && IsInSettings(s)) s.ScrollToVerticalOffset(s.ScrollableHeight * frac);
    }

    static bool IsInSettings(DependencyObject d)
    {
        for (; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement fe && fe.Name == "SettingsPanel") return true;
        return false;
    }

    static List<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var r = new List<T>();
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) r.Add(t);
            r.AddRange(FindAll<T>(c));
        }
        return r;
    }

    static Brush Wallpaper(double w, double h)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x3A, 0x5A, 0x8C), 0));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xA8, 0x7B, 0xB0), 0.55));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xF0, 0xB8, 0x8A), 1));
        return g;
    }

    static bool transparentShots;

    // 官网 / README 用的透明背景截图
    static List<KeyValuePair<int, Action>> Gallery(Application app)
    {
        var vm = ctl.VM;
        var steps = new List<KeyValuePair<int, Action>> { Step(800, () => transparentShots = true) };
        foreach (var th in new[] { "midnight", "graphite", "ocean", "aurora", "dusk", "daylight", "sakura", "mint", "paper" })
        {
            var t = th;
            steps.Add(Step(0, () => vm.SelectTheme(t)));
            steps.Add(Step(450, () => Shot("theme_" + t)));
        }
        steps.Add(Step(0, () => vm.SelectTheme("midnight")));
        foreach (var ly in new[] { "comfy", "compact", "card", "minimal" })
        {
            var l = ly;
            steps.Add(Step(0, () => vm.SelectLayout(l)));
            steps.Add(Step(450, () => Shot("layout_" + l)));
        }
        steps.Add(Step(0, () => vm.SelectLayout("comfy")));
        steps.Add(Step(0, () => { ctl.DebugChrome(true); ctl.DebugSelect(1); ctl.DebugComposer(true); ((TextBox)Field("input")).Text = "准备周会材料 !!! #蓝"; }));
        steps.Add(Step(600, () => Shot("feature_compose")));
        steps.Add(Step(0, () => { ((TextBox)Field("input")).Text = ""; ctl.DebugComposer(false); ctl.DebugMenu(0); }));
        steps.Add(Step(600, () => Shot("feature_menu", ctl.MenuPopup.Child as FrameworkElement)));
        steps.Add(Step(0, () => Call("SetDockHint", MainController.Edge.Right)));
        steps.Add(Step(500, () => Shot("feature_dock")));
        steps.Add(Step(0, () => { Call("SetDockHint", MainController.Edge.None); ctl.DebugSettings(true); }));
        steps.Add(Step(700, () => Shot("feature_settings")));
        steps.Add(Step(0, () => ctl.DebugSettings(false)));
        steps.Add(Step(500, () => { ctl.DebugChrome(false); ctl.DebugMini(); }));
        steps.Add(Step(600, () => Shot("feature_mini")));
        steps.Add(Step(0, () => app.Shutdown()));
        return steps;
    }

    static void Shot(string name, FrameworkElement popup = null)
    {
        var w = ctl.Win;
        w.UpdateLayout();
        const double scale = 2;
        double W = w.ActualWidth, H = w.ActualHeight;
        var content = new RenderTargetBitmap((int)(W * scale), (int)(H * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        content.Render(w);

        BitmapSource pop = null;
        double pw = 0, ph = 0;
        if (popup != null)
        {
            popup.UpdateLayout();
            pw = popup.ActualWidth + 28; ph = popup.ActualHeight + 28;
            var rb = new RenderTargetBitmap((int)(pw * scale), (int)(ph * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rb.Render(popup);
            pop = rb;
        }

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (!transparentShots)
            {
                dc.DrawRectangle(Wallpaper(W, H), null, new Rect(0, 0, W, H));
                // 几个色块，用来观察透明效果
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), null, new Point(W * 0.82, H * 0.30), 46, 46);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(220, 30, 30, 40)), null, new Rect(W * 0.08, H * 0.62, W * 0.35, 30));
            }
            dc.DrawImage(content, new Rect(0, 0, W, H));
            if (pop != null) dc.DrawImage(pop, new Rect(W - pw - 4, 96, pw, ph));
        }
        var final = new RenderTargetBitmap((int)(W * scale), (int)(H * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        final.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(final));
        var path = Path.Combine(outDir, prefix + "_" + name + ".png");
        using (var fs = File.Create(path)) enc.Save(fs);
        Console.WriteLine(path);
    }
}
