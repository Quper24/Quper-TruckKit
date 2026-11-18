using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace QuperDesk {
    internal sealed partial class MainWindow : Window {
        private void CopyModsPage(Panel pageContent, IList<Profile> profiles) {
            Add(pageContent, Text(L.T("Копирование модов"), 32, Ink, true));
            Add(pageContent, Text(L.T("Полностью замените список активных модов профиля назначения списком из профиля-источника."), 14, Muted), 7, 16);
            if (NoProfiles(pageContent, profiles)) return;
            if (profiles.Count < 2) {
                Add(pageContent, Box(Text(L.T("Для копирования модов нужны два локальных профиля одной игры."))));
                return;
            }

            var form = Stack();
            var from = MakeDropdown(); var to = MakeDropdown();
            Bind(from, profiles); Bind(to, profiles, 1);
            Pair(form, Field(L.T("Профиль источника"), from), Field(L.T("Профиль назначения"), to));
            var note = Text("", 13, Muted);
            Add(form, note, 12);
            var list = new ListBox { Height = 165 };
            Add(form, Field(L.T("Порядок загрузки · высокий приоритет сверху"), list), 9);
            var action = Button(L.T("Скопировать моды  →"), () => { }, "Primary");
            action.HorizontalAlignment = HorizontalAlignment.Left;
            Add(form, action, 13);
            Add(pageContent, Box(form));

            Tuple<List<string>, List<string>> lists = null;
            int revision = 0;
            Func<Task> preview = async () => {
                var request = ++revision;
                var source = from.SelectedItem as Profile;
                var destination = to.SelectedItem as Profile;
                lists = null;
                action.IsEnabled = false;
                list.ItemsSource = null;
                note.Foreground = Muted;
                if (source == null || destination == null || source == destination) {
                    note.Text = L.T("Выберите два разных профиля.");
                    return;
                }
                note.Text = L.T("Сравниваю списки модов…");
                try {
                    var result = await Task.Run(() => Tuple.Create(source.Mods(), destination.Mods()));
                    if (request != revision) return;
                    lists = result;
                    list.ItemsSource = result.Item1.AsEnumerable().Reverse().Select(mod => {
                        var separator = mod.IndexOf('|');
                        return Text(separator < 0 ? mod : mod.Substring(separator + 1), 13);
                    }).ToList();
                    action.IsEnabled = !result.Item1.SequenceEqual(result.Item2);
                    note.Text = !action.IsEnabled ? L.T("Списки активных модов совпадают.")
                        : result.Item1.Count == 0 ? L.T("У источника нет активных модов. При копировании список назначения будет очищен.")
                        : L.T("Копируется список активных модов. Сами файлы модов должны быть установлены в игре.");
                } catch (Exception error) {
                    if (request != revision) return;
                    note.Text = error.Message;
                    note.Foreground = Warn;
                }
            };
            from.SelectionChanged += async (s, e) => await preview();
            to.SelectionChanged += async (s, e) => await preview();
            action.Click += async (s, e) => {
                var source = (Profile)from.SelectedItem;
                var destination = (Profile)to.SelectedItem;
                var expected = lists;
                if (expected == null || expected.Item1.SequenceEqual(expected.Item2)) return;
                var detail = string.Format(L.Culture, L.T("В профиле «{0}» будет полностью заменён список активных модов: {1} → {2}. Перед записью будет создана резервная копия."), destination.Name, expected.Item2.Count, expected.Item1.Count);
                if (!Confirm(L.T("Заменить список модов?"), detail, L.T("Заменить"))) return;
                await Work(L.T("Записываю порядок модов…"), () => destination.CopyModsFrom(source, expected.Item1, expected.Item2),
                    path => SetStatus(L.T("Список модов заменён. Проверьте его в менеджере модов игры."), Accent, path));
                await preview();
            };
            var initial = preview();
        }
    }
}
