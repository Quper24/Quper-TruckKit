using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QuperDesk {
    internal static class Smoke {
        private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
        private static void Same(byte[] a, string path) { Check(a.SequenceEqual(File.ReadAllBytes(FileTransaction.NativePath(path))), "Original file changed: " + System.IO.Path.GetFileName(path)); }
        [STAThread]
        private static void Main(string[] args) {
            Program.EnableLongPaths();
            Check(L.Detect(System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) == "ru" &&
                L.Detect(System.Globalization.CultureInfo.GetCultureInfo("de-DE")) == "en", "Windows language defaults");
            L.Select("en", false);
            Check(L.T("Сохранение") == "Save" && L.T("Профили") == "Profiles", "English translation resource");
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            L.Select("ru", false);
            CatalogOrigin source;
            var catalog = Catalog.Local(out source);
            Check(catalog.combos.Any(c => c.game == "ats" && c.version == "1.62") && catalog.combos.Any(c => c.game == "ets2"), "Missing site catalog");
            Console.WriteLine("Catalog: " + catalog.combos.Count + " presets");
            ControlsSmoke.Run(args.Contains("--local"));
            CheckModInventory();
            CheckXpGate();
            if (args.Contains("--local")) { CheckLocal(catalog); CheckDlcCareers(); }
            var render = args.FirstOrDefault(x => x.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
            if (render != null) Render(render, args.Contains("--local"), args.Contains("--preview-mods"));
        }
        private static void CheckLocal(Catalog catalog) {
            var temp = Path.Combine(Path.GetTempPath(), "quper-smoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try {
                foreach (var ats in new[] { false, true }) {
                    var real = Game.Create(ats);
                    var profile = real.Profiles().First(p => p.Saves().Count > 0);
                    var realSlot = profile.Saves()[0];
                    var game = Game.Create(ats, Path.Combine(temp, real.Code));
                    var sandbox = Path.Combine(game.Root, "profiles", "536F75726365");
                    var slotDir = Path.Combine(sandbox, "save", "1");
                    Directory.CreateDirectory(slotDir);
                    File.Copy(profile.FilePath, Path.Combine(sandbox, "profile.sii"));
                    File.Copy(realSlot.Path, Path.Combine(slotDir, "game.sii"));
                    File.Copy(realSlot.InfoPath, Path.Combine(slotDir, "info.sii"));
                    var from = game.Profiles().Single(); var fromSlot = from.Saves()[0];
                    var profileBytes = File.ReadAllBytes(from.FilePath);
                    var saveBytes = File.ReadAllBytes(fromSlot.Path);
                    var infoBytes = File.ReadAllBytes(fromSlot.InfoPath);
                    var clone = from.Clone("Копия Quper");
                    var to = game.Profiles().Single(p => p.DirectoryPath == clone);
                    Check(Sii.Unquote(Sii.Load(to.FilePath).One("user_profile").Get("profile_name")) == "Копия Quper", "Clone name");
                    var toSlot = to.Saves()[0];
                    Same(saveBytes, toSlot.Path);
                    var progress = toSlot.Progress();
                    var truckDelta = progress.CanAddTruckXp ? 345 : 0;
                    var backup = toSlot.AddProgress(12345, truckDelta, 0, false, progress);
                    var updated = toSlot.Progress();
                    Check(updated.Money == progress.Money + 12345 && updated.TruckXp == progress.TruckXp + truckDelta && updated.CoachXp == progress.CoachXp, "Progress values");
                    Same(saveBytes, Path.Combine(backup, "game.sii")); Same(infoBytes, Path.Combine(backup, "info.sii"));
                    var resultInfo = Sii.Load(toSlot.InfoPath).One("save_container");
                    Check(resultInfo.Get("info_money_account") == Sii.Num(progress.Money + 12345), "Money summary");
                    Check(resultInfo.Get("info_players_experience") == Sii.Num(progress.TruckXp + truckDelta + (progress.CoachXp ?? 0)), "XP summary");
                    var combo = catalog.combos.First(c => c.game == game.Code);
                    Check(!Directory.Exists(Path.Combine(game.Root, "mod")), "Expected a profile without mod files");
                    var previousProfile = File.ReadAllBytes(to.FilePath);
                    var modBackup = to.ApplyMods(combo);
                    Same(previousProfile, Path.Combine(modBackup, "profile.sii"));
                    Check(to.Mods().SequenceEqual(combo.mods.Select(m => m.key + "|" + m.name)), "Full order must be saved with all mod files missing");
                    var earlier = File.ReadAllBytes(to.FilePath);
                    var existing = to.Mods();
                    var sourceMods = from.Mods();
                    var copyBackup = to.CopyModsFrom(from, sourceMods, existing);
                    Same(earlier, Path.Combine(copyBackup, "profile.sii"));
                    Check(to.Mods().SequenceEqual(sourceMods), "Mod copy must replace the entire load order");
                    var manuallyOrdered = sourceMods.AsEnumerable().Reverse().Concat(new[] { "quper_test|Test mod" }).ToList();
                    var beforeManualOrder = File.ReadAllBytes(to.FilePath);
                    var manualBackup = to.SaveModOrder(sourceMods, manuallyOrdered);
                    Check(to.Mods().SequenceEqual(manuallyOrdered), "Manual mod order must round-trip without changes");
                    Same(beforeManualOrder, Path.Combine(manualBackup, "profile.sii"));
                    if (ats) CheckTrucks(fromSlot, toSlot);
                    Same(profileBytes, from.FilePath); Same(saveBytes, fromSlot.Path); Same(infoBytes, fromSlot.InfoPath);
                    Console.WriteLine(real.Code + ": encrypted files decoded; clone, progress, load order and backups verified" + (ats ? "; truck transfer and full garage guard verified" : ""));
                }
            } finally { Directory.Delete(FileTransaction.NativePath(temp), true); }
        }
        private static void CheckDlcCareers() {
            var temp = Path.Combine(Path.GetTempPath(), "quper-dlc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try {
                foreach (var ats in new[] { false, true }) {
                    var real = Game.Create(ats);
                    var origin = real.Profiles().SelectMany(p => p.Saves().Take(2)).OrderByDescending(s => s.Updated).FirstOrDefault(s => {
                        try {
                            var progress = s.Progress();
                            return ats ? progress.RatingsToRaise > 0 : progress.CanAddCoachXp;
                        } catch (IOException) { return false; }
                    });
                    Check(origin != null, "Missing real fixture for " + real.Code + " DLC career");
                    var game = Game.Create(ats, Path.Combine(temp, real.Code));
                    var profile = new Profile { Game = game, Name = "Test", DirectoryPath = game.Root };
                    var folder = Path.Combine(game.Root, "save");
                    Directory.CreateDirectory(folder);
                    File.Copy(origin.Path, Path.Combine(folder, "game.sii"));
                    File.Copy(origin.InfoPath, Path.Combine(folder, "info.sii"));
                    var slot = new SaveSlot(profile, folder);
                    var original = File.ReadAllBytes(slot.Path);
                    var originalInfo = File.ReadAllBytes(slot.InfoPath);
                    var before = slot.Progress();
                    var backup = slot.AddProgress(0, 0, ats ? 0 : 345, ats, before);
                    var after = slot.Progress();
                    Same(original, Path.Combine(backup, "game.sii"));
                    Check(after.Money == before.Money && after.TruckXp == before.TruckXp, "DLC edits changed money or truck XP");
                    if (ats) {
                        Same(originalInfo, slot.InfoPath);
                        Check(after.HasCourierLog && after.RatingsToRaise == 0 && after.FinishedDeliveries == before.FinishedDeliveries, "Road Trip rating edit");
                        var originalDoc = Sii.Read(original);
                        var result = Sii.Load(slot.Path);
                        var log = originalDoc.ById[originalDoc.One("economy").Get("car_job_log")];
                        Check(log.Fields.SequenceEqual(result.ById[log.Id].Fields), "Road Trip log list changed");
                        foreach (var id in log.Array("entries")) {
                            var oldEntry = originalDoc.ById[id];
                            var newEntry = result.ById[id];
                            Check(oldEntry.Fields.Where(f => f.Key != "rating_lvl").SequenceEqual(newEntry.Fields.Where(f => f.Key != "rating_lvl")), "Road Trip edited unrelated delivery data");
                            if (oldEntry.Get("type") == "finished_job") Check(newEntry.Get("rating_lvl") == "5", "Rating was not saved");
                        }
                    } else {
                        Same(originalInfo, Path.Combine(backup, "info.sii"));
                        Check(after.CoachXp == before.CoachXp + 345, "Coaches XP edit");
                        var summary = Sii.Load(slot.InfoPath).One("save_container");
                        Check(summary.Get("info_players_experience") == Sii.Num(after.TruckXp + after.CoachXp.Value), "Coaches XP summary");
                    }
                    Console.WriteLine(real.Code + ": DLC career change verified on a copy of a real save");
                }
            } finally { Directory.Delete(FileTransaction.NativePath(temp), true); }
        }
        private static void CheckXpGate() {
            foreach (var truckReady in new[] { false, true }) {
                var truck = truckReady ? 200 : 199;
                var coach = truckReady ? 199 : 200;
                var doc = Sii.FromText("SiiNunit\n{\nbank : bank.test {\n money_account: 0\n}\neconomy : economy.test {\n experience_points: " + truck + "\n bus_experience_points: " + coach + "\n}\n}\n");
                var before = new CareerProgress(doc, "ets2");
                Check(before.CanAddTruckXp == truckReady && before.CanAddCoachXp != truckReady, "Level 1 gates are independent");
                bool rejected = false;
                try { before.Apply(1, truckReady ? 0 : 1, truckReady ? 1 : 0, false); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected && new CareerProgress(doc, "ets2").SameValues(before), "XP below level 1 must be rejected before applying any changes");
                before.Apply(1, truckReady ? 1 : 0, truckReady ? 0 : 1, false);
                var after = new CareerProgress(doc, "ets2");
                Check(after.Money == 1 && after.TruckXp == (truckReady ? 201 : 199) && after.CoachXp == (truckReady ? 199 : 201), "Level 1 XP or money failed");
            }
            Console.WriteLine("XP editing: 199/200 XP boundary and independent career gates verified");
        }

        private static void CheckModInventory() {
            var inventory = new ModInventory(new[] { "COAST_TO_COAST_v2.24.62.0.scs", "promods-ats-map-v172.zip", "promods-ats-def-v171.scs", "Reforma_3_0_1_162.scs" });
            Check(inventory.Inspect(new Mod { key = "Coast_to_Coast_v2.24.62.1" }).Status == ModStatus.Outdated, "Older dotted version not detected");
            Check(inventory.Inspect(new Mod { key = "Reforma_3_0_4_162" }).Status == ModStatus.Outdated, "Older underscore version not detected");
            Check(inventory.Inspect(new Mod { key = "promods-ats-map-v171" }).Status == ModStatus.Different, "A newer version must not be labeled outdated");
            Check(inventory.Inspect(new Mod { key = "promods-ats-models-v171" }).Status == ModStatus.Missing, "An unrelated package was matched");
            Check(inventory.Inspect(new Mod { key = "alias", filename = "promods-ats-def-v171.scs" }).Status == ModStatus.Found, "Explicit file name not found");
            Check(inventory.Inspect(new Mod { key = "coast_to_coast_v2.24.62.0" }).Status == ModStatus.Found, "Exact file match was not preferred");
            Console.WriteLine("Mod inventory: exact, missing, outdated and different-version packages verified");
        }
        private static void CheckTrucks(SaveSlot from, SaveSlot to) {
            var owned = Trucks.List(from);
            var garage = Trucks.Garages(to).First();
            var destination = Sii.Load(to.Path);
            var unit = destination.ById[garage.Id];
            var vehicles = unit.Array("vehicles");
            var player = destination.One("player");
            var before = player.Array("trucks"); var logs = player.Array("truck_profit_logs");
            Check(owned.Count > 0 && vehicles.Count > 0, "Missing real fleet fixture");
            unit.SetArray("vehicles", vehicles.Select(v => v == "null" ? owned[0].Id : v).ToList());
            File.WriteAllText(to.Path, destination.ToText(), Sii.Utf8);
            var full = File.ReadAllBytes(to.Path);
            try { Trucks.Copy(from, to, new[] { owned[0].Id }, garage.Id); throw new Exception("Full garage was accepted"); }
            catch (InvalidOperationException e) { Check(e.Message == Trucks.NoSpace, "Missing full garage guidance"); }
            Same(full, to.Path);
            vehicles[0] = "null"; unit.SetArray("vehicles", vehicles);
            File.WriteAllText(to.Path, destination.ToText(), Sii.Utf8);
            var old = File.ReadAllBytes(to.Path);
            var path = Trucks.Copy(from, to, new[] { owned[0].Id }, garage.Id);
            Same(old, Path.Combine(path, "game.sii"));
            var result = Sii.Load(to.Path); var winner = result.One("player");
            var newId = winner.Array("trucks").Last();
            Check(!before.Contains(newId) && winner.Array("trucks").Count == before.Count + 1 && winner.Array("truck_profit_logs").Count == logs.Count + 1, "Truck arrays");
            Check(result.ById[garage.Id].Array("vehicles")[0] == newId, "Garage pointer");
            var copied = result.ById[newId].Array("accessories");
            var donor = Sii.Load(from.Path); var originals = donor.ById[owned[0].Id].Array("accessories");
            Check(copied.Count == originals.Count && !copied.Intersect(originals).Any(), "Fresh accessory ids");
            Check(copied.Select(x => result.ById[x].Get("data_path")).SequenceEqual(originals.Select(x => donor.ById[x].Get("data_path"))), "Accessory configuration");
        }
        private static IEnumerable<DependencyObject> LayoutElements(DependencyObject root) {
            yield return root;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (var item in LayoutElements(child)) yield return item;
        }

        private static void Render(string path, bool useLocal, bool previewMods) {
            var app = new Application();
            using (var theme = Assembly.GetExecutingAssembly().GetManifestResourceStream("Quper.Theme"))
                app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(theme));
            var window = new MainWindow();
            window.ChangeLanguage("en", false);
            var show = typeof(MainWindow).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic);
            for (var i = 0; i < 8; i++) show.Invoke(window, new object[] { i });
            Check(LayoutElements((DependencyObject)window.Content).OfType<TextBlock>().Any(block => block.Text == "Help"), "English navigation did not refresh");
            window.ChangeLanguage("ru", false);
            if (previewMods) {
                var bindings = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(MainWindow).GetMethod("SwitchGame", bindings).Invoke(window, new object[] { 1 });
                typeof(MainWindow).GetMethod("ShowPage", bindings).Invoke(window, new object[] { 3 });
                var buttons = LayoutElements((DependencyObject)window.Content).OfType<Button>().ToList();
                var apply = buttons.Single(button => (button.Content as string ?? "").StartsWith("Применить порядок"));
                var website = buttons.Single(button => (button.Content as string ?? "").StartsWith("Страница сборки на сайте"));
                Check(apply.IsEnabled, "Mod warnings must not disable applying the order");
                Check((website.ToolTip as string ?? "").StartsWith("https://qupersimulator.ru/"), "Selected build has no website URL");
            } else if (useLocal) {
                System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
                typeof(MainWindow).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, new object[] { 4 });
                var elements = LayoutElements((DependencyObject)window.Content).ToList();
                var game = elements.OfType<ComboBox>().Single(box => AutomationProperties.GetName(box) == "Игра источника");
                var profile = elements.OfType<ComboBox>().Single(box => AutomationProperties.GetName(box) == "Профиль источника");
                game.SelectedIndex = 1;
                profile.SelectedItem = profile.Items.OfType<Profile>().First(p => File.Exists(Path.Combine(p.DirectoryPath, "controls.sii")));
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                timer.Tick += (sender, e) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
                Check(elements.OfType<TextBlock>().Any(block => block.Text.Contains("Общие назначения управления")), "Controls preview did not load");
            }
            var size = new Size(1150, 760);
            var content = (FrameworkElement)window.Content;
            content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
            var render = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            render.Render(content);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(render));
            using (var file = File.Create(path)) png.Save(file);
            Console.WriteLine("WPF layout rendered");
        }
    }
}
