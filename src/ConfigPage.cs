using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace QuperDesk {
    internal static class GameConfig {
        private static readonly Regex Setting = new Regex(@"(?m)^(\uFEFF?[ \t]*uset[ \t]+)([A-Za-z0-9_]+)([ \t]+)(""[^""\r\n]*""|[^ \t\r\n#]+)");

        internal static string Read(Game game) {
            return Read(new FileSnapshot(Path.Combine(game.Root, "config.cfg")));
        }

        private static string Read(FileSnapshot config) {
            if (config.Bytes == null) throw new FileNotFoundException(L.T("Файл config.cfg не найден. Запустите игру один раз или проверьте выбранную папку игры."));
            return Sii.Utf8.GetString(config.Bytes);
        }

        internal static Dictionary<string, string> Values(string config) {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match match in Setting.Matches(config)) {
                var value = match.Groups[4].Value;
                values[match.Groups[2].Value] = value.StartsWith("\"", StringComparison.Ordinal)
                    ? value.Substring(1, value.Length - 2) : value;
            }
            return values;
        }

        internal static string SetValues(string config, IDictionary<string, string> values) {
            var found = new HashSet<string>();
            var updated = Setting.Replace(config, match => {
                var key = match.Groups[2].Value;
                string value;
                if (!values.TryGetValue(key, out value)) return match.Value;
                found.Add(key);
                return match.Groups[1].Value + key + match.Groups[3].Value + "\"" + value + "\"";
            });
            var missing = values.Where(pair => !found.Contains(pair.Key)).ToList();
            if (missing.Count == 0) return updated;
            var newline = config.Contains("\r\n") || !config.Contains("\n") ? "\r\n" : "\n";
            if (updated.Length > 0 && updated != "\uFEFF" && !updated.EndsWith("\n", StringComparison.Ordinal)) updated += newline;
            return updated + string.Concat(missing.Select(pair => "uset " + pair.Key + " \"" + pair.Value + "\"" + newline));
        }

        internal static string Apply(Game game, IDictionary<string, string> values) {
            game.EnsureClosed();
            var config = new FileSnapshot(Path.Combine(game.Root, "config.cfg"));
            var updated = Sii.Utf8.GetBytes(SetValues(Read(config), values));
            if (FileSnapshot.Equal(config.Bytes, updated)) return null;
            return FileTransaction.Commit(new[] { new FileUpdate(config, updated) }, game.EnsureClosed);
        }
    }

    internal sealed partial class MainWindow {
        private void ConfigPage(Panel content) {
            var game = Current;
            Add(content, Text(L.T("Настройки конфига"), 32, Ink, true));
            Add(content, Text(L.T("Изменения применяются ко всем профилям выбранной игры. Закройте игру перед записью."), 14, Muted), 7, 14);
            Add(content, Text(Path.Combine(game.Root, "config.cfg"), 13, Muted), 0, 10);
            AddConfigPreset(content, game, L.T("Выставить буфер 30"), "r_buffer_page_size = 30",
                new Dictionary<string, string> { { "r_buffer_page_size", "30" } }, true);
            AddConfigPreset(content, game, L.T("Настроить конвой"), "g_max_convoy_size = 128  ·  g_convoy_allow_load = 0",
                new Dictionary<string, string> { { "g_max_convoy_size", "128" }, { "g_convoy_allow_load", "0" } });
            AddConfigPreset(content, game, L.T("Включить консоль"), "g_console = 1  ·  g_developer = 1",
                new Dictionary<string, string> { { "g_console", "1" }, { "g_developer", "1" } });
        }

        private void AddConfigPreset(Panel content, Game game, string label, string description, IDictionary<string, string> values, bool buffer = false) {
            var row = Stack();
            var button = Button(label, () => { }, "Primary");
            button.HorizontalAlignment = HorizontalAlignment.Left;
            var status = Text("", 13, Muted);
            Action refresh = () => {
                try {
                    var current = GameConfig.Values(GameConfig.Read(game));
                    var matches = values.All(pair => current.ContainsKey(pair.Key) && current[pair.Key] == pair.Value);
                    var message = matches ? L.T("Настройки уже совпадают.") : L.T("Можно применить настройки.");
                    string raw;
                    int size;
                    if (buffer && current.TryGetValue("r_buffer_page_size", out raw) &&
                        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out size)) {
                        matches = size == 30;
                        if (matches) message = L.T("Буфер уже выставлен на 30.");
                        else message = string.Format(L.Culture, size > 30
                            ? L.T("Текущий буфер {0}, выше 30. Его можно оставить или снизить до 30.")
                            : L.T("Текущий буфер {0}, ниже 30. Нажмите кнопку, чтобы поднять его до 30."), size);
                    }
                    button.IsEnabled = !matches;
                    status.Foreground = matches ? Accent : Muted;
                    status.Text = message;
                } catch (Exception error) {
                    button.IsEnabled = false;
                    status.Foreground = Warn;
                    status.Text = error.Message;
                }
            };
            button.Click += async (s, e) => await Work(L.T("Обновляю config.cfg…"), () => GameConfig.Apply(game, values), path => {
                refresh();
                SetStatus(path == null ? L.T("Настройки уже совпадают.") : L.T("Настройки сохранены в config.cfg."), Accent, path);
            });
            Add(row, button);
            Add(row, Text(description, 13, Muted), 8);
            Add(row, status, 5);
            Add(content, Box(row, 16), 10);
            refresh();
        }
    }
}
