// 开发用：脚本化录制演示视频。离屏渲染每一帧 → 通过管道交给 ffmpeg 编码（不显示窗口、不动鼠标）
// 用法：Snap.exe <data.json> <输出目录> --video
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiteTodo;

static partial class Snap
{
    const int VW = 1920, VH = 1080, FPS = 30;
    const double S = 1.5;            // 窗口在视频中的缩放
    const double SLOW = 8;           // 动画放慢倍数（每帧有足够时间渲染）

    static Process ff;
    static Stream ffIn;
    static byte[] frameBuf;
    static RenderTargetBitmap bgBmp, winRt, finalRt;
    static int frameNo;
    static double Now => frameNo / (double)FPS;

    // 合成状态
    static double wx = 1180, wy = 70, wAlpha = 0, wSlideY = 30;
    static Point cursor = new Point(900, 760);
    static double cursorAlpha = 0;
    static FrameworkElement popupEl;
    static Point popupPos;
    static string capZh = "", capEn = "", capZhOld = "", capEnOld = "";
    static double capT0 = -10;
    static string bigTitle = null; static double bigAlpha = 0;
    static readonly List<Ripple> ripples = new List<Ripple>();
    class Ripple { public Point P; public double T0; }

    // 时间轴
    class Cue { public double T; public Action A; }
    class Tw { public double T0, Dur; public Action<double> Apply; public Func<double, double> Ease; public bool Started, Done; public Action Init; }
    static readonly List<Cue> cues = new List<Cue>();
    static readonly List<Tw> tweens = new List<Tw>();
    static double endTime;

    static void At(double t, Action a) => cues.Add(new Cue { T = t, A = a });
    static void Tween(double t0, double dur, Action<double> apply, Action init = null, Func<double, double> ease = null)
        => tweens.Add(new Tw { T0 = t0, Dur = dur, Apply = apply, Init = init, Ease = ease ?? EaseInOut });
    static double EaseInOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    /// <summary>光标移动到目标（目标在开始移动时再计算，保证布局已更新）</summary>
    static void CursorTo(double t0, double dur, Func<Point> target)
    {
        Point from = default, to = default;
        Tween(t0, dur, k => cursor = new Point(from.X + (to.X - from.X) * k, from.Y + (to.Y - from.Y) * k),
              () => { from = cursor; to = target(); });
    }

    static void Click(double t) => At(t, () => ripples.Add(new Ripple { P = cursor, T0 = Now }));

    static void Caption(double t, string zh, string en) => At(t, () =>
    {
        capZhOld = capZh; capEnOld = capEn; capZh = zh; capEn = en; capT0 = Now;
    });

    // ---------- 元素在视频中的坐标 ----------
    static Point InWin(FrameworkElement el, double fx = 0.5, double fy = 0.5)
    {
        var p = el.TransformToAncestor(ctl.Win).Transform(new Point(el.ActualWidth * fx, el.ActualHeight * fy));
        return new Point(wx + p.X * S, wy + p.Y * S);
    }
    static Point InPopup(FrameworkElement el)
    {
        var root = popupEl;
        var p = el.TransformToAncestor(root).Transform(new Point(el.ActualWidth / 2, el.ActualHeight / 2));
        return new Point(popupPos.X + (p.X + 14) * S, popupPos.Y + (p.Y + 14) * S);
    }
    static T F<T>(string name) where T : class => (T)ctl.Win.FindName(name);
    static TodoItem ItemByText(string text) => ctl.VM.Items.First(i => i.Text.StartsWith(text));
    static FrameworkElement CheckOf(TodoItem it)
    {
        var c = (FrameworkElement)Call("ContainerOf", it);
        return FindAll<ToggleButton>(c).First(t => (t.Tag as string) == "check");
    }
    static FrameworkElement ButtonIn(FrameworkElement root, Func<Button, bool> pred) => FindAll<Button>(root).First(pred);

    // =====================================================================
    static void RunVideo(Application app, string outPath)
    {
        Fx.TimeScale = SLOW;
        MainController.AnimateHeadless = true;
        var vm = ctl.VM;
        var input = (TextBox)Field("input");

        BuildBackground();
        Script(vm, input);

        var args = $"-y -f rawvideo -pix_fmt bgra -s {VW}x{VH} -r {FPS} -i - -c:v libx264 -preset slow -crf 21 -pix_fmt yuv420p -movflags +faststart \"{outPath}\"";
        ff = Process.Start(new ProcessStartInfo("ffmpeg", args) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, CreateNoWindow = true });
        ff.ErrorDataReceived += (s, e) => { };
        ff.BeginErrorReadLine();
        ffIn = ff.StandardInput.BaseStream;
        frameBuf = new byte[VW * VH * 4];

        double period = 1000.0 / FPS * SLOW;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(period) };
        var sw = Stopwatch.StartNew();
        double maxRender = 0;
        int late = 0;
        timer.Tick += (s, e) =>
        {
            var t0 = sw.Elapsed.TotalMilliseconds;
            Step();
            RenderFrame();
            maxRender = Math.Max(maxRender, sw.Elapsed.TotalMilliseconds - t0);
            frameNo++;
            // 下一帧的目标时刻 = 起点 + 帧号 × 周期（保证画面里的动画速度准确）
            double due = frameNo * period - sw.Elapsed.TotalMilliseconds;
            if (due < 1) { late++; due = 1; }
            timer.Interval = TimeSpan.FromMilliseconds(due);
            if (Now > endTime)
            {
                timer.Stop();
                ffIn.Flush(); ffIn.Close();
                ff.WaitForExit();
                Console.WriteLine($"video: {frameNo} frames, max render {maxRender:0} ms (budget {period:0} ms), late frames {late} -> {outPath}");
                app.Shutdown(ff.ExitCode);
            }
        };
        timer.Start();
    }

    static void Step()
    {
        foreach (var c in cues.Where(c => c.T <= Now).ToList()) { cues.Remove(c); try { c.A(); } catch (Exception ex) { Console.Error.WriteLine(ex); } }
        foreach (var t in tweens)
        {
            if (t.Done || Now < t.T0) continue;
            if (!t.Started) { t.Started = true; t.Init?.Invoke(); }
            double k = t.Dur <= 0 ? 1 : Math.Min(1, (Now - t.T0) / t.Dur);
            t.Apply(t.Ease(k));
            if (k >= 1) t.Done = true;
        }
    }

    // ---------------------------------------------------------------------
    static void Script(MainVM vm, TextBox input)
    {
        var win = ctl.Win;
        double t = 0;

        // ===== 开场 =====
        At(0, () => { bigTitle = "LiteTodo"; });
        Tween(0.2, 0.8, k => bigAlpha = k);
        Tween(0.9, 0.9, k => { wAlpha = k; wSlideY = 30 * (1 - k); }, null, EaseOut);
        Tween(3.0, 0.6, k => bigAlpha = 1 - k);
        At(3.6, () => bigTitle = null);
        Tween(3.2, 0.5, k => cursorAlpha = k);

        // ===== 1. 输入添加 =====
        t = 3.6;
        Caption(t, "回车添加，!!! 设为高优先级", "Type and press Enter · !!! marks it high priority");
        CursorTo(t, 0.9, () => InWin(input, 0.3, 0.5));
        Click(t + 1.0);
        At(t + 1.0, () => ctl.DebugComposer(true));
        const string typed = "准备周会材料 !!! #蓝";
        for (int i = 1; i <= typed.Length; i++)
        {
            int n = i;
            At(t + 1.3 + i * 0.09, () => { input.Text = typed.Substring(0, n); input.CaretIndex = n; });
        }
        double tEnter = t + 1.3 + typed.Length * 0.09 + 0.5;
        At(tEnter, () => { Call("AddFromInput"); ctl.DebugComposer(false); });

        // ===== 2. 粘贴多行 =====
        t = tEnter + 1.4;
        Caption(t, "粘贴多行文字，一次添加多项", "Paste a list to add several tasks at once");
        At(t + 0.8, () => Call("AddLines", "回复客户邮件\n整理本月发票\n预订周五的会议室"));
        At(t + 0.8, () => Call("ShowHud", "已添加 3 项", 1800));

        // ===== 3. 完成 =====
        t += 3.2;
        Caption(t, "点圆圈完成，自动收进「已完成」", "Check it off — it slides into Done");
        CursorTo(t + 0.2, 0.9, () => InWin(CheckOf(ItemByText("买菜"))));
        Click(t + 1.2);
        At(t + 1.2, () => Call("ToggleDone", ItemByText("买菜")));

        // ===== 4. 右键菜单 =====
        t += 3.4;
        Caption(t, "右键设置优先级与颜色标签", "Right-click for priority and color tags");
        CursorTo(t + 0.2, 0.8, () => InWin((FrameworkElement)Call("ContainerOf", ItemByText("给妈妈")), 0.55, 0.5));
        Click(t + 1.1);
        At(t + 1.1, () =>
        {
            ctl.DebugMenuFor(ItemByText("给妈妈"));
            popupEl = ctl.MenuPopup.Child as FrameworkElement;
            popupPos = new Point(cursor.X - 14 * S + 6, cursor.Y - 14 * S + 6);
        });
        CursorTo(t + 1.6, 0.6, () => InPopup(ButtonIn(popupEl, b => (b.Tag as string) == "setPrio" && ((Choice)b.DataContext).Level == 2)));
        Click(t + 2.3);
        At(t + 2.3, () => ClickTag("setPrio", vm.PrioChoices.First(c => c.Level == 2)));
        CursorTo(t + 2.6, 0.6, () => InPopup(ButtonIn(popupEl, b => (b.Tag as string) == "setColor" && ((Choice)b.DataContext).Name == "粉")));
        Click(t + 3.3);
        At(t + 3.3, () => ClickTag("setColor", vm.ColorChoices.First(c => c.Name == "粉")));
        At(t + 4.0, () => popupEl = null);

        // ===== 5. 主题 / 风格 =====
        t += 4.4;
        Caption(t, "9 种主题 · 4 种显示风格", "9 themes · 4 layouts");
        Tween(t, 0.4, k => cursorAlpha = 1 - k);
        string[] themes = { "aurora", "daylight", "sakura", "mint", "paper", "ocean", "midnight" };
        for (int i = 0; i < themes.Length; i++) { var th = themes[i]; At(t + 0.5 + i * 0.55, () => vm.SelectTheme(th)); }
        double tl = t + 0.5 + themes.Length * 0.55;
        string[] layouts = { "card", "compact", "minimal", "comfy" };
        for (int i = 0; i < layouts.Length; i++) { var ly = layouts[i]; At(tl + i * 0.7, () => vm.SelectLayout(ly)); }

        // ===== 6. 透明度 =====
        t = tl + layouts.Length * 0.7 + 0.3;
        Caption(t, "Ctrl + 滚轮，随心调透明度", "Ctrl + scroll wheel to set transparency");
        double[] ops = { 0.75, 0.6, 0.45, 0.3, 0.45, 0.6, 0.75, 0.88 };
        for (int i = 0; i < ops.Length; i++)
        {
            var o = ops[i];
            At(t + 0.6 + i * 0.32, () => { vm.BgOpacity = o; Call("ShowHud", "背景不透明度 " + (int)Math.Round(o * 100) + "%", 900); });
        }

        // ===== 7. 贴边收起 =====
        t += 0.6 + ops.Length * 0.32 + 0.6;
        Caption(t, "拖到屏幕边缘，自动收起", "Drag it to the screen edge — it tucks away");
        At(t - 0.4, () => cursor = new Point(wx - 160, wy + 420));
        Tween(t - 0.4, 0.4, k => cursorAlpha = k);
        CursorTo(t, 0.7, () => InWin(F<FrameworkElement>("Header"), 0.35, 0.4));
        Click(t + 0.8);
        // 拖到右边缘
        double dragStart = t + 0.9, dragDur = 1.0, m = 14 * S;
        double shownX = VW - (ctl.Win.ActualWidth * S) + m;
        double startX = 0;
        Point grab = default;
        Tween(dragStart, dragDur, k =>
        {
            wx = startX + (shownX - startX) * k;
            cursor = new Point(grab.X + (wx - startX), grab.Y);
            if (k > 0.75) Call("SetDockHint", MainController.Edge.Right);
        }, () => { startX = wx; grab = cursor; });
        At(dragStart + dragDur + 0.4, () => { Call("SetDockHint", MainController.Edge.None); ctl.DebugDock("right", false); });
        // 鼠标移开 → 收起
        double hideAt = dragStart + dragDur + 1.6;
        CursorTo(hideAt - 0.6, 0.6, () => new Point(VW - 620, cursor.Y + 120));
        double hiddenX = VW - 4 * S - m;
        Tween(hideAt, 0.45, k => wx = shownX + (hiddenX - shownX) * k, () => ctl.DebugDockHideVisual());
        // 碰到边缘 → 展开
        double peekAt = hideAt + 1.2;
        CursorTo(peekAt, 0.7, () => new Point(VW - 3, cursor.Y - 60));
        Tween(peekAt + 0.8, 0.38, k => wx = hiddenX + (shownX - hiddenX) * k, () => ctl.DebugDockShowVisual(), EaseOut);
        // 移开 → 再收起
        CursorTo(peekAt + 1.9, 0.6, () => new Point(VW - 700, cursor.Y + 40));
        Tween(peekAt + 2.6, 0.45, k => wx = shownX + (hiddenX - shownX) * k, () => ctl.DebugDockHideVisual());

        // ===== 8. 折叠为标题栏 =====
        t = peekAt + 3.6;
        Caption(t, "双击标题栏，折叠成一条", "Double-click the title bar to fold it");
        double backX = 1180;
        Tween(t + 0.2, 0.6, k => wx = hiddenX + (backX - hiddenX) * k, () => { ctl.DebugUndock(); }, EaseOut);
        CursorTo(t + 0.5, 0.7, () => InWin(F<FrameworkElement>("Header"), 0.3, 0.4));
        Click(t + 1.3); Click(t + 1.42);
        At(t + 1.42, () => ctl.DebugMini());
        Click(t + 3.4); Click(t + 3.52);
        At(t + 3.52, () => ctl.DebugMini());

        // ===== 结尾 =====
        t += 4.8;
        Caption(t, "", "");
        Tween(t, 0.5, k => cursorAlpha = 1 - k);
        At(t + 0.3, () => bigTitle = "LiteTodo");
        Tween(t + 0.3, 0.8, k => bigAlpha = k);
        endTime = t + 3.6;
    }

    static void ClickTag(string tag, object dc)
    {
        var b = new Button { Tag = tag, DataContext = dc };
        Call("OnClick", b, new RoutedEventArgs(ButtonBase.ClickEvent, b));
    }

    // ---------------------------------------------------------------------
    static void BuildBackground()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0x10, 0x16, 0x34), 0));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0x33, 0x23, 0x63), 0.45));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0x8E, 0x3E, 0x7E), 0.78));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0xF0, 0x8E, 0x6A), 1));
            dc.DrawRectangle(g, null, new Rect(0, 0, VW, VH));
            // 柔光圆斑（让透明效果看得见）
            void Blob(double x, double y, double r, Color c, double a)
            {
                var rb = new RadialGradientBrush(Color.FromArgb((byte)(a * 255), c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
                dc.DrawEllipse(rb, null, new Point(x, y), r, r);
            }
            Blob(1500, 300, 420, Color.FromRgb(0x5E, 0xC8, 0xFF), 0.55);
            Blob(1300, 820, 380, Color.FromRgb(0xFF, 0x7A, 0xB6), 0.5);
            Blob(1750, 650, 300, Color.FromRgb(0xFF, 0xC8, 0x6B), 0.55);
            Blob(300, 900, 500, Color.FromRgb(0x6E, 0x5B, 0xFF), 0.35);
            // 任务栏
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 18, 18, 28)), null, new Rect(0, VH - 52, VW, 52));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), null, new Rect(0, VH - 52, VW, 1));
            Color[] icons = { Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0x10, 0xB9, 0x81),
                              Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0x14, 0xB8, 0xA6) };
            double ix = VW / 2.0 - icons.Length * 46 / 2.0;
            for (int i = 0; i < icons.Length; i++)
                dc.DrawRoundedRectangle(new SolidColorBrush(icons[i]), null, new Rect(ix + i * 46 + 7, VH - 42, 32, 32), 8, 8);
            var ft = Text("14:32", 15, Brushes.White, false);
            dc.DrawText(ft, new Point(VW - 80, VH - 36));
        }
        bgBmp = new RenderTargetBitmap(VW, VH, 96, 96, PixelFormats.Pbgra32);
        bgBmp.Render(dv);
        bgBmp.Freeze();
    }

    static FormattedText Text(string s, double size, Brush b, bool bold, string font = "Microsoft YaHei UI")
        => new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(font), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            size, b, 1.0);

    static Geometry cursorGeo = Geometry.Parse("M0,0 L0,22 L5.2,17 L9,26 L12.4,24.5 L8.6,15.8 L15.6,15.8 Z");

    static void RenderFrame()
    {
        var w = ctl.Win;
        int ww = (int)Math.Ceiling(w.ActualWidth * S), wh = (int)Math.Ceiling(w.ActualHeight * S);
        if (winRt == null || winRt.PixelWidth != ww || winRt.PixelHeight != wh)
            winRt = new RenderTargetBitmap(ww, wh, 96 * S, 96 * S, PixelFormats.Pbgra32);
        else winRt.Clear();
        winRt.Render(w);

        BitmapSource pop = null; double pw = 0, ph = 0;
        if (popupEl != null)
        {
            pw = popupEl.ActualWidth + 28; ph = popupEl.ActualHeight + 28;
            var rb = new RenderTargetBitmap((int)(pw * S), (int)(ph * S), 96 * S, 96 * S, PixelFormats.Pbgra32);
            rb.Render(popupEl);
            pop = rb;
        }

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(bgBmp, new Rect(0, 0, VW, VH));

            // 字幕 / 标题（左侧）
            if (bigTitle != null && bigAlpha > 0)
            {
                dc.PushOpacity(bigAlpha);
                dc.DrawText(Text(bigTitle, 104, Brushes.White, true, "Segoe UI"), new Point(150, 330));
                dc.DrawText(Text("轻量 · 透明 · 无边框的 Windows 桌面待办", 36, Brushes.White, false), new Point(156, 492));
                dc.DrawText(Text("A featherweight, transparent to-do widget for Windows", 24, new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), false, "Segoe UI"), new Point(158, 552));
                dc.DrawText(Text("免安装  ·  约 210 KB  ·  Windows 10 / 11", 22, new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), false), new Point(158, 612));
                dc.Pop();
            }
            DrawCaption(dc);

            // 窗口
            if (wAlpha > 0)
            {
                dc.PushOpacity(wAlpha);
                dc.DrawImage(winRt, new Rect(wx, wy + wSlideY, ww, wh));
                dc.Pop();
            }
            if (pop != null) dc.DrawImage(pop, new Rect(popupPos.X, popupPos.Y, pw * S, ph * S));

            // 点击波纹
            foreach (var r in ripples.ToList())
            {
                double age = Now - r.T0;
                if (age > 0.5) { ripples.Remove(r); continue; }
                double k = age / 0.5;
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(200 * (1 - k)), 255, 255, 255)), 3), r.P, 8 + 26 * k, 8 + 26 * k);
            }

            // 光标
            if (cursorAlpha > 0)
            {
                dc.PushOpacity(cursorAlpha);
                dc.PushTransform(new TranslateTransform(cursor.X + 2, cursor.Y + 3));
                dc.PushTransform(new ScaleTransform(1.5, 1.5));
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, cursorGeo);
                dc.Pop(); dc.Pop();
                dc.PushTransform(new TranslateTransform(cursor.X, cursor.Y));
                dc.PushTransform(new ScaleTransform(1.5, 1.5));
                dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1.1), cursorGeo);
                dc.Pop(); dc.Pop();
                dc.Pop();
            }
        }
        if (finalRt == null) finalRt = new RenderTargetBitmap(VW, VH, 96, 96, PixelFormats.Pbgra32);
        else finalRt.Clear();
        finalRt.Render(dv);
        finalRt.CopyPixels(frameBuf, VW * 4, 0);
        ffIn.Write(frameBuf, 0, frameBuf.Length);
    }

    static void DrawCaption(DrawingContext dc)
    {
        double age = Now - capT0;
        double kIn = Math.Min(1, Math.Max(0, age / 0.45));
        void Draw(string zh, string en, double alpha, double dy)
        {
            if (alpha <= 0.01 || string.IsNullOrEmpty(zh)) return;
            dc.PushOpacity(alpha);
            // 左侧强调条
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x8B, 0x9B, 0xFF)), null, new Rect(150, 452 + dy, 6, 112), 3, 3);
            dc.DrawText(Text(zh, 50, Brushes.White, true), new Point(184, 450 + dy));
            dc.DrawText(Text(en, 26, new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), false, "Segoe UI"), new Point(186, 526 + dy));
            dc.Pop();
        }
        Draw(capZhOld, capEnOld, 1 - kIn, -24 * EaseOut(kIn));
        Draw(capZh, capEn, kIn, 24 * (1 - EaseOut(kIn)));
    }
}
