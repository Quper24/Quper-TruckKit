using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace QuperDesk {
    internal static class ControlsSmoke {
        private const string Controls = "controls.sii";

        public static void Run(bool useLocal) {
            var temp = Path.Combine(Path.GetTempPath(), "quper-controls-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try {
                var ets = Game.Create(false, Path.Combine(temp, "ets2"));
                var ats = Game.Create(true, Path.Combine(temp, "ats"));
                foreach (var game in new[] { ets, ats }) CheckWithinGame(game);
                CheckCrossGame(ets, ats);
                CheckRollback(temp);
                if (useLocal) CheckLocalControls(temp);
                Console.WriteLine("Controls: same-game settings, cross-game bindings, stale preview, missing files, running game and rollback verified");
            } finally { Directory.Delete(FileTransaction.NativePath(temp), true); }
        }

        private static Profile ProfileFor(Game game, string name) {
            var id = BitConverter.ToString(Sii.Utf8.GetBytes(name)).Replace("-", "");
            var path = Path.Combine(game.Root, "profiles", id);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "profile.sii"), "test profile");
            return game.Profiles().Single(p => p.DirectoryPath == path);
        }

        private static void Put(Profile profile, string name, string content) {
            File.WriteAllText(Path.Combine(profile.DirectoryPath, name), content, Sii.Utf8);
        }

        private static string Get(Profile profile, string name) {
            return Sii.Utf8.GetString(File.ReadAllBytes(Path.Combine(profile.DirectoryPath, name)));
        }

        private static string Input(string id, int version, params string[] bindings) {
            var doc = new Sii();
            var unit = new Unit { Type = "input_config", Id = id };
            unit.Add("version", Sii.Num(version));
            unit.SetArray("config_lines", bindings.Select(Sii.Quote).ToList());
            doc.Add(unit);
            return doc.ToText();
        }

        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

        private static void Expect<T>(Action action, string detail) where T : Exception {
            try { action(); }
            catch (T e) {
                Check(e.Message.Contains(detail), "Unexpected error: " + e.Message);
                return;
            }
            throw new Exception("Missing expected error: " + detail);
        }

        private static void CheckWithinGame(Game game) {
            const string global = "uset g_lang \"en_us\"\r\n";
            const string gearbox = "SiiNunit\n{\ndirect_gearbox_config : gearbox.test {\n gears: 1\n gears[0]: gearbox.gear\n}\n" +
                "direct_gearbox_gear : gearbox.gear {\n gear_impulse_index: 1\n}\n}\n";
            var from = ProfileFor(game, "Source");
            var to = ProfileFor(game, "Target");
            var donor = Input("input.source", 32, "mix horn `keyboard.h?0`");
            var original = Input("input.target", 32, "mix horn `keyboard.k?0`");
            Put(from, Controls, donor);
            Put(to, Controls, original);
            Put(from, "gearbox_custom.sii", gearbox);
            Put(from, "config.cfg", "uset g_adaptive_shift \"2\"\nuset g_cam_steering \"1\"\nuset g_gps_navigation \"1\"\nuset g_lang \"ru_ru\"\n");
            var oldConfig = "\ufeff  uset g_adaptive_shift \"0\"  # shift\r\nuset g_cam_steering \"0\"\r\nuset g_gps_navigation \"0\"\r\n" + global;
            Put(to, "config.cfg", oldConfig);
            Put(from, "config_local.cfg", "uset g_trans \"2\"\nuset s_master_volume \"1.0\"\nuset g_force_economy_reset \"1\"\n");
            const string oldLocal = "uset g_trans \"0\"\r\nuset s_master_volume \"0.3\"\r\nuset g_force_economy_reset \"0\"\r\n";
            Put(to, "config_local.cfg", oldLocal);
            File.WriteAllText(Path.Combine(game.Root, "config.cfg"), global, Sii.Utf8);
            var transfer = ControlsTransfer.Prepare(from, to, ProfileSettings.None);
            var originalProcess = game.ProcessName;
            using (var process = Process.GetCurrentProcess()) game.ProcessName = process.ProcessName;
            try { Expect<InvalidOperationException>(() => transfer.Apply(), "Закройте"); }
            finally { game.ProcessName = originalProcess; }
            Check(!Directory.Exists(Path.Combine(to.DirectoryPath, ".quper-backups")), "Running game guard created files");

            var backup = transfer.Apply();
            Check(transfer.FileCount == 4 && Get(to, Controls) == donor, "Same-game controls must be copied byte-for-byte");
            Check(Get(to, "gearbox_custom.sii") == gearbox, "Custom gearbox was not copied");
            Check(File.ReadAllLines(Path.Combine(backup, "created-files.txt")).SequenceEqual(new[] { "gearbox_custom.sii" }), "Missing created-file record");
            Check(File.ReadAllText(Path.Combine(backup, Controls)) == original, "Controls backup mismatch");
            Check(Sii.Utf8.GetString(File.ReadAllBytes(Path.Combine(backup, "config.cfg"))) == oldConfig, "Config backup mismatch");
            Check(Get(to, "config.cfg") == oldConfig.Replace("g_adaptive_shift \"0\"", "g_adaptive_shift \"2\""), "Default config changed unselected settings or formatting");
            Check(Get(to, "config_local.cfg") == oldLocal.Replace("g_trans \"0\"", "g_trans \"2\""), "Default local config changed unselected settings");

            var extras = ProfileSettings.Sound | ProfileSettings.Camera | ProfileSettings.Interface;
            var pending = ControlsTransfer.Prepare(from, to, extras);
            Put(from, "config.cfg", Get(from, "config.cfg") + "# changed after preview\n");
            Expect<IOException>(() => pending.Apply(), "изменился");
            Check(Get(to, "config.cfg").Contains("g_gps_navigation \"0\""), "Stale preview changed files");
            ControlsTransfer.Prepare(from, to, extras).Apply();
            Check(Get(to, "config.cfg").Contains("g_cam_steering \"1\"") && Get(to, "config.cfg").Contains("g_gps_navigation \"1\""), "Camera or navigation not copied");
            Check(Get(to, "config_local.cfg").Contains("s_master_volume \"1.0\""), "Sound not copied");
            Check(Get(to, "config_local.cfg").Contains("g_force_economy_reset \"0\"") && Get(to, "config.cfg").Contains(global), "Unrelated settings changed");
            Check(Get(from, Controls) == donor && File.ReadAllText(Path.Combine(game.Root, "config.cfg")) == global, "Source or global settings changed");
            Check(ControlsTransfer.Prepare(from, to, extras).FileCount == 0, "Already copied settings should require no write");
            var empty = ProfileFor(game, "Empty");
            Expect<IOException>(() => ControlsTransfer.Prepare(from, empty, ProfileSettings.None), "Настройки → Управление");
        }

        private static void CheckCrossGame(Game ets, Game ats) {
            foreach (var game in new[] { ets, ats }) {
                var other = game == ets ? ats : ets;
                var from = ProfileFor(game, "Cross source");
                var to = ProfileFor(other, "Cross target");
                var only = other.Code + "only";
                Put(from, Controls, Input("input.source", 32, "device joy `di8.wheel`", "input j_steer `joy.y`", "constant c_ff_gain 0.6", "mix horn `joy.b2?0`", "mix sourceonly `keyboard.s?0`"));
                var original = Input("input.target", 32, "device joy ``", "mix " + only + " `keyboard.f?0`", "input j_steer `joy.x`", "constant c_ff_gain 1.0", "mix horn `keyboard.h?0`");
                Put(to, Controls, original);
                Put(to, "config_local.cfg", "uset g_trans \"0\"\n");
                var transfer = ControlsTransfer.Prepare(from, to, ProfileSettings.None);
                Check(transfer.FileCount == 1 && transfer.Notes.Count == 2, "Cross-game preview missing device or skipped binding notice");
                transfer.Apply();
                var unit = Sii.Read(File.ReadAllBytes(Path.Combine(to.DirectoryPath, Controls))).One("input_config");
                var lines = unit.Array("config_lines").Select(Sii.Unquote).ToArray();
                Check(unit.Id == "input.target" && lines.SequenceEqual(new[] { "device joy `di8.wheel`", "mix " + only + " `keyboard.f?0`", "input j_steer `joy.y`", "constant c_ff_gain 0.6", "mix horn `joy.b2?0`" }), "Cross-game merge altered the target schema or lost bindings");
                Check(Get(to, "config_local.cfg") == "uset g_trans \"0\"\n", "Cross-game touched config");
                Put(from, Controls, Input("input.source", 33, "mix horn `keyboard.k?0`"));
                Expect<InvalidDataException>(() => ControlsTransfer.Prepare(from, to, ProfileSettings.None), "Версии");
                Put(from, Controls, Input("input.source", 32, "input custom `joy.z`", "mix horn `custom`"));
                Expect<InvalidDataException>(() => ControlsTransfer.Prepare(from, to, ProfileSettings.None), "custom");
                Put(from, Controls, Input("input.source", 32, "mix horn `keyboard.h?0`", "mix horn `keyboard.k?0`"));
                Expect<InvalidDataException>(() => ControlsTransfer.Prepare(from, to, ProfileSettings.None), "дважды");
            }
        }

        private static void CheckRollback(string folder) {
            var first = Path.Combine(folder, "first.cfg");
            var created = Path.Combine(folder, "gearbox_new.sii");
            var blocked = Path.Combine(folder, "blocked.cfg");
            File.WriteAllText(first, "original first");
            File.WriteAllText(blocked, "original last");
            var updates = new[] { first, created, blocked }.Select(path => new FileUpdate(new FileSnapshot(path), Sii.Utf8.GetBytes("changed"))).ToArray();
            using (File.Open(blocked, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                bool failed = false;
                try { FileTransaction.Commit(updates, () => { }); }
                catch (IOException) { failed = true; }
                Check(failed, "Locked destination did not fail the transaction");
            }
            Check(File.ReadAllText(first) == "original first" && File.ReadAllText(blocked) == "original last" && !File.Exists(created), "Partial write was not rolled back");
            var backup = Directory.GetDirectories(Path.Combine(folder, ".quper-backups")).Single();
            Check(File.ReadAllText(Path.Combine(backup, "blocked.cfg")) == "original last" && !Directory.GetFiles(folder, ".quper-temp-*").Any(), "Backup staging or cleanup incomplete");
        }

        private static void CheckLocalControls(string temp) {
            var copies = new Profile[2];
            for (var i = 0; i < copies.Length; i++) {
                var original = Game.Create(i == 1).Profiles().First(profile => File.Exists(Path.Combine(profile.DirectoryPath, Controls)));
                var game = Game.Create(i == 1, Path.Combine(temp, "real-" + original.Game.Code));
                var from = ProfileFor(game, "Local source");
                var to = ProfileFor(game, "Local destination");
                var files = Directory.GetFiles(original.DirectoryPath, "gearbox*.sii").Concat(new[] { Controls, "config.cfg", "config_local.cfg" }.Select(name => Path.Combine(original.DirectoryPath, name))).Where(File.Exists);
                foreach (var file in files) File.Copy(file, Path.Combine(from.DirectoryPath, Path.GetFileName(file)));
                File.Copy(Path.Combine(from.DirectoryPath, Controls), Path.Combine(to.DirectoryPath, Controls));
                var transfer = ControlsTransfer.Prepare(from, to, ProfileSettings.None);
                if (transfer.FileCount > 0) transfer.Apply();
                copies[i] = from;
            }
            for (var i = 0; i < copies.Length; i++) {
                var from = copies[i];
                var baseTarget = copies[1 - i];
                var to = ProfileFor(baseTarget.Game, "Local cross-game destination");
                File.Copy(Path.Combine(baseTarget.DirectoryPath, Controls), Path.Combine(to.DirectoryPath, Controls));
                var transfer = ControlsTransfer.Prepare(from, to, ProfileSettings.None);
                if (transfer.FileCount > 0) transfer.Apply();
                Check(transfer.Summary.Any(s => s.Contains("Общие назначения")), "Real controls were not merged");
            }
            Console.WriteLine("Controls: real ETS2 and ATS layouts and controls parsed and transferred using temporary profile copies");
        }
    }
}
