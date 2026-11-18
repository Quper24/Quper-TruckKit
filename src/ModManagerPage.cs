using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuperDesk {
    internal sealed partial class MainWindow {
        private sealed class ModRow {
            public readonly string Entry, Key, Name;
            public ModRow(string entry) {
                Entry = entry;
                var parts = entry.Split(new[] { '|' }, 2);
                Key = parts[0];
                Name = parts.Length == 2 && parts[1].Length > 0 ? parts[1] : Key;
            }
            public override string ToString() { return Name; }
        }

        private void ModManagerPage(Panel content, IList<Profile> profiles) {
            Add(content, Text(L.T("Менеджер модов"), 32, Ink, true));
            Add(content, Text(L.T("Перетащите моды между списками. Двойной клик справа отключает мод."), 14, Muted), 7, 16);
            if (NoProfiles(content, profiles)) return;

            var form = Stack();
            var picker = ProfileDrop(profiles);
            var filter = new TextBox();
            Pair(form, Field(L.T("Профиль"), picker), Field(L.T("Поиск доступных модов"), filter));
            var available = new ListBox { Height = 300, AllowDrop = true };
            var active = new ListBox { Height = 300, AllowDrop = true };
            var activePanel = new Grid();
            activePanel.Children.Add(active);
            var insertion = new Border {
                Height = 2, Background = Accent, IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed
            };
            activePanel.Children.Add(insertion);
            System.Windows.Automation.AutomationProperties.SetName(active, L.T("Порядок загрузки · высокий приоритет сверху"));
            Pair(form, Field(L.T("Доступные моды · папка mod"), available), Field(L.T("Порядок загрузки · высокий приоритет сверху"), activePanel), 12);
            var note = Text("", 13, Muted);
            Add(form, note, 10);
            var buttons = Stack(true);
            var apply = Button(L.T("Подтвердить"), () => { }, "Primary");
            var add = Button("→", () => { }); var remove = Button("←", () => { });
            var up = Button("↑", () => { }); var down = Button("↓", () => { });
            var navigation = new[] { add, remove, up, down };
            var labels = new[] { L.T("Активировать мод"), L.T("Деактивировать мод"), L.T("Повысить приоритет"), L.T("Понизить приоритет") };
            buttons.Children.Add(apply);
            for (var i = 0; i < navigation.Length; i++) {
                navigation[i].Margin = new Thickness(7, 0, 0, 0);
                navigation[i].ToolTip = labels[i];
                System.Windows.Automation.AutomationProperties.SetName(navigation[i], labels[i]);
                buttons.Children.Add(navigation[i]);
            }
            Add(form, buttons, 10);
            Add(content, Box(form));

            var game = Current;
            var known = catalog.combos.Where(c => c.game == game.Code).SelectMany(c => c.mods)
                .GroupBy(m => m.key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().name, StringComparer.OrdinalIgnoreCase);
            var packages = new Dictionary<string, ModRow>(StringComparer.OrdinalIgnoreCase);
            var order = new List<ModRow>();
            List<string> original = null;
            Func<List<string>> savedOrder = () => order.AsEnumerable().Reverse().Select(m => m.Entry).ToList();
            Action updateButtons = () => {
                add.IsEnabled = available.SelectedItem != null;
                remove.IsEnabled = active.SelectedItem != null;
                up.IsEnabled = active.SelectedIndex > 0;
                down.IsEnabled = active.SelectedIndex >= 0 && active.SelectedIndex < order.Count - 1;
                apply.IsEnabled = original != null && !original.SequenceEqual(savedOrder());
            };
            Action<ModRow> refresh = selected => {
                var keys = new HashSet<string>(order.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);
                available.ItemsSource = packages.Values.Where(m => !keys.Contains(m.Key) &&
                    (m.Name.IndexOf(filter.Text, StringComparison.OrdinalIgnoreCase) >= 0 || m.Key.IndexOf(filter.Text, StringComparison.OrdinalIgnoreCase) >= 0))
                    .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                active.ItemsSource = order.ToList();
                active.SelectedItem = selected;
                if (selected != null) active.ScrollIntoView(selected);
                updateButtons();
            };
            Action load = () => {
                original = null;
                packages.Clear(); order.Clear();
                note.Foreground = Muted;
                try {
                    var profile = (Profile)picker.SelectedItem;
                    if (profile == null) { refresh(null); return; }
                    original = profile.Mods();
                    order.AddRange(original.AsEnumerable().Reverse().Select(entry => new ModRow(entry)));
                    foreach (var file in game.InstalledMods()) {
                        var key = Directory.Exists(Path.Combine(game.Root, "mod", file)) ? file : Path.GetFileNameWithoutExtension(file);
                        if (key.Length == 0 || key.Contains("|")) continue;
                        string name;
                        if (!known.TryGetValue(key, out name)) name = key;
                        packages[key] = new ModRow(key + "|" + name);
                    }
                    foreach (var row in order)
                        if (packages.ContainsKey(row.Key)) packages[row.Key] = row;
                    note.Text = L.T("Нажмите «Подтвердить», чтобы сохранить изменения и создать резервную копию.");
                } catch (Exception error) {
                    original = null;
                    note.Text = error.Message;
                    note.Foreground = Warn;
                }
                refresh(null);
            };
            Action<ModRow, int> place = (row, index) => {
                if (row == null || original == null) return;
                var previous = order.IndexOf(row);
                if (previous >= 0) {
                    order.RemoveAt(previous);
                    if (previous < index) index--;
                } else if (order.Any(m => string.Equals(m.Key, row.Key, StringComparison.OrdinalIgnoreCase))) return;
                order.Insert(Math.Max(0, Math.Min(index, order.Count)), row);
                refresh(row);
            };
            Action<ModRow> deactivate = row => {
                if (row == null || !order.Remove(row)) return;
                refresh(null);
            };
            picker.SelectionChanged += (s, e) => load();
            filter.TextChanged += (s, e) => refresh(active.SelectedItem as ModRow);
            available.SelectionChanged += (s, e) => updateButtons();
            active.SelectionChanged += (s, e) => updateButtons();
            add.Click += (s, e) => place(available.SelectedItem as ModRow, 0);
            remove.Click += (s, e) => deactivate(active.SelectedItem as ModRow);
            up.Click += (s, e) => place(active.SelectedItem as ModRow, active.SelectedIndex - 1);
            down.Click += (s, e) => place(active.SelectedItem as ModRow, active.SelectedIndex + 2);
            active.PreviewMouseLeftButtonDown += (s, e) => {
                if (e.ClickCount != 2) return;
                var item = ModContainer(active, e.OriginalSource);
                if (item == null) return;
                e.Handled = true;
                deactivate(item.Content as ModRow);
            };
            Action hideInsertion = () => insertion.Visibility = Visibility.Collapsed;
            EnableModDrag(available, hideInsertion);
            EnableModDrag(active, hideInsertion);
            active.PreviewDragOver += (s, e) => {
                if (original == null || !e.Data.GetDataPresent(typeof(ModRow))) {
                    hideInsertion();
                    e.Effects = DragDropEffects.None;
                } else {
                    double y;
                    ModDropIndex(active, e, out y);
                    insertion.Margin = new Thickness(5, Math.Max(2, Math.Min(y - 1, active.ActualHeight - 4)), 19, 0);
                    insertion.Visibility = Visibility.Visible;
                    e.Effects = DragDropEffects.Move;
                }
                e.Handled = true;
            };
            active.DragLeave += (s, e) => hideInsertion();
            available.Drop += (s, e) => deactivate(e.Data.GetData(typeof(ModRow)) as ModRow);
            active.Drop += (s, e) => {
                hideInsertion();
                double y;
                var index = ModDropIndex(active, e, out y);
                place(e.Data.GetData(typeof(ModRow)) as ModRow, index);
                e.Handled = true;
            };
            apply.Click += async (s, e) => {
                var profile = picker.SelectedItem as Profile;
                var expected = original;
                var pending = savedOrder();
                if (profile == null || expected == null || expected.SequenceEqual(pending)) return;
                await Work(L.T("Записываю порядок модов…"), () => profile.SaveModOrder(expected, pending), path => {
                    original = pending;
                    updateButtons();
                    SetStatus(L.T("Список модов заменён. Проверьте его в менеджере модов игры."), Accent, path);
                });
            };
            load();
        }

        private static ListBoxItem ModContainer(ListBox list, object source) {
            var element = source as DependencyObject;
            return element == null ? null : ItemsControl.ContainerFromElement(list, element) as ListBoxItem;
        }

        private static int ModDropIndex(ListBox list, DragEventArgs e, out double y) {
            var target = ModContainer(list, e.OriginalSource);
            if (target == null) {
                var last = list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1) as ListBoxItem;
                y = last == null ? (list.Items.Count == 0 ? 4 : list.ActualHeight - 4)
                    : last.TranslatePoint(new Point(0, last.ActualHeight), list).Y;
                return list.Items.Count;
            }
            var after = e.GetPosition(target).Y >= target.ActualHeight / 2;
            y = target.TranslatePoint(new Point(0, after ? target.ActualHeight : 0), list).Y;
            return list.ItemContainerGenerator.IndexFromContainer(target) + (after ? 1 : 0);
        }

        private static void EnableModDrag(ListBox list, Action dragFinished) {
            Point start = new Point();
            ModRow row = null;
            list.PreviewMouseLeftButtonDown += (s, e) => {
                start = e.GetPosition(list);
                var item = ModContainer(list, e.OriginalSource);
                row = item == null ? null : item.Content as ModRow;
            };
            list.PreviewMouseLeftButtonUp += (s, e) => row = null;
            list.PreviewMouseMove += (s, e) => {
                if (row == null || e.LeftButton != MouseButtonState.Pressed) return;
                var delta = e.GetPosition(list) - start;
                if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                var dragged = row;
                row = null;
                try { DragDrop.DoDragDrop(list, new DataObject(typeof(ModRow), dragged), DragDropEffects.Move); }
                finally { dragFinished(); }
            };
            list.DragOver += (s, e) => {
                e.Effects = e.Data.GetDataPresent(typeof(ModRow)) ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
        }
    }
}
