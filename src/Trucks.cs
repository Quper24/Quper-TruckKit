using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace QuperDesk {
    internal sealed class Truck {
        public string Id, Model, Plate;
        public override string ToString() { return Model + (string.IsNullOrWhiteSpace(Plate) ? "" : "  ·  " + Plate); }
    }
    internal sealed class Garage {
        public string Id, City;
        public int Free;
        public override string ToString() { return City + L.T("  ·  свободно: ") + Free; }
    }
    internal static class Trucks {
        private static readonly Regex Ref = new Regex(@"^_nameless\.[a-fA-F0-9.]+$");
        private static Unit Require(Sii doc, string id) {
            Unit unit;
            if (!doc.ById.TryGetValue(id, out unit)) throw new InvalidDataException(L.T("Сохранение ссылается на отсутствующий блок «") + id + "».");
            return unit;
        }
        public static List<Truck> List(SaveSlot save) { return List(Sii.Load(save.Path)); }
        public static List<Truck> List(Sii doc) {
            return doc.One("player").Array("trucks").Select(id => {
                var vehicle = Require(doc, id);
                if (vehicle.Type != "vehicle") throw new InvalidDataException(L.T("В списке грузовиков найден неизвестный тип транспорта."));
                var path = vehicle.Array("accessories").Select(a => Sii.Unquote(Require(doc, a).Get("data_path")))
                    .FirstOrDefault(p => p != null && Regex.IsMatch(p, @"^/def/vehicle/truck/[^/]+/data\.sii$", RegexOptions.IgnoreCase));
                var model = path == null ? L.T("Грузовик") : path.Split('/')[4].Replace('.', ' ').Replace('_', ' ');
                if (model.Length > 0) model = char.ToUpperInvariant(model[0]) + model.Substring(1);
                var plate = Sii.Unquote(vehicle.Get("license_plate") ?? "").Split('|')[0];
                plate = Regex.Replace(plate, "<[^>]*>", "").Trim();
                return new Truck { Id = id, Model = model, Plate = plate };
            }).ToList();
        }
        public static List<Garage> Garages(SaveSlot save) { return Garages(Sii.Load(save.Path)); }
        public static List<Garage> Garages(Sii doc) {
            var player = doc.One("player");
            return doc.Units.Where(u => u.Type == "garage" && u.Get("status") != null && u.Get("status") != "0").Select(u => {
                var vehicles = u.Array("vehicles"); var drivers = u.Array("drivers");
                if (vehicles.Count != drivers.Count) throw new InvalidDataException(L.T("В сохранении нарушена структура гаража «") + u.Id + "».");
                var city = u.Id.StartsWith("garage.") ? u.Id.Substring(7) : u.Id;
                city = city.Replace('_', ' ');
                if (city.Length > 0) city = char.ToUpperInvariant(city[0]) + city.Substring(1);
                var free = Enumerable.Range(0, vehicles.Count).Count(i => FreeSlot(vehicles, drivers, player, i));
                return new Garage { Id = u.Id, City = city, Free = free };
            }).OrderByDescending(g => g.Free > 0).ThenBy(g => g.City).ToList();
        }
        private static bool FreeSlot(List<string> vehicles, List<string> drivers, Unit player, int i) {
            return vehicles[i] == "null" && (drivers[i] == "null" || drivers[i] == player.Get("drivers[0]"));
        }
        public static string Copy(SaveSlot source, SaveSlot target, IList<string> truckIds, string garageId) {
            var game = target.Profile.Game;
            game.EnsureClosed();
            if (source.Profile.Game.Code != game.Code || string.Equals(source.Profile.DirectoryPath, target.Profile.DirectoryPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(L.T("Выберите разные профили одной игры."));
            var sourceVersion = source.FormatVersion(); var targetVersion = target.FormatVersion();
            if (!string.IsNullOrEmpty(sourceVersion) && !string.IsNullOrEmpty(targetVersion) && sourceVersion != targetVersion)
                throw new InvalidOperationException(L.T("Сохранения записаны разными версиями игры. Загрузите оба профиля в одной версии и сделайте новые сохранения."));
            var donor = Sii.Load(source.Path); var destination = new FileChange(target.Path);
            Transfer(donor, destination.Doc, truckIds, garageId);
            return FileChange.Commit(game, destination);
        }
        public static string NoSpace { get { return L.T("Освободите место в гараже профиля назначения и заново сохраните игру."); } }
        internal static void Transfer(Sii donor, Sii destination, IList<string> truckIds, string garageId) {
            if (truckIds == null || truckIds.Count == 0 || truckIds.Distinct().Count() != truckIds.Count)
                throw new InvalidOperationException(L.T("Выберите грузовики для переноса."));
            var sourcePlayer = donor.One("player"); var targetPlayer = destination.One("player");
            var owned = sourcePlayer.Array("trucks");
            var trucks = targetPlayer.Array("trucks"); var logs = targetPlayer.Array("truck_profit_logs");
            var sourceLogs = sourcePlayer.Array("truck_profit_logs");
            if (trucks.Count != logs.Count || owned.Count != sourceLogs.Count)
                throw new InvalidDataException(L.T("Списки грузовиков и их истории не совпадают."));
            var garage = Require(destination, garageId);
            if (garage.Type != "garage" || garage.Get("status") == "0") throw new InvalidOperationException(L.T("Выберите купленный гараж."));
            var vehicles = garage.Array("vehicles"); var drivers = garage.Array("drivers");
            if (vehicles.Count != drivers.Count) throw new InvalidDataException(L.T("Сохранение содержит повреждённый гараж."));
            var free = new Queue<int>(Enumerable.Range(0, vehicles.Count).Where(i => FreeSlot(vehicles, drivers, targetPlayer, i)));
            if (free.Count < truckIds.Count) throw new InvalidOperationException(NoSpace);
            var staged = new List<Unit>();
            var allocated = new HashSet<string>(destination.ById.Keys);
            Func<string> newId = () => {
                string result;
                do { var hex = Guid.NewGuid().ToString("N").Substring(0, 8); result = "_nameless.7f0." + hex.Substring(0, 4) + "." + hex.Substring(4, 4); }
                while (!allocated.Add(result));
                return result;
            };
            foreach (var id in truckIds) {
                var index = owned.IndexOf(id);
                if (index < 0) throw new InvalidOperationException(L.T("Грузовик больше не найден в исходном сохранении."));
                var vehicle = Require(donor, id);
                if (vehicle.Type != "vehicle") throw new InvalidDataException(L.T("Выбранный транспорт не является грузовиком."));
                var copies = new Dictionary<string, string>();
                Action<Unit> visit = null;
                visit = u => {
                    if (copies.ContainsKey(u.Id)) return;
                    copies.Add(u.Id, newId());
                    foreach (var field in u.Fields.Where(f => Ref.IsMatch(f.Value))) {
                        var part = Require(donor, field.Value);
                        if (!part.Type.StartsWith("vehicle_", StringComparison.Ordinal) || !part.Type.EndsWith("accessory", StringComparison.Ordinal))
                            throw new InvalidDataException(L.T("Грузовик ссылается на неподдерживаемую деталь «") + part.Type + "».");
                        visit(part);
                    }
                };
                visit(vehicle);
                if (vehicle.Array("accessories").Any(part => !copies.ContainsKey(part)))
                    throw new InvalidDataException(L.T("Грузовик содержит аксессуар с неподдерживаемой ссылкой."));
                foreach (var entry in copies) {
                    var original = donor.ById[entry.Key]; var copy = new Unit { Id = entry.Value, Type = original.Type };
                    foreach (var f in original.Fields) { string mapped; copy.Add(f.Key, copies.TryGetValue(f.Value, out mapped) ? mapped : f.Value); }
                    staged.Add(copy);
                }
                var oldLog = Require(donor, sourceLogs[index]);
                if (oldLog.Type != "profit_log" || oldLog.Get("stats_data") == null || oldLog.Get("history_age") == null)
                    throw new InvalidDataException(L.T("Не распознан формат истории грузовика."));
                var log = new Unit { Id = newId(), Type = "profit_log" };
                log.Add("stats_data", "0"); log.Add("acc_distance_free", "0"); log.Add("acc_distance_on_job", "0"); log.Add("history_age", "nil");
                staged.Add(log);
                trucks.Add(copies[id]); logs.Add(log.Id); vehicles[free.Dequeue()] = copies[id];
            }
            foreach (var unit in staged) destination.Add(unit);
            targetPlayer.SetArray("trucks", trucks); targetPlayer.SetArray("truck_profit_logs", logs); garage.SetArray("vehicles", vehicles);
        }
    }
}
