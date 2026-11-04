using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LiteTodo
{
    public class MainVM : Notify
    {
        public readonly AppData Data;
        public Settings S => Data.Settings;

        public ObservableCollection<TodoItem> Items { get; }
        public ListCollectionView ActiveView { get; }
        public ListCollectionView DoneView { get; }
        public TodoItem Draft { get; } = new TodoItem();

        public List<Choice> Themes { get; }
        public List<Choice> Layouts { get; }
        public List<Choice> ColorChoices { get; }   // 条目菜单
        public List<Choice> PrioChoices { get; }    // 条目菜单
        public List<Choice> DraftPrios { get; }     // 输入栏
        public List<Choice> DraftColors { get; }    // 输入栏

        public event Action SaveRequested;
        public event Action AppearanceChanged;
        public event Action WinOpacityChanged;
        /// <summary>某条目的完成状态被切换（由界面决定何时移动分组）</summary>
        public event Action<TodoItem> DoneToggled;

        readonly DispatcherTimer sortTimer;
        static readonly CultureInfo zh = new CultureInfo("zh-CN");

        public MainVM(AppData data)
        {
            Data = data;
            Items = new ObservableCollection<TodoItem>(data.Items);
            foreach (var i in Items) { i.PropertyChanged += ItemChanged; i.InDoneSection = i.IsDone; }

            // 先创建视图再订阅，保证视图先于我们收到集合变更
            ActiveView = new ListCollectionView(Items) { Filter = o => !((TodoItem)o).IsDone };
            DoneView = new ListCollectionView(Items) { Filter = o => ((TodoItem)o).IsDone, CustomSort = new DoneComparer() };
            Items.CollectionChanged += OnCollectionChanged;

            Themes = global::LiteTodo.Themes.All.Select(t => new Choice
            {
                Key = t.Key, Name = t.Name,
                Brush = global::LiteTodo.Themes.Gradient(t.Bg1, t.Bg2, 1),
                Brush2 = Palette.BrushOf(t.Accent.ToString()),
                IsSelected = t.Key == S.Theme
            }).ToList();
            Layouts = global::LiteTodo.Themes.Layouts.Select(l => new Choice
            {
                Key = l.Key, Name = l.Name, Geo = Freeze(Geometry.Parse(l.Preview)), IsSelected = l.Key == S.Layout
            }).ToList();

            ColorChoices = Palette.Colors.Select(c => new Choice
            {
                Key = c[0], Name = c[1], Brush = Palette.BrushOf(c[0]), Glyph = c[0] == "" ? "" : ""
            }).ToList();
            PrioChoices = new[] { 3, 2, 1, 0 }.Select(p => new Choice
            {
                Level = p, Name = p == 0 ? "无" : Palette.PrioName(p),
                Brush = p == 0 ? Palette.BrushOf("#9096A6") : Palette.PrioBrush(p),
                Brush2 = p == 0 ? Palette.Soft("#9096A6", 0.16) : PrioSoft(p)
            }).ToList();

            DraftPrios = new[] { 3, 2, 1 }.Select(p => new Choice
            {
                Level = p, Name = Palette.PrioName(p), Brush = Palette.PrioBrush(p), Brush2 = PrioSoft(p),
                Glyph = Palette.PrioName(p) + "优先级（也可在开头输入 " + new string('!', p) + "）"
            }).ToList();
            DraftColors = Palette.Colors.Where(c => c[0] != "").Select(c => new Choice
            {
                Key = c[0], Name = c[1], Brush = Palette.BrushOf(c[0]), Glyph = c[1] + "色标签（也可输入 #" + c[1] + "）"
            }).ToList();

            sortTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            sortTimer.Tick += (s, e) => { sortTimer.Stop(); RefreshViews(); };

            ApplySort();
            UpdateStats();
            foreach (var c in LayerChoices) c.IsSelected = c.Key == Layer;
        }

        static Geometry Freeze(Geometry g) { g.Freeze(); return g; }
        static Brush PrioSoft(int p) => Palette.Soft(p == 3 ? Palette.P3 : p == 2 ? Palette.P2 : Palette.P1, 0.16);

        // ================= 集合 / 条目变化 =================

        void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null) foreach (TodoItem i in e.OldItems) i.PropertyChanged -= ItemChanged;
            if (e.NewItems != null) foreach (TodoItem i in e.NewItems) { i.PropertyChanged += ItemChanged; i.InDoneSection = i.IsDone; }
            UpdateStats();
            RequestSave();
        }

        void ItemChanged(object sender, PropertyChangedEventArgs e)
        {
            var it = (TodoItem)sender;
            switch (e.PropertyName)
            {
                case nameof(TodoItem.IsDone):
                    UpdateStats();
                    RequestSave();
                    DoneToggled?.Invoke(it);
                    break;
                case nameof(TodoItem.Priority):
                    RequestSave();
                    if (S.SortByPriority && !it.IsDone) { sortTimer.Stop(); sortTimer.Start(); }
                    break;
                case nameof(TodoItem.Text):
                case nameof(TodoItem.Color):
                    RequestSave();
                    break;
            }
        }

        public void RequestSave() => SaveRequested?.Invoke();

        class PrioComparer : IComparer
        {
            public int Compare(object x, object y)
            {
                var a = (TodoItem)x; var b = (TodoItem)y;
                int c = b.Priority.CompareTo(a.Priority);
                return c != 0 ? c : a.SortIndex.CompareTo(b.SortIndex);
            }
        }

        class DoneComparer : IComparer
        {
            public int Compare(object x, object y)
            {
                var a = (TodoItem)x; var b = (TodoItem)y;
                int c = (b.DoneAt ?? DateTime.MinValue).CompareTo(a.DoneAt ?? DateTime.MinValue);
                return c != 0 ? c : a.SortIndex.CompareTo(b.SortIndex);
            }
        }

        void Reindex()
        {
            for (int i = 0; i < Items.Count; i++) Items[i].SortIndex = i;
        }

        void ApplySort()
        {
            Reindex();
            ActiveView.CustomSort = S.SortByPriority ? new PrioComparer() : null;
        }

        /// <summary>重新过滤 / 排序两个分组</summary>
        public void RefreshViews()
        {
            sortTimer.Stop();
            Reindex();
            foreach (var i in Items) i.InDoneSection = i.IsDone;
            ActiveView.Refresh();
            DoneView.Refresh();
            UpdateStats();
            Raise(nameof(HeaderSubtitle));
        }

        /// <summary>手动排序模式下才允许拖动 / Ctrl+方向键</summary>
        public bool CanReorder => !S.SortByPriority;

        // ================= 统计 =================

        int _total, _active, _done;
        public int DoneCount => _done;
        public bool HasDone => _done > 0;
        public bool DoneExpanded => _done > 0 && !S.DoneCollapsed;
        public bool ShowEmpty => _active == 0;
        public bool AllDone => _total > 0 && _active == 0;
        public double Progress => _total == 0 ? 0 : (double)_done / _total;
        public string ProgressText => _total == 0 || _active == 0 ? "" : ((int)Math.Round(Progress * 100)).ToString();
        public string ProgressTip => _total == 0 ? "还没有任务" : $"已完成 {_done} / {_total}";

        public string Subtitle
        {
            get
            {
                var date = DateTime.Now.ToString("M月d日 dddd", zh);
                if (_total == 0) return date;
                return _active == 0 ? date + " · 全部完成" : $"{date} · 还有 {_active} 项";
            }
        }

        /// <summary>折叠为标题栏时显示“下一项”，平时显示日期与剩余数量</summary>
        public string HeaderSubtitle
        {
            get
            {
                if (S.Mini && _active > 0)
                {
                    var next = ActiveView.Cast<TodoItem>().FirstOrDefault();
                    if (next != null) return "下一项：" + next.Text;
                }
                return Subtitle;
            }
        }

        public string EmptyGlyph => _total == 0 ? "" : "";
        public string EmptyTitle => _total == 0 ? "开始你的第一项任务" : "全部完成，干得漂亮";
        public string EmptyHint => _total == 0 ? "在下方输入内容，按 Enter 添加" : "休息一下，或者继续添加新任务";

        public void UpdateStats()
        {
            _total = Items.Count;
            _done = Items.Count(i => i.IsDone);
            _active = _total - _done;
            foreach (var n in new[] { nameof(DoneCount), nameof(HasDone), nameof(DoneExpanded), nameof(ShowEmpty), nameof(AllDone),
                                      nameof(Progress), nameof(ProgressText), nameof(ProgressTip), nameof(Subtitle),
                                      nameof(EmptyGlyph), nameof(EmptyTitle), nameof(EmptyHint), nameof(HeaderSubtitle) })
                Raise(n);
        }

        // ================= 新增（支持 !!! 优先级、#颜色 语法） =================

        static readonly Regex BangRx = new Regex(@"(?:^|\s)([!！]{1,3})(?=\s|$)");
        static readonly Regex TagRx = new Regex(@"(?:^|\s)[#＃](红|橙|黄|绿|青|蓝|紫|粉)(?=\s|$)");

        public TodoItem Add(string raw)
        {
            var text = (raw ?? "").Trim();
            int? prio = null;
            string color = null;

            var m = BangRx.Match(text);
            if (m.Success)
            {
                prio = m.Groups[1].Length; // ! 低, !! 中, !!! 高
                text = text.Remove(m.Index, m.Length);
            }
            m = TagRx.Match(text);
            if (m.Success)
            {
                color = Palette.NameToColor(m.Groups[1].Value);
                text = text.Remove(m.Index, m.Length);
            }
            text = Regex.Replace(text, @"\s{2,}", " ").Trim();
            if (text.Length == 0) return null;

            var it = TodoItem.New(text, prio ?? Draft.Priority, color ?? Draft.Color);
            it.IsNew = true;
            it.SortIndex = Items.Count;
            Items.Add(it);
            SetDraftPriority(0);
            SetDraftColor("");
            return it;
        }

        public void SetDraftPriority(int level)
        {
            Draft.Priority = level;
            foreach (var c in DraftPrios) c.IsSelected = c.Level == level;
        }

        public void SetDraftColor(string key)
        {
            Draft.Color = key ?? "";
            foreach (var c in DraftColors) c.IsSelected = c.Key == Draft.Color;
        }

        public List<KeyValuePair<int, TodoItem>> Remove(IEnumerable<TodoItem> items)
        {
            var removed = items.Select(i => new KeyValuePair<int, TodoItem>(Items.IndexOf(i), i))
                               .Where(p => p.Key >= 0).OrderByDescending(p => p.Key).ToList();
            foreach (var p in removed) { p.Value.IsSelected = false; Items.RemoveAt(p.Key); }
            removed.Reverse();
            return removed;
        }

        public void Restore(List<KeyValuePair<int, TodoItem>> removed)
        {
            Reindex();
            foreach (var p in removed)
            {
                p.Value.IsNew = true;
                p.Value.SortIndex = p.Key;
                Items.Insert(Math.Min(p.Key, Items.Count), p.Value);
            }
            if (S.SortByPriority) RefreshViews();
        }

        public void MoveToTop(TodoItem it)
        {
            int i = Items.IndexOf(it);
            if (i > 0) Items.Move(i, 0);
            if (S.SortByPriority) RefreshViews();
        }

        /// <summary>在同一分组内上移 / 下移一位</summary>
        public bool MoveWithin(TodoItem it, int dir)
        {
            int i = Items.IndexOf(it);
            if (i < 0) return false;
            for (int j = i + dir; j >= 0 && j < Items.Count; j += dir)
            {
                if (Items[j].IsDone != it.IsDone) continue;
                Items.Move(i, j);
                return true;
            }
            return false;
        }

        // ================= 设置（绑定到界面） =================

        public bool SortByPriority
        {
            get => S.SortByPriority;
            set
            {
                if (S.SortByPriority == value) return;
                S.SortByPriority = value; Raise();
                ApplySort(); RequestSave();
            }
        }

        public bool DoneCollapsed
        {
            get => S.DoneCollapsed;
            set { S.DoneCollapsed = value; Raise(); Raise(nameof(DoneExpanded)); RequestSave(); }
        }

        public bool Mini
        {
            get => S.Mini;
            set { S.Mini = value; Raise(); Raise(nameof(MiniGlyph)); Raise(nameof(HeaderSubtitle)); RequestSave(); }
        }
        public string MiniGlyph => S.Mini ? "" : "";

        public string Title
        {
            get => S.Title;
            set { S.Title = value; Raise(); RequestSave(); }
        }

        public bool Topmost
        {
            get => S.Topmost;
            set
            {
                S.Topmost = value;
                if (value) S.BottomMost = false;
                RaiseLayer();
                RequestSave();
            }
        }
        public string PinGlyph => S.Topmost ? "" : "";

        /// <summary>贴边时强制置顶，否则收起的细条会被其他窗口挡住</summary>
        public bool EffectiveTopmost => S.Topmost || _docked;

        bool _docked;
        public bool Docked
        {
            get => _docked;
            set { if (Set(ref _docked, value)) Raise(nameof(EffectiveTopmost)); }
        }

        public List<Choice> LayerChoices { get; } = new List<Choice>
        {
            new Choice { Key = "top", Name = "置顶" },
            new Choice { Key = "normal", Name = "普通" },
            new Choice { Key = "bottom", Name = "置底" },
        };

        public string Layer => S.Topmost ? "top" : S.BottomMost ? "bottom" : "normal";
        public event Action LayerChanged;

        public void SelectLayer(string key)
        {
            S.Topmost = key == "top";
            S.BottomMost = key == "bottom";
            RaiseLayer();
            RequestSave();
        }

        void RaiseLayer()
        {
            foreach (var c in LayerChoices) c.IsSelected = c.Key == Layer;
            Raise(nameof(Topmost)); Raise(nameof(PinGlyph)); Raise(nameof(EffectiveTopmost)); Raise(nameof(Layer));
            LayerChanged?.Invoke();
        }

        public bool EdgeDock
        {
            get => S.EdgeDock;
            set { S.EdgeDock = value; Raise(); RequestSave(); }
        }

        /// <summary>插入使用示例（设置页按钮）</summary>
        public void AddTutorial()
        {
            Reindex();
            foreach (var it in Tutorial.Create())
            {
                it.IsNew = true;
                it.SortIndex = Items.Count;
                Items.Add(it);
            }
            RefreshViews();
        }

        public bool HoverOpaque
        {
            get => S.HoverOpaque;
            set { S.HoverOpaque = value; Raise(); RequestSave(); }
        }

        /// <summary>实际是否显示任务栏按钮（隐藏 Alt+Tab 时强制不显示）</summary>
        public bool ShowInTaskbar
        {
            get => !S.HideTaskbar && !S.HideAltTab;
            set { S.HideTaskbar = !value; Raise(); RequestSave(); }
        }

        public event Action AltTabChanged;

        public bool HideAltTab
        {
            get => S.HideAltTab;
            set
            {
                if (S.HideAltTab == value) return;
                S.HideAltTab = value;
                Raise(); Raise(nameof(ShowInTaskbar));
                AltTabChanged?.Invoke();
                RequestSave();
            }
        }

        public double BgOpacity
        {
            get => S.BgOpacity;
            set { S.BgOpacity = Math.Round(value, 2); Raise(); AppearanceChanged?.Invoke(); RequestSave(); }
        }

        public double WinOpacity
        {
            get => S.WinOpacity;
            set { S.WinOpacity = Math.Round(value, 2); Raise(); WinOpacityChanged?.Invoke(); RequestSave(); }
        }

        public double FontSizeBase
        {
            get => S.FontSize;
            set { S.FontSize = value; Raise(); AppearanceChanged?.Invoke(); RequestSave(); }
        }

        public void SelectTheme(string key)
        {
            S.Theme = key;
            foreach (var c in Themes) c.IsSelected = c.Key == key;
            AppearanceChanged?.Invoke(); RequestSave();
        }

        public void SelectLayout(string key)
        {
            S.Layout = key;
            foreach (var c in Layouts) c.IsSelected = c.Key == key;
            AppearanceChanged?.Invoke(); RequestSave();
        }

        // ================= 开机启动 =================

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "LiteTodo";

        public bool AutoStart
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                        return k?.GetValue(RunName) != null;
                }
                catch { return false; }
            }
            set
            {
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (value) k.SetValue(RunName, "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --autostart");
                        else k.DeleteValue(RunName, false);
                    }
                }
                catch { }
                Raise();
            }
        }
    }
}
