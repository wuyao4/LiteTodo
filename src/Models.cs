using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Media;

namespace LiteTodo
{
    [DataContract]
    public abstract class Notify : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    /// <summary>一条待办</summary>
    [DataContract]
    public class TodoItem : Notify
    {
        static readonly Brush NoBrush = Brushes.Transparent;

        string _text = "", _color = "";
        bool _done, _editing, _dragging, _selected;
        int _prio;
        string _editText;

        [DataMember(Order = 0)] public string Id { get; set; }

        [DataMember(Order = 1)]
        public string Text { get => _text; set => Set(ref _text, value ?? ""); }

        [DataMember(Order = 2)]
        public bool IsDone
        {
            get => _done;
            set
            {
                if (!Set(ref _done, value)) return;
                DoneAt = value ? DateTime.Now : (DateTime?)null;
                Raise(nameof(MetaText));
            }
        }

        /// <summary>0 无, 1 低, 2 中, 3 高</summary>
        [DataMember(Order = 3)]
        public int Priority
        {
            get => _prio;
            set
            {
                value = Math.Max(0, Math.Min(3, value));
                if (!Set(ref _prio, value)) return;
                Raise(nameof(PrioBrush)); Raise(nameof(PrioText));
            }
        }

        /// <summary>颜色标签 (#RRGGBB)，空字符串表示无</summary>
        [DataMember(Order = 4)]
        public string Color
        {
            get => _color;
            set
            {
                if (!Set(ref _color, value ?? "")) return;
                Raise(nameof(TagBrush)); Raise(nameof(HasColor));
            }
        }

        [DataMember(Order = 5)] public DateTime Created { get; set; }
        [DataMember(Order = 6)] public DateTime? DoneAt { get; set; }

        // ---------- 以下为界面状态，不保存 ----------
        public bool IsEditing { get => _editing; set => Set(ref _editing, value); }
        public string EditText { get => _editText; set => Set(ref _editText, value); }
        public bool IsDragging { get => _dragging; set => Set(ref _dragging, value); }
        public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
        /// <summary>新出现的条目（播放入场动画后清除）</summary>
        public bool IsNew { get; set; }
        /// <summary>当前显示在“已完成”分组中（用于判断是否需要移动动画）</summary>
        internal bool InDoneSection;
        internal int SortIndex;

        public bool HasColor => !string.IsNullOrEmpty(_color);
        public Brush TagBrush => HasColor ? Palette.BrushOf(_color) : NoBrush;
        public Brush PrioBrush => Palette.PrioBrush(_prio);
        public string PrioText => Palette.PrioName(_prio);

        public string MetaText
        {
            get
            {
                if (IsDone && DoneAt.HasValue) return "完成于 " + Fmt(DoneAt.Value);
                return Created == default(DateTime) ? "" : "创建于 " + Fmt(Created);
            }
        }

        static string Fmt(DateTime t)
        {
            var now = DateTime.Now;
            if (t.Date == now.Date) return "今天 " + t.ToString("HH:mm");
            if (t.Date == now.Date.AddDays(-1)) return "昨天 " + t.ToString("HH:mm");
            return t.Year == now.Year ? t.ToString("M月d日 HH:mm") : t.ToString("yyyy-M-d");
        }

        public static TodoItem New(string text, int prio, string color)
        {
            return new TodoItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Text = text,
                Priority = prio,
                Color = color,
                Created = DateTime.Now
            };
        }
    }

    /// <summary>颜色 / 优先级常量</summary>
    public static class Palette
    {
        public static readonly string[][] Colors =
        {
            new[] { "",        "无" },
            new[] { "#F87171", "红" },
            new[] { "#FB923C", "橙" },
            new[] { "#FACC15", "黄" },
            new[] { "#34D399", "绿" },
            new[] { "#22D3EE", "青" },
            new[] { "#60A5FA", "蓝" },
            new[] { "#A78BFA", "紫" },
            new[] { "#F472B6", "粉" },
        };

        public const string P3 = "#F2555A", P2 = "#F59E0B", P1 = "#3B82F6";

        static readonly Dictionary<string, Brush> cache = new Dictionary<string, Brush>();

        public static Brush BrushOf(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Brushes.Transparent;
            if (cache.TryGetValue(hex, out var b)) return b;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                b = new SolidColorBrush(c);
                b.Freeze();
            }
            catch { b = Brushes.Transparent; }
            cache[hex] = b;
            return b;
        }

        public static Brush PrioBrush(int p)
        {
            switch (p)
            {
                case 3: return BrushOf(P3);
                case 2: return BrushOf(P2);
                case 1: return BrushOf(P1);
                default: return Brushes.Transparent;
            }
        }

        public static Brush Soft(string hex, double alpha)
        {
            var key = hex + "@" + alpha;
            if (cache.TryGetValue(key, out var b)) return b;
            var c = (Color)ColorConverter.ConvertFromString(hex);
            b = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
            b.Freeze();
            cache[key] = b;
            return b;
        }

        public static string NameToColor(string name)
        {
            foreach (var c in Colors) if (c[0] != "" && c[1] == name) return c[0];
            return null;
        }

        public static string PrioName(int p)
        {
            switch (p)
            {
                case 3: return "高";
                case 2: return "中";
                case 1: return "低";
                default: return "";
            }
        }
    }

    /// <summary>设置面板里各种可选项（主题、风格、颜色、优先级）</summary>
    public class Choice : Notify
    {
        bool _sel;
        public string Key { get; set; }
        public string Name { get; set; }
        public int Level { get; set; }
        public Brush Brush { get; set; }
        public Brush Brush2 { get; set; }
        public string Glyph { get; set; }
        public Geometry Geo { get; set; }
        public bool IsSelected { get => _sel; set => Set(ref _sel, value); }
    }

    [DataContract]
    public class Settings
    {
        [DataMember] public bool HasPosition { get; set; }
        [DataMember] public double Left { get; set; }
        [DataMember] public double Top { get; set; }
        [DataMember] public double Width { get; set; }
        [DataMember] public double Height { get; set; }
        [DataMember] public string Theme { get; set; }
        [DataMember] public string Layout { get; set; }
        [DataMember] public double BgOpacity { get; set; }
        [DataMember] public double WinOpacity { get; set; }
        [DataMember] public double FontSize { get; set; }
        [DataMember] public bool Topmost { get; set; }
        [DataMember] public bool HoverOpaque { get; set; }
        [DataMember] public bool HideTaskbar { get; set; }
        [DataMember] public bool SortByPriority { get; set; }
        [DataMember] public string Filter { get; set; }
        [DataMember] public string Title { get; set; }
        [DataMember] public bool DoneCollapsed { get; set; }
        [DataMember] public bool Mini { get; set; }
        /// <summary>不在 Alt+Tab 中显示（工具窗口样式，同时没有任务栏按钮）</summary>
        [DataMember] public bool HideAltTab { get; set; }
        /// <summary>拖到屏幕边缘时自动收起</summary>
        [DataMember] public bool EdgeDock { get; set; }
        /// <summary>当前贴靠的边缘：left / right / top，空为未贴边</summary>
        [DataMember] public string Dock { get; set; }
        /// <summary>置于其他窗口之下（桌面小部件模式）</summary>
        [DataMember] public bool BottomMost { get; set; }
        [DataMember] public bool DockTipShown { get; set; }
        /// <summary>设置格式版本，用于升级旧配置</summary>
        [DataMember] public int Version { get; set; }

        const int CurrentVersion = 2;

        public Settings() { Defaults(); Version = CurrentVersion; }

        [OnDeserializing]
        void OnDeserializing(StreamingContext c) { Defaults(); }

        void Defaults()
        {
            Width = 360; Height = 540;
            Theme = "midnight"; Layout = "comfy";
            BgOpacity = 0.88; WinOpacity = 1.0; FontSize = 13.5;
            Filter = "all"; Title = "待办事项";
            HoverOpaque = true;
            EdgeDock = true;
            Dock = "";
            Version = 0; // 旧文件没有该字段 → 0，触发升级
        }

        public void Normalize()
        {
            // v2：默认开启「鼠标移入时完全不透明」（旧版本保存的是 false 默认值）
            if (Version < 2) HoverOpaque = true;
            Version = CurrentVersion;

            Width = Clamp(Width, 260, 1600, 360);
            Height = Clamp(Height, 220, 2000, 540);
            BgOpacity = Clamp(BgOpacity, 0.0, 1.0, 0.88);
            WinOpacity = Clamp(WinOpacity, 0.3, 1.0, 1.0);
            FontSize = Clamp(FontSize, 11, 22, 13.5);
            if (string.IsNullOrWhiteSpace(Theme)) Theme = "midnight";
            if (string.IsNullOrWhiteSpace(Layout)) Layout = "comfy";
            if (Filter != "active" && Filter != "done") Filter = "all";
            if (string.IsNullOrWhiteSpace(Title)) Title = "待办事项";
            if (Dock != "left" && Dock != "right" && Dock != "top") Dock = "";
            if (Topmost && BottomMost) BottomMost = false;
        }

        static double Clamp(double v, double min, double max, double def)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return def;
            return Math.Max(min, Math.Min(max, v));
        }
    }

    [DataContract]
    public class AppData
    {
        [DataMember(Order = 0)] public Settings Settings { get; set; }
        [DataMember(Order = 1)] public List<TodoItem> Items { get; set; }
    }

    /// <summary>首次运行时的示例任务，顺便教会主要操作</summary>
    public static class Tutorial
    {
        public static List<TodoItem> Create()
        {
            var now = DateTime.Now;
            int n = 0;
            TodoItem T(string text, int prio, string color, bool done = false)
            {
                var it = TodoItem.New(text, prio, color);
                it.Created = now.AddSeconds(n++);
                if (done) it.IsDone = true;
                return it;
            }
            return new List<TodoItem>
            {
                T("欢迎使用 LiteTodo，点左边的圆圈完成任务", 0, ""),
                T("把窗口拖到屏幕左 / 右 / 上边缘，它会自动收起", 2, "#60A5FA"),
                T("右键任务可以设置优先级和颜色标签", 3, "#F87171"),
                T("双击文字编辑，按住拖动调整顺序", 1, ""),
                T("输入时加 !!! 设为高优先级，加 #绿 设颜色", 0, "#34D399"),
                T("Ctrl + 滚轮调透明度，Ctrl + Alt + T 随时唤出", 0, "#A78BFA"),
                T("完成的任务会收进「已完成」，可以一键清除", 0, "", true),
            };
        }
    }

    /// <summary>JSON 读写。exe 同目录存在 todo-data.json 时为便携模式，否则存放在 %APPDATA%\LiteTodo</summary>
    public static class Store
    {
        public static readonly string FilePath;

        static Store()
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var portable = Path.Combine(exeDir, "todo-data.json");
            var over = Environment.GetEnvironmentVariable("LITETODO_DATA");
            if (!string.IsNullOrEmpty(over))
                FilePath = Path.GetFullPath(over);
            else if (File.Exists(portable) || File.Exists(Path.Combine(exeDir, "portable")))
                FilePath = portable;
            else
                FilePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LiteTodo", "todo-data.json");
        }

        static DataContractJsonSerializer Ser()
        {
            return new DataContractJsonSerializer(typeof(AppData), new DataContractJsonSerializerSettings
            {
                DateTimeFormat = new DateTimeFormat("yyyy-MM-dd'T'HH:mm:ss")
            });
        }

        public static AppData Load()
        {
            AppData d = null;
            if (File.Exists(FilePath))
            {
                try
                {
                    using (var fs = File.OpenRead(FilePath))
                        d = (AppData)Ser().ReadObject(fs);
                }
                catch
                {
                    // 文件损坏：先备份，绝不覆盖用户数据
                    try { File.Copy(FilePath, FilePath + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".bak", true); } catch { }
                }
            }
            bool firstRun = d == null && !File.Exists(FilePath);
            d = d ?? new AppData();
            if (firstRun) d.Items = Tutorial.Create();
            d.Settings = d.Settings ?? new Settings();
            d.Settings.Normalize();
            d.Items = d.Items ?? new List<TodoItem>();
            d.Items.RemoveAll(i => i == null);
            foreach (var i in d.Items)
                if (string.IsNullOrEmpty(i.Id)) i.Id = Guid.NewGuid().ToString("N");
            return d;
        }

        public static void Save(AppData d)
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var tmp = FilePath + ".tmp";
            using (var fs = File.Create(tmp))
            using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, false, true, "  "))
            {
                Ser().WriteObject(w, d);
                w.Flush();
            }
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }
    }
}
