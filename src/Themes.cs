using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace LiteTodo
{
    public class Theme
    {
        public string Key, Name;
        public Color Bg1, Bg2, Text, Sub, Accent;
        public bool Dark;
    }

    public class LayoutStyle
    {
        public string Key, Name, Preview;
        public Thickness Padding, Margin, Border, StripeMargin;
        public CornerRadius Radius;
        public bool Surface;
        public double Stripe, FontDelta, Check;
        public bool ShowMeta, ShowDot;
    }

    public static class Themes
    {
        static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        static Theme T(string key, string name, string bg1, string bg2, string text, string sub, string accent, bool dark)
            => new Theme { Key = key, Name = name, Bg1 = C(bg1), Bg2 = C(bg2), Text = C(text), Sub = C(sub), Accent = C(accent), Dark = dark };

        public static readonly List<Theme> All = new List<Theme>
        {
            T("midnight", "夜幕", "#1E2030", "#15161F", "#ECEEF6", "#8B90A7", "#8B9BFF", true),
            T("graphite", "石墨", "#28282B", "#1B1B1D", "#EDEDEF", "#8E8E95", "#5EE0C3", true),
            T("ocean",    "深海", "#10263D", "#0A1828", "#E4F2FD", "#7FA3C2", "#38BDF8", true),
            T("aurora",   "极光", "#241F45", "#0E2B35", "#EEF0FF", "#9AA2C8", "#6EE7B7", true),
            T("dusk",     "暮色", "#301D33", "#1B1220", "#FBEAF0", "#B293A6", "#FB923C", true),
            T("daylight", "晨光", "#FFFFFF", "#F3F5F9", "#1D2129", "#868D99", "#2F6BFF", false),
            T("sakura",   "樱花", "#FFF6F8", "#FFE7EE", "#4A2A35", "#AC8390", "#E8578A", false),
            T("mint",     "薄荷", "#F3FBF7", "#E2F5EB", "#173A2C", "#779A8B", "#12A16B", false),
            T("paper",    "便签", "#FFFCEB", "#FDF0C4", "#3D3320", "#9A8A63", "#C98500", false),
        };

        // 预览图案：36x26 的简单几何
        public static readonly List<LayoutStyle> Layouts = new List<LayoutStyle>
        {
            new LayoutStyle { Key = "comfy", Name = "舒适",
                Preview = "M1,1 H35 V7 H1 Z M1,10 H35 V16 H1 Z M1,19 H35 V25 H1 Z",
                Padding = new Thickness(10, 9, 6, 9), Margin = new Thickness(0, 1, 0, 1), Border = new Thickness(0),
                Radius = new CornerRadius(10), Surface = false, Stripe = 3, StripeMargin = new Thickness(0, 9, 0, 9), FontDelta = 0.5, Check = 18 },
            new LayoutStyle { Key = "compact", Name = "紧凑",
                Preview = "M1,2 H35 V4 H1 Z M1,7 H35 V9 H1 Z M1,12 H35 V14 H1 Z M1,17 H35 V19 H1 Z M1,22 H35 V24 H1 Z",
                Padding = new Thickness(8, 5, 4, 5), Margin = new Thickness(0), Border = new Thickness(0),
                Radius = new CornerRadius(7), Surface = false, Stripe = 2, StripeMargin = new Thickness(0, 5, 0, 5), FontDelta = -0.5, Check = 15 },
            new LayoutStyle { Key = "card", Name = "卡片",
                Preview = "M1,1 H35 V12 H1 Z M1,14 H35 V25 H1 Z",
                Padding = new Thickness(12, 11, 8, 11), Margin = new Thickness(0, 3, 0, 3), Border = new Thickness(1),
                Radius = new CornerRadius(12), Surface = true, Stripe = 3, StripeMargin = new Thickness(0, 12, 0, 12), FontDelta = 0.5, Check = 19, ShowMeta = true },
            new LayoutStyle { Key = "minimal", Name = "极简",
                Preview = "M2,4 a2,2 0 1 1 0.01,0 Z M8,3 H34 V5 H8 Z M2,11 a2,2 0 1 1 0.01,0 Z M8,10 H30 V12 H8 Z M2,18 a2,2 0 1 1 0.01,0 Z M8,17 H32 V19 H8 Z",
                Padding = new Thickness(6, 6, 4, 6), Margin = new Thickness(0), Border = new Thickness(0),
                Radius = new CornerRadius(8), Surface = false, Stripe = 0, FontDelta = 0, Check = 16, ShowDot = true },
        };

        public static Theme Get(string key) => All.FirstOrDefault(t => t.Key == key) ?? All[0];
        public static LayoutStyle GetLayout(string key) => Layouts.FirstOrDefault(l => l.Key == key) ?? Layouts[0];

        static Color A(Color c, double a) => Color.FromArgb((byte)Math.Round(Math.Max(0, Math.Min(1, a)) * 255), c.R, c.G, c.B);

        static Brush Solid(Color c, double a = 1)
        {
            var b = new SolidColorBrush(A(c, a));
            b.Freeze();
            return b;
        }

        public static Brush Gradient(Color c1, Color c2, double a)
        {
            var b = new LinearGradientBrush(A(c1, a), A(c2, a), new Point(0, 0), new Point(0.6, 1));
            b.Freeze();
            return b;
        }

        static Color Mix(Color a, Color b, double t = 0.5) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        /// <summary>把主题 / 风格 / 透明度 / 字号 写入资源字典，界面通过 DynamicResource 自动刷新</summary>
        public static void Apply(ResourceDictionary r, Settings s, bool hoverBoost)
        {
            var t = Get(s.Theme);
            var l = GetLayout(s.Layout);
            double a = hoverBoost ? Math.Max(s.BgOpacity, 0.96) : s.BgOpacity;
            var white = Colors.White;
            var black = Colors.Black;

            // —— 背景层 ——
            r["BgBrush"] = Gradient(t.Bg1, t.Bg2, a);
            var glow = new RadialGradientBrush
            {
                Center = new Point(0.12, 0.0), GradientOrigin = new Point(0.12, 0.0), RadiusX = 0.95, RadiusY = 0.75,
                GradientStops =
                {
                    new GradientStop(A(t.Accent, (t.Dark ? 0.20 : 0.13) * Math.Min(1, a * 1.25)), 0),
                    new GradientStop(A(t.Accent, 0), 1)
                }
            };
            glow.Freeze();
            r["GlowBrush"] = glow;
            var edge = new LinearGradientBrush(
                t.Dark ? A(white, 0.17) : A(black, 0.07),
                t.Dark ? A(white, 0.05) : A(black, 0.11), 90);
            edge.Freeze();
            r["EdgeBrush"] = edge;

            var mid = Mix(t.Bg1, t.Bg2);
            r["PopupBrush"] = Gradient(t.Bg1, t.Bg2, 1.0);
            r["PopupSolidBrush"] = Solid(t.Dark ? Mix(mid, white, 0.04) : mid);

            // —— 文字 / 强调色 ——
            r["TextBrush"] = Solid(t.Text);
            r["SubTextBrush"] = Solid(t.Sub);
            r["FaintTextBrush"] = Solid(t.Sub, 0.7);
            r["AccentBrush"] = Solid(t.Accent);
            r["AccentSoftBrush"] = Solid(t.Accent, t.Dark ? 0.18 : 0.13);
            r["AccentLineBrush"] = Solid(t.Accent, 0.55);
            r["OnAccentBrush"] = Solid(t.Dark ? Mix(t.Bg2, black, 0.3) : white);

            // —— 面 / 线 ——
            r["LineBrush"] = Solid(t.Text, t.Dark ? 0.09 : 0.08);
            r["InputBrush"] = Solid(t.Text, t.Dark ? 0.065 : 0.045);
            r["InputHoverBrush"] = Solid(t.Text, t.Dark ? 0.11 : 0.08);
            r["CardBrush"] = t.Dark ? Solid(white, 0.045) : Solid(white, 0.65);
            r["CardLineBrush"] = Solid(t.Text, t.Dark ? 0.06 : 0.07);
            r["SurfaceBrush"] = t.Dark ? Solid(white, 0.055) : Solid(white, 0.7);
            r["SurfaceHoverBrush"] = t.Dark ? Solid(white, 0.095) : Solid(white, 0.95);
            r["HoverBrush"] = Solid(t.Text, t.Dark ? 0.06 : 0.045);
            r["SelectBrush"] = Solid(t.Accent, t.Dark ? 0.14 : 0.10);
            r["DangerBrush"] = Solid(C("#F2555A"));
            r["DangerSoftBrush"] = Solid(C("#F2555A"), 0.14);
            r["ShadowOpacity"] = t.Dark ? 0.55 : 0.22;

            // —— 优先级（勾选圈：描边 + 淡色填充） ——
            r["P3Brush"] = Solid(C(Palette.P3)); r["P3Soft"] = Solid(C(Palette.P3), 0.14);
            r["P2Brush"] = Solid(C(Palette.P2)); r["P2Soft"] = Solid(C(Palette.P2), 0.14);
            r["P1Brush"] = Solid(C(Palette.P1)); r["P1Soft"] = Solid(C(Palette.P1), 0.14);

            // —— 显示风格 ——
            r["ItemPadding"] = l.Padding;
            r["ItemMargin"] = l.Margin;
            r["ItemBorderThick"] = l.Border;
            r["ItemRadius"] = l.Radius;
            r["ItemBgBrush"] = l.Surface ? r["SurfaceBrush"] : Brushes.Transparent;
            r["ItemHoverBrush"] = l.Surface ? r["SurfaceHoverBrush"] : r["HoverBrush"];
            r["ItemLineBrush"] = l.Surface ? r["CardLineBrush"] : Brushes.Transparent;
            r["StripeWidth"] = l.Stripe;
            r["StripeMargin"] = l.StripeMargin;
            r["CheckSize"] = l.Check;
            r["MetaVis"] = l.ShowMeta ? Visibility.Visible : Visibility.Collapsed;
            r["DotVis"] = l.ShowDot ? Visibility.Visible : Visibility.Collapsed;

            r["BaseFontSize"] = s.FontSize;
            r["ItemFontSize"] = s.FontSize + l.FontDelta;
            r["MetaFontSize"] = Math.Max(10.0, s.FontSize - 2.5);
            r["TitleFontSize"] = s.FontSize + 6;
        }
    }
}
