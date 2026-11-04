using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LiteTodo
{
    public partial class MainController
    {
        TodoItem editing, menuTarget, dragCandidate, draggingItem, selected;
        Point dragStart;
        List<KeyValuePair<int, TodoItem>> lastRemoved;
        readonly HashSet<TodoItem> pendingMove = new HashSet<TodoItem>();
        readonly HashSet<TodoItem> deleting = new HashSet<TodoItem>();
        const string DragFormat = "LiteTodo.Item";

        void WireEvents()
        {
            win.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnClick));
            win.AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnLostFocus), true);
            win.PreviewKeyDown += OnWinKey;
            input.PreviewKeyDown += OnInputKey;
            input.TextChanged += (s, e) => UpdateComposer();
            input.GotKeyboardFocus += (s, e) => { Select(null); UpdateComposer(); };
            input.LostKeyboardFocus += (s, e) => UpdateComposer();
            vm.Draft.PropertyChanged += (s, e) => UpdateComposer();

            WireDrag(header, true);
            WireDrag(settingsHeader, false);
            header.MouseRightButtonUp += (s, e) => { OpenWinMenu(); e.Handled = true; };

            // Ctrl+滚轮：调整背景透明度
            win.PreviewMouseWheel += (s, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.Control) return;
                vm.BgOpacity = Math.Max(0, Math.Min(1, Math.Round(vm.BgOpacity + (e.Delta > 0 ? 0.05 : -0.05), 2)));
                ShowHud("背景不透明度 " + (int)Math.Round(vm.BgOpacity * 100) + "%");
                e.Handled = true;
            };

            // 粘贴多行文本 → 批量添加
            DataObject.AddPastingHandler(input, OnPaste);
            // 从其他程序拖入文字 → 添加任务
            win.AllowDrop = true;
            win.PreviewDragOver += OnExternalDragOver;
            win.PreviewDrop += OnExternalDrop;

            foreach (var l in new[] { activeList, doneList })
            {
                l.PreviewMouseLeftButtonDown += OnListDown;
                l.MouseRightButtonUp += OnListRightClick;
            }
            activeList.PreviewMouseMove += OnListMove;
            activeList.PreviewMouseLeftButtonUp += (s, e) => dragCandidate = null;
            activeList.DragOver += OnListDragOver;
            activeList.Drop += (s, e) => e.Handled = true;

            // 双击列表空白处 → 开始输入
            scroller.MouseDoubleClick += (s, e) =>
            {
                var src = e.OriginalSource as DependencyObject;
                if (FindItem(src, out _) == null && !HasAncestor<ButtonBase>(src, scroller)) { input.Focus(); e.Handled = true; }
            };
            scroller.PreviewMouseLeftButtonDown += (s, e) =>
            {
                var src = e.OriginalSource as DependencyObject;
                if (editing != null && !IsEditBox(src)) CommitEdit();
                if (FindItem(src, out _) == null && !HasAncestor<ButtonBase>(src, scroller)) Select(null);
            };

            win.MouseEnter += (s, e) => { SetHover(true); OnDockMouseEnter(); };
            win.MouseLeave += (s, e) => { if (!menu.IsOpen && !winMenu.IsOpen) SetHover(false); OnDockMouseLeave(); };
            menu.Closed += (s, e) => { menuTarget = null; SetHover(win.IsMouseOver); if (!win.IsMouseOver) OnDockMouseLeave(); };
            winMenu.Closed += (s, e) => { SetHover(win.IsMouseOver); if (!win.IsMouseOver) OnDockMouseLeave(); };
            win.Deactivated += (s, e) => { lastDeactivated = DateTime.Now; OnDockDeactivated(); };
            win.Activated += (s, e) => vm.UpdateStats(); // 跨天后刷新日期
            win.LocationChanged += (s, e) => CaptureBounds();
            win.SizeChanged += (s, e) =>
            {
                CaptureBounds();
                // 贴边时尺寸变化（缩放 / 折叠）后保持贴齐
                if (dock != Edge.None && tween == null && !dragging)
                {
                    var p = DockPos(dock, dockShown, WinRect());
                    SetPos(p.X, p.Y);
                }
            };
            win.StateChanged += (s, e) =>
            {
                // 无边框窗口不需要最大化（Aero Snap 拖到顶部时还原）
                if (win.WindowState == WindowState.Maximized) win.WindowState = WindowState.Normal;
            };
        }


        // ================= 按钮统一分发（按 Tag） =================

        void OnClick(object sender, RoutedEventArgs e)
        {
            var fe = e.OriginalSource as FrameworkElement;
            if (!(fe?.Tag is string tag)) return;
            var dc = fe.DataContext;
            if (winMenu.IsOpen) winMenu.IsOpen = false;

            switch (tag)
            {
                case "layer": if (dc is Choice lc) vm.SelectLayer(lc.Key); break;
                case "tutorial": vm.AddTutorial(); ShowSettings(false); ShowHud("已添加使用示例"); break;
                case "dockNow": if (dock != Edge.None) UndockInward(); else DockToNearest(); break;
                case "close": Exit(); break;
                case "min": Minimize(); break;
                case "mini": ToggleMini(); break;
                case "pin": vm.Topmost = !vm.Topmost; break;
                case "settings": ShowSettings(!settingsOpen); break;
                case "add": AddFromInput(); break;

                case "draftPrio":
                    if (dc is Choice p) vm.SetDraftPriority(vm.Draft.Priority == p.Level ? 0 : p.Level);
                    input.Focus();
                    break;
                case "draftColor":
                    if (dc is Choice dcol) vm.SetDraftColor(vm.Draft.Color == dcol.Key ? "" : dcol.Key);
                    input.Focus();
                    break;

                case "check":
                    Fx.Pop(fe);
                    break;
                case "doneSection": vm.DoneCollapsed = !vm.DoneCollapsed; break;
                case "clearDone": DeleteItems(vm.Items.Where(i => i.IsDone).ToList()); break;
                case "theme": if (dc is Choice t) vm.SelectTheme(t.Key); break;
                case "layout": if (dc is Choice l) vm.SelectLayout(l.Key); break;
                case "more": if (dc is TodoItem m) { Select(m, false); OpenMenu(m, fe); } break;
                case "del": if (dc is TodoItem d) DeleteItems(new[] { d }); break;

                case "setPrio":
                    if (menuTarget != null && dc is Choice sp) { menuTarget.Priority = sp.Level; SyncMenuSelection(); }
                    break;
                case "setColor":
                    if (menuTarget != null && dc is Choice sc) { menuTarget.Color = sc.Key; SyncMenuSelection(); }
                    break;
                case "edit":
                    {
                        var it = menuTarget; menu.IsOpen = false;
                        if (it != null) BeginEdit(it);
                        break;
                    }
                case "toggleDone":
                    {
                        var it = menuTarget; menu.IsOpen = false;
                        if (it != null) ToggleDone(it);
                        break;
                    }
                case "toTop":
                    {
                        var it = menuTarget; menu.IsOpen = false;
                        if (it != null) { vm.MoveToTop(it); ScrollTo(it); }
                        break;
                    }
                case "delete":
                    {
                        var it = menuTarget; menu.IsOpen = false;
                        if (it != null) DeleteItems(new[] { it });
                        break;
                    }
                case "openLink":
                    {
                        var url = FindUrl(menuTarget?.Text);
                        menu.IsOpen = false;
                        if (url != null) try { Process.Start(url); } catch (Exception ex) { Program.Log("打开链接失败: " + ex); }
                        break;
                    }
                case "undo": Undo(); break;
                case "openDir": OpenDataDir(); break;
            }
        }

        // ================= 输入栏 =================

        void AddFromInput()
        {
            var it = vm.Add(input.Text);
            if (it != null)
            {
                input.Clear();
                ScrollTo(it);
            }
            input.Focus();
        }

        void UpdateComposer()
        {
            bool hasText = input.Text.Length > 0;
            bool show = input.IsKeyboardFocused || hasText || vm.Draft.Priority > 0 || vm.Draft.HasColor;
            if (show != toolsOpen)
            {
                toolsOpen = show;
                Fx.Expand(composerTools, show);
            }
            if (hasText != sendShown)
            {
                sendShown = hasText;
                sendBtn.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
                if (hasText) Fx.Pop(sendBtn);
            }
        }

        void OnInputKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { AddFromInput(); e.Handled = true; }
            else if (e.Key == Key.Escape)
            {
                if (input.Text.Length > 0) input.Clear();
                else if (vm.Draft.Priority > 0 || vm.Draft.HasColor) { vm.SetDraftPriority(0); vm.SetDraftColor(""); }
                else if (dock != Edge.None) DockHide();
                e.Handled = true;
            }
            else if (e.Key == Key.Down && input.Text.Length == 0)
            {
                var order = NavOrder();
                if (order.Count > 0) { Select(order[0]); e.Handled = true; }
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                int p = DigitPriority(e.Key);
                if (p >= 0) { vm.SetDraftPriority(p); e.Handled = true; }
            }
        }

        /// <summary>1/2/3/0 → 高/中/低/无</summary>
        static int DigitPriority(Key k)
        {
            switch (k)
            {
                case Key.D1: case Key.NumPad1: return 3;
                case Key.D2: case Key.NumPad2: return 2;
                case Key.D3: case Key.NumPad3: return 1;
                case Key.D0: case Key.NumPad0: return 0;
                default: return -1;
            }
        }

        // ================= 键盘 =================

        void OnWinKey(object sender, KeyEventArgs e)
        {
            var src = e.OriginalSource as DependencyObject;
            if (editing != null && IsEditBox(src))
            {
                if (e.Key == Key.Enter) { var it = editing; CommitEdit(); Select(it); e.Handled = true; }
                else if (e.Key == Key.Escape) { var it = editing; CommitEdit(true); Select(it); e.Handled = true; }
                return;
            }

            var mods = Keyboard.Modifiers;
            bool ctrl = mods == ModifierKeys.Control;

            if (ctrl && e.Key == Key.Z)
            {
                if (src is TextBox tb && tb.Text.Length > 0) return; // 让输入框自己撤销文字
                if (lastRemoved != null) { Undo(); e.Handled = true; }
                return;
            }
            if (ctrl && (e.Key == Key.N || e.Key == Key.L))
            {
                if (settingsOpen) ShowSettings(false);
                if (vm.Mini) ToggleMini();
                input.Focus();
                e.Handled = true;
                return;
            }
            if (ctrl && e.Key == Key.OemComma) { ShowSettings(!settingsOpen); e.Handled = true; return; }
            if (ctrl && !(src is TextBox && e.Key == Key.D0) &&
                (e.Key == Key.OemPlus || e.Key == Key.Add || e.Key == Key.OemMinus || e.Key == Key.Subtract))
            {
                double size = vm.FontSizeBase + (e.Key == Key.OemPlus || e.Key == Key.Add ? 0.5 : -0.5);
                vm.FontSizeBase = Math.Max(11, Math.Min(20, size));
                ShowHud("字号 " + vm.FontSizeBase.ToString("0.#"));
                e.Handled = true;
                return;
            }

            if (settingsOpen)
            {
                if (e.Key == Key.Escape) { ShowSettings(false); e.Handled = true; }
                return;
            }
            if (src is TextBox) return;

            var order = NavOrder();
            int idx = selected == null ? -1 : order.IndexOf(selected);
            switch (e.Key)
            {
                case Key.Down:
                case Key.Up:
                    {
                        int dir = e.Key == Key.Down ? 1 : -1;
                        if (ctrl && selected != null)
                        {
                            if (vm.CanReorder && !selected.IsDone && vm.MoveWithin(selected, dir)) ScrollTo(selected);
                        }
                        else if (order.Count > 0)
                        {
                            int next = idx < 0 ? (dir > 0 ? 0 : order.Count - 1) : Math.Max(0, Math.Min(order.Count - 1, idx + dir));
                            Select(order[next]);
                        }
                        e.Handled = true;
                        break;
                    }
                case Key.Home: if (order.Count > 0) Select(order[0]); e.Handled = true; break;
                case Key.End: if (order.Count > 0) Select(order[order.Count - 1]); e.Handled = true; break;
                case Key.Space:
                    if (selected != null) { ToggleDone(selected); e.Handled = true; }
                    break;
                case Key.Enter:
                case Key.F2:
                    if (selected != null) { BeginEdit(selected); e.Handled = true; }
                    break;
                case Key.Delete:
                    if (selected != null) { DeleteItems(new[] { selected }); e.Handled = true; }
                    break;
                case Key.Escape:
                    if (selected == null && dock != Edge.None) { DockHide(); e.Handled = true; break; }
                    Select(null);
                    input.Focus();
                    e.Handled = true;
                    break;
                default:
                    if (mods == ModifierKeys.None && selected != null)
                    {
                        int p = DigitPriority(e.Key);
                        if (p >= 0) { selected.Priority = p; e.Handled = true; }
                    }
                    break;
            }
        }

        List<TodoItem> NavOrder()
        {
            var l = vm.ActiveView.Cast<TodoItem>().ToList();
            if (vm.DoneExpanded) l.AddRange(vm.DoneView.Cast<TodoItem>());
            return l;
        }

        void Select(TodoItem it, bool focusList = true)
        {
            if (selected != null && selected != it) selected.IsSelected = false;
            selected = it;
            if (it == null) return;
            it.IsSelected = true;
            if (focusList && !scroller.IsKeyboardFocusWithin) scroller.Focus();
            ScrollTo(it);
        }

        // ================= 完成：勾选 → 弹跳 → 稍后滑入另一分组 =================

        void ToggleDone(TodoItem it)
        {
            it.IsDone = !it.IsDone;
            var c = ContainerOf(it);
            var check = FindChild<ToggleButton>(c, t => (t.Tag as string) == "check");
            if (check != null) Fx.Pop(check);
        }

        void OnDoneToggled(TodoItem it)
        {
            pendingMove.Add(it);
            moveTimer.Stop();
            moveTimer.Start();
        }

        void FlushMoves()
        {
            moveTimer.Stop();
            var movers = pendingMove.Where(i => vm.Items.Contains(i) && i.IsDone != i.InDoneSection && !deleting.Contains(i)).ToList();
            pendingMove.Clear();
            if (movers.Count == 0) return;
            if (editing != null && movers.Contains(editing)) CommitEdit();

            int left = movers.Count;
            Action finish = () =>
            {
                if (--left > 0) return;
                foreach (var i in movers) i.IsNew = true;
                vm.RefreshViews();
                if (selected != null && movers.Contains(selected)) ScrollTo(selected);
            };
            foreach (var i in movers)
            {
                var c = ContainerOf(i);
                if (c != null && c.IsVisible) Fx.CollapseOut(c, finish);
                else finish();
            }
        }

        // ================= 行内编辑 =================

        void BeginEdit(TodoItem it)
        {
            CommitEdit();
            menu.IsOpen = false;
            Select(it, false);
            it.EditText = it.Text;
            it.IsEditing = true;
            editing = it;
            win.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                var tb = FindChild<TextBox>(ContainerOf(it), x => x.Uid == "edit");
                if (tb == null) return;
                tb.Focus();
                tb.SelectAll();
            }));
        }

        void CommitEdit(bool cancel = false)
        {
            var it = editing;
            if (it == null) return;
            editing = null;
            if (!cancel)
            {
                var t = (it.EditText ?? "").Trim();
                if (t.Length > 0) it.Text = t;
            }
            it.IsEditing = false;
        }

        void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (editing != null && IsEditBox(e.OriginalSource as DependencyObject)) CommitEdit();
        }

        static bool IsEditBox(DependencyObject d) => d is TextBox tb && tb.Uid == "edit";

        // ================= 列表鼠标：选中 / 双击编辑 / 拖动排序 / 右键菜单 =================

        void OnListDown(object sender, MouseButtonEventArgs e)
        {
            var src = e.OriginalSource as DependencyObject;
            var it = FindItem(src, out var root);
            if (it == null || HasAncestor<TextBoxBase>(src, root)) return;
            if (HasAncestor<ButtonBase>(src, root)) return;

            if (e.ClickCount == 2)
            {
                BeginEdit(it);
                e.Handled = true;
                return;
            }
            Select(it);
            if (sender == activeList && vm.CanReorder && !it.IsEditing)
            {
                dragCandidate = it;
                dragStart = e.GetPosition(activeList);
            }
        }

        void OnListMove(object sender, MouseEventArgs e)
        {
            if (dragCandidate == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { dragCandidate = null; return; }
            var p = e.GetPosition(activeList);
            if (Math.Abs(p.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance + 2 &&
                Math.Abs(p.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance + 2) return;

            var it = dragCandidate;
            dragCandidate = null;
            draggingItem = it;
            it.IsDragging = true;
            try
            {
                DragDrop.DoDragDrop(activeList, new DataObject(DragFormat, it.Id), DragDropEffects.Move);
            }
            finally
            {
                it.IsDragging = false;
                draggingItem = null;
                vm.RequestSave();
            }
        }

        void OnListDragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            var dragged = draggingItem;
            if (dragged == null || !e.Data.GetDataPresent(DragFormat))
            {
                e.Effects = DragDropEffects.None;
                return;
            }
            e.Effects = DragDropEffects.Move;

            // 靠近上下边缘时自动滚动
            var sp = e.GetPosition(scroller);
            if (sp.Y < 30) scroller.ScrollToVerticalOffset(scroller.VerticalOffset - 10);
            else if (sp.Y > scroller.ActualHeight - 30) scroller.ScrollToVerticalOffset(scroller.VerticalOffset + 10);

            var target = FindItem(e.OriginalSource as DependencyObject, out var root);
            if (target == null || target == dragged || target.IsDone) return;
            int from = vm.Items.IndexOf(dragged), to = vm.Items.IndexOf(target);
            if (from < 0 || to < 0) return;
            bool lowerHalf = e.GetPosition(root).Y > root.ActualHeight / 2;
            if ((from < to && lowerHalf) || (from > to && !lowerHalf)) vm.Items.Move(from, to);
        }

        void OnListRightClick(object sender, MouseButtonEventArgs e)
        {
            var it = FindItem(e.OriginalSource as DependencyObject, out _);
            if (it == null) return;
            Select(it, false);
            OpenMenu(it, null);
            e.Handled = true;
        }

        // ================= 条目菜单 =================

        void OpenMenu(TodoItem target, FrameworkElement anchor)
        {
            if (target == null) return;
            CommitEdit();
            menu.IsOpen = false;
            menuTarget = target;
            SyncMenuSelection();
            menuDoneText.Text = target.IsDone ? "标为未完成" : "标为完成";
            menuToTop.Visibility = target.IsDone || !vm.CanReorder ? Visibility.Collapsed : Visibility.Visible;
            menuLink.Visibility = FindUrl(target.Text) != null ? Visibility.Visible : Visibility.Collapsed;

            if (anchor == null)
            {
                menu.PlacementTarget = null;
                menu.Placement = PlacementMode.MousePoint;
                menu.HorizontalOffset = -10;
                menu.VerticalOffset = -10;
            }
            else
            {
                // 菜单右边缘与按钮右边缘对齐（菜单外围有 14px 阴影边距）
                menu.PlacementTarget = anchor;
                menu.Placement = PlacementMode.Bottom;
                menu.HorizontalOffset = anchor.ActualWidth - 228 - 14;
                menu.VerticalOffset = -10;
            }
            OpenPopup(menu);
            SetChrome(true);
        }

        void SyncMenuSelection()
        {
            var t = menuTarget;
            if (t == null) return;
            foreach (var c in vm.PrioChoices) c.IsSelected = c.Level == t.Priority;
            foreach (var c in vm.ColorChoices) c.IsSelected = c.Key == t.Color;
        }

        // ================= 批量添加（粘贴多行 / 拖入文字） =================

        static readonly System.Text.RegularExpressions.Regex BulletRx =
            new System.Text.RegularExpressions.Regex(@"^\s*(?:[-*•·]\s+|\d+[.、)]\s*|\[[ xX]?\]\s*)+");

        /// <summary>按行拆分，去掉常见的列表符号（- * 1. [ ]），返回添加的数量</summary>
        int AddLines(string text)
        {
            int n = 0;
            TodoItem last = null;
            foreach (var raw in (text ?? "").Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
            {
                var line = BulletRx.Replace(raw, "").Trim();
                if (line.Length == 0) continue;
                var it = vm.Add(line);
                if (it != null) { n++; last = it; }
            }
            if (last != null) ScrollTo(last);
            return n;
        }

        void OnPaste(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)) return;
            var text = e.DataObject.GetData(DataFormats.UnicodeText) as string ?? "";
            if (text.Trim().IndexOfAny(new[] { '\r', '\n' }) < 0) return;  // 单行照常粘贴
            e.CancelCommand();
            int n = AddLines(input.Text.Length > 0 ? input.Text + "\n" + text : text);
            input.Clear();
            if (n > 0) ShowHud("已添加 " + n + " 项");
        }

        void OnExternalDragOver(object sender, DragEventArgs e)
        {
            if (draggingItem != null) return; // 内部拖动排序
            if (e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                if (dock != Edge.None && !dockShown) DockShow();
            }
        }

        void OnExternalDrop(object sender, DragEventArgs e)
        {
            if (draggingItem != null) return;
            var text = (e.Data.GetData(DataFormats.UnicodeText) ?? e.Data.GetData(DataFormats.Text)) as string;
            if (string.IsNullOrWhiteSpace(text)) return;
            e.Handled = true;
            int n = AddLines(text);
            if (n > 0) ShowHud("已添加 " + n + " 项");
        }

        static readonly System.Text.RegularExpressions.Regex UrlRx =
            new System.Text.RegularExpressions.Regex(@"https?://[^\s<>""'，。）)]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        internal static string FindUrl(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = UrlRx.Match(text);
            return m.Success ? m.Value : null;
        }

        // ================= 标题栏右键菜单 =================

        void OpenWinMenu()
        {
            menu.IsOpen = false;
            winMenuDockText.Text = dock != Edge.None ? "取消贴边" : "收起到屏幕边缘";
            winMenuMiniText.Text = vm.Mini ? "展开窗口" : "折叠为标题栏";
            winMenu.Placement = PlacementMode.MousePoint;
            winMenu.HorizontalOffset = -10;
            winMenu.VerticalOffset = -10;
            OpenPopup(winMenu);
            SetChrome(true);
        }

        /// <summary>无界面模式下不打开真实弹窗（否则会出现在用户屏幕上），只做布局供截图</summary>
        void OpenPopup(Popup p)
        {
            if (!headless) { p.IsOpen = true; return; }
            if (p.Child is FrameworkElement c)
            {
                c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                c.Arrange(new Rect(c.DesiredSize));
                c.UpdateLayout();
            }
        }

        // ================= 删除 / 撤销 =================

        void DeleteItems(IEnumerable<TodoItem> items)
        {
            var list = items.Where(i => i != null && vm.Items.Contains(i) && !deleting.Contains(i)).ToList();
            if (list.Count == 0) return;
            if (editing != null && list.Contains(editing)) CommitEdit(true);

            // 删除后选中相邻条目，方便连续操作
            TodoItem nextSel = null;
            if (selected != null && list.Contains(selected))
            {
                var order = NavOrder();
                int i = order.IndexOf(selected);
                nextSel = order.Skip(i + 1).FirstOrDefault(x => !list.Contains(x))
                          ?? order.Take(Math.Max(0, i)).LastOrDefault(x => !list.Contains(x));
            }
            foreach (var i in list) deleting.Add(i);

            int left = list.Count;
            Action finish = () =>
            {
                if (--left > 0) return;
                foreach (var i in list) deleting.Remove(i);
                bool hadSel = selected != null && list.Contains(selected);
                lastRemoved = vm.Remove(list);
                if (hadSel) { selected = null; if (nextSel != null) Select(nextSel); }
                ShowToast(list.Count == 1 ? "已删除「" + Trunc(list[0].Text, 12) + "」" : $"已删除 {list.Count} 项", true);
            };
            foreach (var i in list)
            {
                var c = ContainerOf(i);
                if (c != null && c.IsVisible) Fx.CollapseOut(c, finish);
                else finish();
            }
        }

        void Undo()
        {
            if (lastRemoved == null) return;
            var restored = lastRemoved;
            lastRemoved = null;
            vm.Restore(restored);
            HideToast();
            ScrollTo(restored[0].Value);
        }

        void ShowToast(string text, bool undoable)
        {
            toastText.Text = text;
            undoBtn.Visibility = undoable ? Visibility.Visible : Visibility.Collapsed;
            toast.Visibility = Visibility.Visible;
            Fx.SlideIn(toast, 10, 220);
            toastTimer.Stop();
            toastTimer.Interval = Fx.T(5000);
            toastTimer.Start();
        }

        void HideToast()
        {
            toastTimer.Stop();
            Fx.Fade(toast, 0, 200, () => { if (!toastTimer.IsEnabled) toast.Visibility = Visibility.Collapsed; toast.BeginAnimation(UIElement.OpacityProperty, null); });
        }

        static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

        void OpenDataDir()
        {
            try
            {
                if (!File.Exists(Store.FilePath)) SaveNow();
                Process.Start("explorer.exe", "/select,\"" + Store.FilePath + "\"");
            }
            catch (Exception ex) { Program.Log("打开目录失败: " + ex); }
        }

        void ScrollTo(TodoItem it)
        {
            if (it == null) return;
            win.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ContainerOf(it)?.BringIntoView()));
        }

        FrameworkElement ContainerOf(TodoItem it)
        {
            return (activeList.ItemContainerGenerator.ContainerFromItem(it)
                    ?? doneList.ItemContainerGenerator.ContainerFromItem(it)) as FrameworkElement;
        }

        // ================= 视觉树工具 =================

        static DependencyObject Parent(DependencyObject d)
        {
            if (d is Visual || d is System.Windows.Media.Media3D.Visual3D) return VisualTreeHelper.GetParent(d);
            return LogicalTreeHelper.GetParent(d);
        }

        static TodoItem FindItem(DependencyObject d, out FrameworkElement root)
        {
            for (; d != null; d = Parent(d))
            {
                if (d is FrameworkElement fe && (fe.Tag as string) == "item" && fe.DataContext is TodoItem it)
                {
                    root = fe;
                    return it;
                }
            }
            root = null;
            return null;
        }

        static bool HasAncestor<T>(DependencyObject d, DependencyObject stop) where T : class
        {
            for (; d != null && d != stop; d = Parent(d))
                if (d is T) return true;
            return false;
        }

        static T FindChild<T>(DependencyObject parent, Func<T, bool> pred) where T : DependencyObject
        {
            if (parent == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(parent, i);
                if (c is T t && pred(t)) return t;
                var r = FindChild(c, pred);
                if (r != null) return r;
            }
            return null;
        }
    }
}
