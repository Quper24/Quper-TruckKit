using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace QuperDesk {
    internal sealed class Game {
        public string Code, Title, ProcessName, Root;
        public static Game Create(bool ats, string root = null) {
            var title = ats ? "American Truck Simulator" : "Euro Truck Simulator 2";
            return new Game { Code = ats ? "ats" : "ets2", Title = title, ProcessName = ats ? "amtrucks" : "eurotrucks2",
                Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), title) };
        }
        public void EnsureClosed() {
            var processes = Process.GetProcessesByName(ProcessName);
            try { if (processes.Length != 0) throw new InvalidOperationException(L.T("Закройте «") + Title + L.T("» и повторите действие.")); }
            finally { foreach (var p in processes) p.Dispose(); }
        }
        public List<Profile> Profiles() {
            var folder = Path.Combine(Root, "profiles");
            if (!Directory.Exists(folder)) return new List<Profile>();
            return Directory.EnumerateDirectories(folder).Where(p => Regex.IsMatch(Path.GetFileName(p), "^(?:[a-fA-F0-9]{2})+$") && File.Exists(Path.Combine(p, "profile.sii")))
                .Select(p => new Profile { Game = this, DirectoryPath = p, Name = Profile.FolderName(p) })
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        public bool HasCloud {
            get { var dir = Path.Combine(Root, "steam_profiles"); return Directory.Exists(dir) && Directory.EnumerateDirectories(dir).Any(); }
        }
        public string DetectedVersion() {
            var path = Path.Combine(Root, "game.log.txt");
            if (!File.Exists(path)) return null;
            try {
                foreach (var line in File.ReadLines(path).Take(120)) {
                    var m = Regex.Match(line, @"\binit ver\.(\d+\.\d+)\b");
                    if (m.Success) return m.Groups[1].Value;
                }
            } catch (IOException) { }
            return null;
        }
        public HashSet<string> InstalledMods() {
            var dir = Path.Combine(Root, "mod");
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(dir)) return keys;
            foreach (var path in Directory.EnumerateFileSystemEntries(dir)) {
                var ext = Path.GetExtension(path);
                if (Directory.Exists(path) || ext.Equals(".scs", StringComparison.OrdinalIgnoreCase) || ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    keys.Add(Path.GetFileName(path));
            }
            return keys;
        }
    }

    internal sealed class Profile {
        public Game Game;
        public string Name, DirectoryPath;
        public string FilePath { get { return Path.Combine(DirectoryPath, "profile.sii"); } }
        public override string ToString() { return Name; }
        public static string FolderName(string path) {
            var id = Path.GetFileName(path);
            try { return Sii.Utf8.GetString(Enumerable.Range(0, id.Length / 2).Select(i => Convert.ToByte(id.Substring(i * 2, 2), 16)).ToArray()); }
            catch (ArgumentException) { return id; }
        }
        public List<SaveSlot> Saves() {
            var root = Path.Combine(DirectoryPath, "save");
            if (!Directory.Exists(root)) return new List<SaveSlot>();
            return Directory.EnumerateDirectories(root).Where(p => File.Exists(Path.Combine(p, "game.sii")))
                .Select(p => new SaveSlot(this, p)).OrderByDescending(s => s.Updated).ToList();
        }
        public string Clone(string name) {
            Game.EnsureClosed();
            name = (name ?? "").Trim();
            if (name.Length < 2 || name.Length > 32 || name.Any(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c)))
                throw new InvalidOperationException(L.T("Введите от 2 до 32 символов. Не используйте \\ / : * ? \" < > |."));
            var folderId = BitConverter.ToString(Sii.Utf8.GetBytes(name)).Replace("-", "");
            var parent = Path.GetDirectoryName(DirectoryPath);
            var destination = Path.Combine(parent, folderId);
            if (Directory.Exists(destination) || Directory.Exists(Path.Combine(Game.Root, "steam_profiles", folderId)))
                throw new InvalidOperationException(L.T("Профиль с таким именем уже существует."));
            var doc = Sii.Load(FilePath);
            var unit = doc.One("user_profile");
            if (unit.Get("profile_name") == null) throw new InvalidDataException(L.T("В профиле не найдено поле имени."));
            unit.Set("profile_name", Sii.Quote(name));
            foreach (var field in new[] { "online_user_name", "online_password" }) if (unit.Get(field) != null) unit.Set(field, "\"\"");
            var stage = Path.Combine(parent, ".quper-copy-" + Guid.NewGuid().ToString("N"));
            try {
                CopyDirectory(DirectoryPath, stage, true);
                File.WriteAllText(Path.Combine(stage, "profile.sii"), doc.ToText(), Sii.Utf8);
                Game.EnsureClosed();
                Directory.Move(stage, destination);
            } finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
            return destination;
        }
        private static void CopyDirectory(string source, string target, bool top) {
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException(L.T("Профиль содержит ссылку на другую папку."));
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source)) {
                var name = Path.GetFileName(file);
                if (top && new[] { "profile.sii", "profile.bak.sii", "profile.sii.bak" }.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (name.StartsWith(".quper-", StringComparison.OrdinalIgnoreCase)) continue;
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException(L.T("Профиль содержит ссылку на другой файл."));
                var copy = Path.Combine(target, name);
                File.Copy(file, copy);
                File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(file));
            }
            foreach (var dir in Directory.EnumerateDirectories(source)) {
                if (!Path.GetFileName(dir).StartsWith(".quper-", StringComparison.OrdinalIgnoreCase))
                    CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)), false);
            }
        }
        public List<string> Mods() { return Sii.Load(FilePath).One("user_profile").Array("active_mods").Select(Sii.Unquote).ToList(); }
        public string SaveModOrder(IList<string> expected, IList<string> order) {
            Game.EnsureClosed();
            var change = new FileChange(FilePath);
            var profile = change.Doc.One("user_profile");
            if (!profile.Array("active_mods").Select(Sii.Unquote).SequenceEqual(expected))
                throw new InvalidOperationException(L.T("Список модов изменился. Обновите предпросмотр."));
            profile.SetArray("active_mods", order.Select(Sii.Quote).ToList());
            return FileChange.Commit(Game, change);
        }
        public string CopyModsFrom(Profile source, IList<string> expectedSource, IList<string> expectedDestination) {
            Game.EnsureClosed();
            if (Game.Code != source.Game.Code || string.Equals(FilePath, source.FilePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(L.T("Выберите разные профили одной игры."));
            var change = new FileChange(FilePath);
            var profile = change.Doc.One("user_profile");
            var current = profile.Array("active_mods");
            var sourceMods = source.Mods();
            if (!sourceMods.SequenceEqual(expectedSource) || !current.Select(Sii.Unquote).SequenceEqual(expectedDestination))
                throw new InvalidOperationException(L.T("Список модов изменился. Обновите предпросмотр."));
            profile.SetArray("active_mods", sourceMods.Select(Sii.Quote).ToList());
            return FileChange.Commit(Game, change);
        }
        public string ApplyMods(Combo combo) {
            Game.EnsureClosed();
            if (Game.Code != combo.game) throw new InvalidOperationException(L.T("Сборка предназначена для другой игры."));
            var change = new FileChange(FilePath);
            var profile = change.Doc.One("user_profile");
            profile.Array("active_mods");
            profile.SetArray("active_mods", combo.mods.Select(m => Sii.Quote(m.key + "|" + m.name)).ToList());
            return FileChange.Commit(Game, change);
        }
    }

    internal sealed class SaveSlot {
        public readonly Profile Profile;
        public readonly string Dir, Path, InfoPath, Name;
        public readonly DateTime Updated;
        public SaveSlot(Profile profile, string dir) {
            Profile = profile; Dir = dir; Path = System.IO.Path.Combine(dir, "game.sii");
            InfoPath = System.IO.Path.Combine(dir, "info.sii");
            var key = System.IO.Path.GetFileName(dir);
            Updated = File.GetLastWriteTime(Path);
            Name = key == "autosave" ? L.T("Автосохранение") : key == "quicksave" ? L.T("Быстрое сохранение") : key;
            if (File.Exists(InfoPath) && key != "autosave" && key != "quicksave") {
                try { var label = Sii.Unquote(Sii.Load(InfoPath).One("save_container").Get("name"));
                    if (!string.IsNullOrWhiteSpace(label) && !label.StartsWith("@@")) Name = label; }
                catch (IOException) { } catch (ArgumentException) { } catch (System.Security.Cryptography.CryptographicException) { }
            }
        }
        public override string ToString() { return Name + "  ·  " + Updated.ToString("g", L.Culture); }
        public CareerProgress Progress() { return new CareerProgress(Sii.Load(Path), Profile.Game.Code); }
        public string AddProgress(long money, long truckXp, long coachXp, bool raiseRatings, CareerProgress expected) {
            Profile.Game.EnsureClosed();
            if (money == 0 && truckXp == 0 && coachXp == 0 && !raiseRatings)
                throw new InvalidOperationException(L.T("Укажите, что изменить в сохранении."));
            var file = new FileChange(Path);
            var current = new CareerProgress(file.Doc, Profile.Game.Code);
            if (expected != null && !current.SameValues(expected)) throw new InvalidOperationException(L.T("Сохранение обновилось. Перечитайте значения перед записью."));
            current.Apply(money, truckXp, coachXp, raiseRatings);
            if ((money == 0 && truckXp == 0 && coachXp == 0) || !File.Exists(InfoPath))
                return FileChange.Commit(Profile.Game, file);
            var info = new FileChange(InfoPath);
            return current.UpdateSummary(info.Doc.One("save_container"), money, truckXp, coachXp)
                ? FileChange.Commit(Profile.Game, file, info) : FileChange.Commit(Profile.Game, file);
        }
        public string FormatVersion() {
            return File.Exists(InfoPath) ? Sii.Load(InfoPath).One("save_container").Get("version") : null;
        }
    }

    internal sealed class FileChange {
        public readonly Sii Doc;
        private readonly FileSnapshot original;
        public FileChange(string path) {
            original = new FileSnapshot(path);
            if (original.Bytes == null) throw new FileNotFoundException(L.T("Файл сохранения не найден."), path);
            Doc = Sii.Read(original.Bytes);
        }
        public static string Commit(Game game, params FileChange[] changes) {
            game.EnsureClosed();
            var updates = changes.Select(change => {
                var text = change.Doc.ToText();
                if (Sii.FromText(text).Units.Count != change.Doc.Units.Count)
                    throw new InvalidDataException(L.T("Не удалось перепроверить сохранение."));
                return new FileUpdate(change.original, Sii.Utf8.GetBytes(text));
            }).ToList();
            return FileTransaction.Commit(updates, game.EnsureClosed);
        }
    }
}
