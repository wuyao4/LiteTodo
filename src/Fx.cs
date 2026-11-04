using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace LiteTodo
{
    /// <summary>动画与附加属性（XAML 中通过 xmlns:l 引用）</summary>
    public static class Fx
    {
        static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
        static readonly IEasingFunction EaseIn = Freeze(new CubicEase { EasingMode = EasingMode.EaseIn });
        static IEasingFunction Freeze(CubicEase e) { e.Freeze(); return e; }
        /// <summary>全局动画时间倍数（录制演示视频时放慢，逐帧渲染后再按正常速度编码）</summary>
        public static double TimeScale = 1;
        public static TimeSpan T(double ms) => TimeSpan.FromMilliseconds(ms * TimeScale);
        static Duration Ms(double ms) => new Duration(T(ms));

        // ================= 新条目入场动画 =================

        public static readonly DependencyProperty AppearProperty = DependencyProperty.RegisterAttached(
            "Appear", typeof(bool), typeof(Fx), new PropertyMetadata(false, (d, e) =>
            {
                if (d is FrameworkElement fe && (bool)e.NewValue) fe.Loaded += OnAppearLoaded;
            }));
        public static bool GetAppear(DependencyObject d) => (bool)d.GetValue(AppearProperty);
        public static void SetAppear(DependencyObject d, bool v) => d.SetValue(AppearProperty, v);

        static void OnAppearLoaded(object sender, RoutedEventArgs e)
        {
            var fe = (FrameworkElement)sender;
            if (fe.DataContext is TodoItem it && it.IsNew)
            {
                it.IsNew = false;
                SlideIn(fe, 10);
            }
        }

        /// <summary>淡入 + 轻微上移。动画结束后释放属性，不影响触发器</summary>
        public static void SlideIn(UIElement el, double dy, double ms = 280, double delay = 0)
        {
            var tt = EnsureTranslate(el);
            var begin = T(delay);
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(ms))
            { EasingFunction = EaseOut, BeginTime = begin, FillBehavior = FillBehavior.Stop });
            if (delay > 0) el.Opacity = 1;
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, Ms(ms))
            { EasingFunction = EaseOut, BeginTime = begin, FillBehavior = FillBehavior.Stop });
        }

        public static void SlideX(UIElement el, double from, double to, double ms, Action done = null)
        {
            var tt = EnsureTranslate(el);
            var a = new DoubleAnimation(from, to, Ms(ms)) { EasingFunction = to == 0 ? EaseOut : EaseIn };
            if (done != null) a.Completed += (s, e) => done();
            tt.BeginAnimation(TranslateTransform.XProperty, a);
        }

        static TranslateTransform EnsureTranslate(UIElement el)
        {
            if (el.RenderTransform is TranslateTransform t && !t.IsFrozen) return t;
            var n = new TranslateTransform();
            el.RenderTransform = n;
            return n;
        }

        /// <summary>高度收起 + 淡出（用于删除 / 移动到另一分组）</summary>
        public static void CollapseOut(FrameworkElement el, Action done, double ms = 220)
        {
            var st = new ScaleTransform(1, 1);
            el.LayoutTransform = st;
            bool fired = false;
            var a = new DoubleAnimation(1, 0, Ms(ms)) { EasingFunction = EaseIn };
            a.Completed += (s, e) =>
            {
                if (fired) return;
                fired = true;
                done?.Invoke();
                // 若容器被复用，恢复原状
                el.BeginAnimation(UIElement.OpacityProperty, null);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                el.LayoutTransform = Transform.Identity;
            };
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, Ms(ms * 0.8)));
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        /// <summary>勾选时的弹跳</summary>
        public static void Pop(UIElement el)
        {
            var st = new ScaleTransform(1, 1);
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = st;
            var a = new DoubleAnimationUsingKeyFrames { Duration = Ms(360) };
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0.72, KeyTime.FromTimeSpan(T(90)), EaseOut));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1.16, KeyTime.FromTimeSpan(T(220)), EaseOut));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(T(360)), EaseOut));
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        public static void Fade(UIElement el, double to, double ms = 160, Action done = null)
        {
            var a = new DoubleAnimation(to, Ms(ms)) { EasingFunction = EaseOut };
            if (done != null) a.Completed += (s, e) => done();
            el.BeginAnimation(UIElement.OpacityProperty, a);
        }

        /// <summary>LayoutTransform 纵向展开 / 收起（自适应内容高度）</summary>
        public static void Expand(FrameworkElement el, bool open, double ms = 180)
        {
            if (!(el.LayoutTransform is ScaleTransform st) || st.IsFrozen)
            {
                st = new ScaleTransform(1, open ? 0 : 1);
                el.LayoutTransform = st;
            }
            if (open) el.Visibility = Visibility.Visible;
            var a = new DoubleAnimation(open ? 1 : 0, Ms(ms)) { EasingFunction = open ? EaseOut : EaseIn };
            if (!open) a.Completed += (s, e) => { if (st.ScaleY < 0.01) el.Visibility = Visibility.Collapsed; };
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(open ? 1 : 0, Ms(ms)));
        }

        // ================= 进度环 =================

        public static readonly DependencyProperty RingProgressProperty = DependencyProperty.RegisterAttached(
            "RingProgress", typeof(double), typeof(Fx), new PropertyMetadata(double.NaN, (d, e) =>
            {
                if (!(d is Shape s)) return;
                s.SizeChanged -= OnRingSize;
                s.SizeChanged += OnRingSize;
                UpdateRing(s);
            }));
        public static double GetRingProgress(DependencyObject d) => (double)d.GetValue(RingProgressProperty);
        public static void SetRingProgress(DependencyObject d, double v) => d.SetValue(RingProgressProperty, v);

        static void OnRingSize(object sender, SizeChangedEventArgs e) => UpdateRing((Shape)sender);

        static void UpdateRing(Shape s)
        {
            double v = GetRingProgress(s);
            double p = double.IsNaN(v) ? 0 : Math.Max(0, Math.Min(1, v));
            double t = s.StrokeThickness, d = s.ActualWidth - t;
            if (d <= 0 || t <= 0) return;
            double c = Math.PI * d / t; // 周长（以线宽为单位）
            s.StrokeDashArray = new DoubleCollection { p <= 0.001 ? 0 : c * p, c + 10 };
        }
    }
}
