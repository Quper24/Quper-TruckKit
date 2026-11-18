using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace QuperDesk {
    [Flags]
    internal enum ProfileSettings {
        None = 0,
        Sound = 1,
        Camera = 2,
        Interface = 4
    }

    internal sealed class ControlsTransfer {
        private static readonly Regex GearFile = new Regex(@"\Agearbox[\w.-]*\.sii\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Setting = new Regex(@"(?m)^\uFEFF?[ \t]*uset[ \t]+(?<key>[a-zA-Z0-9_]+)[ \t]+(?<value>""(?:[^""\\\r\n]|\\[^\r\n])*"")[ \t]*(?:(?:#|//)[^\r\n]*)?(?=\r?$)");
        private static readonly Regex ControlLine = new Regex(@"\A(?<type>device|input|constant|mix)\s+(?<name>[A-Za-z0-9_]+)\s+(?<value>.+)\z");
        private static readonly Regex Reference = new Regex(@"'[^']*'|(?<![.\w])[A-Za-z_][A-Za-z0-9_]*");
        private static readonly HashSet<string> Driving = new HashSet<string>(StringComparer.Ordinal) {
            "g_pedal_clutch_range", "g_brake_intensity", "g_steer_anim_range", "g_steer_autocenter", "g_clutch_brake",
            "g_hshifter_synchronized", "g_hshifter_split", "g_hshifter_layout_shifting", "g_hshifter_layout", "g_trans"
        };
        private readonly Profile source, destination;
        private readonly List<FileSnapshot> observed = new List<FileSnapshot>();
        private readonly List<FileUpdate> updates = new List<FileUpdate>();
        private string[] sourceLayouts;
        public readonly List<string> Summary = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public int FileCount { get { return updates.Count; } }

        private ControlsTransfer(Profile source, Profile destination) {
            this.source = source;
            this.destination = destination;
        }

        public static ControlsTransfer Prepare(Profile source, Profile destination, ProfileSettings settings) {
            var sourcePath = ValidateProfile(source);
            var destinationPath = ValidateProfile(destination);
            if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(L.T("Выберите два разных профиля."));
            var transfer = new ControlsTransfer(source, destination);
            var from = transfer.Observe(source, "controls.sii");
            var to = transfer.Observe(destination, "controls.sii");
            var input = ReadControls(from, source);
            var output = ReadControls(to, destination);
            if (source.Game.Code == destination.Game.Code) {
                transfer.AddUpdate(to, from.Bytes);
                transfer.Summary.Add(L.T("Кнопки, оси и обратная связь руля"));
                transfer.PrepareLayouts();
                transfer.PrepareConfig("config_local.cfg", key => Driving.Contains(key) || Extra(key, settings));
                transfer.PrepareConfig("config.cfg", key => key == "g_adaptive_shift" || key == "g_transmission_crawl_ignore" || Extra(key, settings));
            } else {
                if (settings != ProfileSettings.None)
                    throw new InvalidOperationException(L.T("Между играми доступны кнопки, оси и обратная связь руля."));
                transfer.MergeControls(input, output, to);
            }
            return transfer;
        }

        public string Apply() {
            return FileTransaction.Commit(updates, () => {
                source.Game.EnsureClosed();
                if (source.Game.Code != destination.Game.Code) destination.Game.EnsureClosed();
                ValidateProfile(source);
                ValidateProfile(destination);
                foreach (var file in observed) file.Verify();
                if (sourceLayouts != null && !sourceLayouts.SequenceEqual(LayoutNames(source), StringComparer.OrdinalIgnoreCase))
                    throw new IOException(L.T("Список раскладок КПП изменился. Обновите предпросмотр."));
            });
        }

        private static string ValidateProfile(Profile profile) {
            var path = Path.GetFullPath(profile.DirectoryPath);
            var parent = Path.GetDirectoryName(path);
            if (!string.Equals(parent, Path.GetFullPath(Path.Combine(profile.Game.Root, "profiles")), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(L.T("Выберите локальный профиль из папки profiles."));
            FileTransaction.EnsureDirectory(parent);
            FileTransaction.EnsureDirectory(path);
            if (!File.Exists(FileTransaction.NativePath(Path.Combine(path, "profile.sii"))))
                throw new IOException(L.T("Профиль больше не найден: ") + profile.Name);
            return path;
        }

        private FileSnapshot Observe(Profile profile, string name) {
            var file = new FileSnapshot(Path.Combine(profile.DirectoryPath, name));
            observed.Add(file);
            return file;
        }

        private void AddUpdate(FileSnapshot original, byte[] contents) {
            if (!FileSnapshot.Equal(original.Bytes, contents)) updates.Add(new FileUpdate(original, contents));
        }

        private static Sii ReadControls(FileSnapshot snapshot, Profile profile) {
            if (snapshot.Bytes == null)
                throw new IOException(L.T("В профиле «") + profile.Name + L.T("» нет настроек управления. Откройте этот профиль в игре, зайдите в «Настройки → Управление», затем закройте игру и нажмите ↻."));
            var doc = Sii.Read(snapshot.Bytes);
            var unit = doc.One("input_config");
            int version;
            if (!int.TryParse(unit.Get("version"), out version) || version <= 0 || unit.Array("config_lines").Count == 0)
                throw new InvalidDataException(L.T("Не удалось прочитать настройки управления профиля «") + profile.Name + "».");
            return doc;
        }

        private static string[] LayoutNames(Profile profile) {
            return Directory.EnumerateFiles(FileTransaction.NativePath(Path.GetFullPath(profile.DirectoryPath)))
                .Select(Path.GetFileName).Where(name => GearFile.IsMatch(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private void PrepareLayouts() {
            sourceLayouts = LayoutNames(source);
            if (sourceLayouts.Length == 0) return;
            foreach (var name in sourceLayouts) {
                var from = Observe(source, name);
                if (from.Bytes == null) throw new IOException(L.T("Раскладка КПП исчезла: ") + name);
                var doc = Sii.Read(from.Bytes);
                var gears = doc.One("direct_gearbox_config").Array("gears");
                Unit gear;
                if (gears.Count == 0 || gears.Any(id => !doc.ById.TryGetValue(id, out gear) || gear.Type != "direct_gearbox_gear"))
                    throw new InvalidDataException(L.T("Повреждена раскладка КПП: ") + name);
                AddUpdate(Observe(destination, name), from.Bytes);
            }
            Summary.Add(L.T("Раскладки КПП: ") + sourceLayouts.Length);
        }

        private static bool Extra(string key, ProfileSettings settings) {
            if ((settings & ProfileSettings.Sound) != 0 && key.StartsWith("s_", StringComparison.Ordinal) &&
                (key.EndsWith("_volume", StringComparison.Ordinal) || key.EndsWith("_mute", StringComparison.Ordinal))) return true;
            if ((settings & ProfileSettings.Camera) != 0 && (key.StartsWith("g_cam_", StringComparison.Ordinal) || key == "g_camera_horizon_lock")) return true;
            return (settings & ProfileSettings.Interface) != 0 && (key.StartsWith("g_hud_", StringComparison.Ordinal) ||
                key.StartsWith("g_adviser", StringComparison.Ordinal) || key.StartsWith("g_gps_", StringComparison.Ordinal) ||
                key.StartsWith("g_mirrors", StringComparison.Ordinal) || key == "g_ui_map_align" || key == "g_world_map_zoom" || key == "g_voice_navigation");
        }

        private void PrepareConfig(string name, Func<string, bool> include) {
            var from = Observe(source, name);
            var to = Observe(destination, name);
            if (from.Bytes == null || to.Bytes == null) {
                Notes.Add(name + L.T(" пропущен: файла нет в одном из профилей."));
                return;
            }
            var sourceText = Sii.Utf8.GetString(from.Bytes);
            var targetText = Sii.Utf8.GetString(to.Bytes);
            var sourceSettings = SettingsIn(sourceText, include);
            var targetSettings = SettingsIn(targetText, include);
            var common = sourceSettings.Keys.Count(targetSettings.ContainsKey);
            if (common == 0) {
                Notes.Add(name + L.T(" пропущен: общих параметров нет."));
                return;
            }
            var changed = 0;
            var output = Setting.Replace(targetText, match => {
                var key = match.Groups["key"].Value;
                var oldValue = match.Groups["value"];
                string value;
                if (!sourceSettings.TryGetValue(key, out value) || oldValue.Value == value) return match.Value;
                changed++;
                var offset = oldValue.Index - match.Index;
                return match.Value.Substring(0, offset) + value + match.Value.Substring(offset + oldValue.Length);
            });
            AddUpdate(to, Sii.Utf8.GetBytes(output));
            Summary.Add(name + L.T(": изменится ") + changed + L.T(" из ") + common + L.T(" общих параметров"));
            var missing = sourceSettings.Count - common;
            if (missing > 0) Notes.Add(name + L.T(": в профиле назначения нет ") + missing + L.T(" параметров источника."));
        }

        private static Dictionary<string, string> SettingsIn(string text, Func<string, bool> include) {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match match in Setting.Matches(text)) {
                var key = match.Groups["key"].Value;
                if (!include(key)) continue;
                if (entries.ContainsKey(key)) throw new InvalidDataException(L.T("Параметр указан дважды: ") + key);
                entries.Add(key, match.Groups["value"].Value);
            }
            return entries;
        }

        private sealed class Binding {
            public string Name, Type, Value, Raw;
        }

        private static List<Binding> ReadBindings(Unit input) {
            var result = new List<Binding>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in input.Array("config_lines")) {
                var match = ControlLine.Match(Sii.Unquote(line));
                if (!match.Success) throw new InvalidDataException(L.T("В controls.sii есть строка неизвестного формата. Обновите настройки управления в игре."));
                var name = match.Groups["name"].Value;
                if (!names.Add(name)) throw new InvalidDataException(L.T("Назначение указано дважды: ") + name);
                result.Add(new Binding { Name = name, Type = match.Groups["type"].Value, Value = match.Groups["value"].Value, Raw = line });
            }
            return result;
        }

        private void MergeControls(Sii input, Sii output, FileSnapshot to) {
            var fromUnit = input.One("input_config");
            var toUnit = output.One("input_config");
            if (fromUnit.Get("version") != toUnit.Get("version"))
                throw new InvalidDataException(L.T("Версии настроек управления различаются. Откройте настройки в обеих играх и попробуйте снова."));
            var sourceBindings = ReadBindings(fromUnit).ToDictionary(entry => entry.Name, StringComparer.Ordinal);
            var targetBindings = ReadBindings(toUnit);
            var targetByName = targetBindings.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
            var common = 0;
            var changed = 0;
            var devicesDiffer = false;
            var merged = new List<string>();
            foreach (var target in targetBindings) {
                Binding origin;
                if (!sourceBindings.TryGetValue(target.Name, out origin) || target.Type != origin.Type) {
                    merged.Add(target.Raw);
                    continue;
                }
                common++;
                if (origin.Value == target.Value) {
                    merged.Add(target.Raw);
                    continue;
                }
                foreach (Match reference in Reference.Matches(origin.Value)) {
                    Binding dependency, targetDependency;
                    if (sourceBindings.TryGetValue(reference.Value, out dependency) &&
                        (!targetByName.TryGetValue(reference.Value, out targetDependency) || targetDependency.Type != dependency.Type))
                        throw new InvalidDataException(L.T("Назначение «") + origin.Name + L.T("» использует «") + reference.Value + L.T("», которого нет в игре назначения."));
                }
                changed++;
                if (origin.Type == "device") devicesDiffer = true;
                merged.Add(origin.Raw);
            }
            if (common == 0) throw new InvalidDataException(L.T("Общих назначений управления не найдено."));
            if (changed > 0) {
                toUnit.SetArray("config_lines", merged);
                var text = output.ToText();
                if (!Sii.FromText(text).One("input_config").Array("config_lines").SequenceEqual(merged))
                    throw new InvalidDataException(L.T("Не удалось проверить новые настройки управления."));
                AddUpdate(to, Sii.Utf8.GetBytes(text));
            }
            Summary.Add(L.T("Общие назначения управления: ") + common + L.T(", изменится ") + changed);
            if (targetBindings.Count > common) Summary.Add(L.T("Назначения только в игре назначения: ") + (targetBindings.Count - common) + L.T(". Они сохранятся."));
            if (sourceBindings.Count > common) Notes.Add(L.T("Назначения только в игре источника пропущены: ") + (sourceBindings.Count - common) + ".");
            if (devicesDiffer) Notes.Add(L.T("Привязки устройств отличаются. Подключите те же устройства, что в исходном профиле, и проверьте кнопки и оси в игре."));
        }
    }
}
