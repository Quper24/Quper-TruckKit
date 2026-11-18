using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace QuperDesk {
    internal sealed partial class MainWindow {
        private void ControlsPage(Panel pageContent, IList<Profile> destinationProfiles) {
            Add(pageContent, Text(L.T("Управление"), 32, Ink, true), 0, 6);
            Add(pageContent, Text(L.T("Перенесите кнопки, оси и обратную связь руля между профилями ETS2 и ATS."), 14, Muted), 0, 17);
            if (NoProfiles(pageContent, destinationProfiles)) return;

            var destinationGame = Current;
            var sourceGame = MakeDropdown();
            Bind(sourceGame, new[] { "ETS2", "ATS" }, gameIndex);
            var sourceProfile = MakeDropdown();
            var destinationProfile = ProfileDrop(destinationProfiles);
            var form = Stack();
            var left = Stack();
            Add(left, Field(L.T("Игра источника"), sourceGame));
            Add(left, Field(L.T("Профиль источника"), sourceProfile), 10);
            var right = Stack();
            Add(right, Text(L.T("Игра назначения"), 12, Muted, true), 0, 6);
            var gameLabel = Text(destinationGame.Title, 14, Accent, true);
            gameLabel.MinHeight = 42;
            gameLabel.Padding = new Thickness(0, 9, 0, 0);
            Add(right, gameLabel);
            Add(right, Field(L.T("Профиль назначения"), destinationProfile), 10);
            Pair(form, left, right);

            var options = Stack();
            Add(options, Text(L.T("Дополнительные настройки"), 13, Muted, true));
            var choices = new WrapPanel();
            var sound = SettingsChoice(L.T("Звук"));
            var camera = SettingsChoice(L.T("Камера"));
            var ui = SettingsChoice(L.T("Интерфейс и навигация"));
            choices.Children.Add(sound);
            choices.Children.Add(camera);
            choices.Children.Add(ui);
            Add(options, choices, 9);
            Add(form, options, 16);
            var mode = Text("", 13, Muted);
            Add(form, mode, 12);
            Add(pageContent, Box(form));

            var review = Stack();
            Add(review, Text(L.T("Что будет перенесено"), 18, Ink, true));
            var details = Text("", 14);
            var notes = Text("", 13, Warn);
            Add(review, details, 9);
            Add(review, notes, 7);
            var apply = Button(L.T("Скопировать настройки"), () => { }, "Primary");
            apply.HorizontalAlignment = HorizontalAlignment.Left;
            apply.IsEnabled = false;
            Add(review, apply, 13);
            Add(pageContent, Box(review), 13);

            ControlsTransfer prepared = null;
            int revision = 0;
            bool bindingProfiles = false;
            Func<Task> preview = async () => {
                if (bindingProfiles) return;
                var currentRevision = ++revision;
                prepared = null;
                apply.IsEnabled = false;
                notes.Text = "";
                var from = sourceProfile.SelectedItem as Profile;
                var to = destinationProfile.SelectedItem as Profile;
                var sameGame = sourceGame.SelectedIndex == gameIndex;
                options.Visibility = sameGame ? Visibility.Visible : Visibility.Collapsed;
                mode.Text = sameGame ? L.T("Управление, раскладки КПП и параметры вождения копируются вместе.")
                    : L.T("В этом режиме копируются общие назначения кнопок, осей и обратной связи руля.");
                if (from == null || to == null) {
                    details.Text = sourceProfile.Items.Count == 0
                        ? L.T("Для переноса нужен другой локальный профиль. Выберите другую игру источника или создайте профиль в игре.")
                        : L.T("Выберите исходный профиль и профиль назначения.");
                    return;
                }

                var settings = ProfileSettings.None;
                if (sameGame) {
                    if (sound.IsChecked == true) settings |= ProfileSettings.Sound;
                    if (camera.IsChecked == true) settings |= ProfileSettings.Camera;
                    if (ui.IsChecked == true) settings |= ProfileSettings.Interface;
                }
                details.Text = L.T("Сравниваю настройки…");
                try {
                    var result = await Task.Run(() => ControlsTransfer.Prepare(from, to, settings));
                    if (currentRevision != revision) return;
                    prepared = result;
                    var title = result.FileCount == 0 ? L.T("Выбранные настройки уже совпадают.")
                        : L.T("Файлов для записи: ") + result.FileCount + L.T(". Перед записью будет создана резервная копия.");
                    details.Text = title + "\n" + string.Join("\n", result.Summary);
                    notes.Text = string.Join("\n", result.Notes);
                    apply.IsEnabled = result.FileCount > 0;
                } catch (Exception e) {
                    if (currentRevision != revision) return;
                    details.Text = L.T("Предпросмотр недоступен.");
                    notes.Text = e.Message;
                }
            };

            Action updateSources = () => {
                bindingProfiles = true;
                try {
                    var to = destinationProfile.SelectedItem as Profile;
                    var selected = sourceProfile.SelectedItem as Profile;
                    var profiles = sourceGame.SelectedIndex == gameIndex ? destinationProfiles : games[sourceGame.SelectedIndex].Profiles();
                    var available = profiles.Where(p => to == null || !string.Equals(p.DirectoryPath, to.DirectoryPath, StringComparison.OrdinalIgnoreCase)).ToList();
                    var index = selected == null ? 0 : available.FindIndex(p => p.DirectoryPath == selected.DirectoryPath);
                    Bind(sourceProfile, available, index);
                } finally { bindingProfiles = false; }
            };

            Func<Task> update = async () => {
                try { updateSources(); }
                catch (Exception e) {
                    revision++;
                    prepared = null;
                    apply.IsEnabled = false;
                    details.Text = L.T("Не удалось прочитать профили источника.");
                    notes.Text = e.Message;
                    return;
                }
                await preview();
            };
            sourceGame.SelectionChanged += async (sender, e) => await update();
            destinationProfile.SelectionChanged += async (sender, e) => await update();
            sourceProfile.SelectionChanged += async (sender, e) => await preview();
            foreach (var choice in new[] { sound, camera, ui }) {
                choice.Checked += async (sender, e) => await preview();
                choice.Unchecked += async (sender, e) => await preview();
            }
            apply.Click += async (sender, e) => {
                var transfer = prepared;
                if (transfer == null || transfer.FileCount == 0) return;
                await Work(L.T("Сохраняю резервную копию и переношу настройки…"), transfer.Apply, path =>
                    SetStatus(L.T("Настройки скопированы. Проверьте кнопки и оси в игре."), Accent, path));
                await preview();
            };
            var initialPreview = update();
        }

        private static CheckBox SettingsChoice(string label) {
            return new CheckBox {
                Content = label, Foreground = Ink, Margin = new Thickness(0, 0, 20, 3),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }
    }
}
