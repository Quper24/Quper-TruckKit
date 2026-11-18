using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace QuperDesk {
    internal sealed class CareerProgress {
        // level_xp[0] in the standard truck and coach progression tables.
        public const int LevelOneXp = 200;
        private const string TruckXpField = "experience_points";
        private const string CoachXpField = "bus_experience_points";
        private const string MoneyField = "money_account";
        private readonly Unit bank, economy;
        private readonly List<Unit> ratedDeliveries = new List<Unit>();
        public readonly long Money, TruckXp;
        public readonly long? CoachXp;
        public bool CanAddTruckXp { get { return TruckXp >= LevelOneXp; } }
        public bool CanAddCoachXp { get { return CoachXp >= LevelOneXp; } }
        public readonly bool HasCourierLog;
        public readonly int FinishedDeliveries;
        public int RatingsToRaise { get { return ratedDeliveries.Count; } }

        public CareerProgress(Sii doc, string game) {
            bank = doc.One("bank");
            economy = doc.One("economy");
            Money = ReadNumber(bank, MoneyField);
            TruckXp = ReadNumber(economy, TruckXpField);
            if (game == "ets2" && economy.Get(CoachXpField) != null) CoachXp = ReadNumber(economy, CoachXpField);
            if (game != "ats") return;

            var logId = economy.Get("car_job_log");
            Unit log;
            if (string.IsNullOrEmpty(logId) || !doc.ById.TryGetValue(logId, out log) || log.Type != "car_job_log" || log.Get("version") != "1") return;
            try {
                var ids = log.Array("entries");
                if (ids.Distinct().Count() != ids.Count) return;
                var finished = 0;
                foreach (var id in ids) {
                    Unit entry;
                    if (!doc.ById.TryGetValue(id, out entry) || entry.Type != "car_job_log_entry") return;
                    if (entry.Get("type") != "finished_job") continue;
                    long rating = ReadNumber(entry, "rating_lvl");
                    if (rating < 0 || rating > 5) return;
                    finished++;
                    if (rating < 5) ratedDeliveries.Add(entry);
                }
                FinishedDeliveries = finished;
                HasCourierLog = true;
            } catch (InvalidDataException) { }
            finally { if (!HasCourierLog) ratedDeliveries.Clear(); }
        }

        public static long ReadNumber(Unit unit, string field) {
            long value;
            if (!long.TryParse(unit.Get(field), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                throw new InvalidDataException(L.T("Поле «") + field + L.T("» не найдено или повреждено."));
            return value;
        }

        public bool SameValues(CareerProgress expected) {
            return Money == expected.Money && TruckXp == expected.TruckXp && CoachXp == expected.CoachXp &&
                HasCourierLog == expected.HasCourierLog && ratedDeliveries.Select(RatingSnapshot).SequenceEqual(expected.ratedDeliveries.Select(RatingSnapshot));
        }

        private static Tuple<string, string> RatingSnapshot(Unit entry) {
            return Tuple.Create(entry.Id, entry.Get("rating_lvl"));
        }

        public bool Fits(long money, long truck, long coach) {
            return money >= 0 && truck >= 0 && coach >= 0 && (money == 0 || Money <= long.MaxValue - money) &&
                (truck == 0 || TruckXp >= 0 && truck <= int.MaxValue && TruckXp <= int.MaxValue - truck) &&
                (coach == 0 || CoachXp.HasValue && CoachXp.Value >= 0 && coach <= int.MaxValue && CoachXp.Value <= int.MaxValue - coach);
        }

        public void Apply(long money, long truck, long coach, bool raiseRatings) {
            if (coach > 0 && !CoachXp.HasValue) throw new InvalidOperationException(L.T("В этом сохранении нет опыта Coaches."));
            if (truck > 0 && !CanAddTruckXp) throw new InvalidOperationException(L.T("Сначала получите 1-й уровень в карьере грузовиков и сохраните игру."));
            if (coach > 0 && !CanAddCoachXp) throw new InvalidOperationException(L.T("Сначала получите 1-й уровень в карьере Coaches и сохраните игру."));
            if (!Fits(money, truck, coach)) throw new InvalidOperationException(L.T("Итоговое значение превышает предел игры."));
            if (raiseRatings && (!HasCourierLog || RatingsToRaise == 0))
                throw new InvalidOperationException(L.T("Нет завершённых доставок Road Trip с оценкой ниже 5 звёзд."));
            if (money > 0) bank.Set(MoneyField, Sii.Num(Money + money));
            if (truck > 0) economy.Set(TruckXpField, Sii.Num(TruckXp + truck));
            if (coach > 0) economy.Set(CoachXpField, Sii.Num(CoachXp.Value + coach));
            if (raiseRatings) foreach (var entry in ratedDeliveries) entry.Set("rating_lvl", "5");
        }

        public bool UpdateSummary(Unit summary, long money, long truck, long coach) {
            bool changed = false;
            if (money > 0 && summary.Get("info_money_account") != null) {
                summary.Set("info_money_account", Sii.Num(Money + money));
                changed = true;
            }
            if ((truck > 0 || coach > 0) && summary.Get("info_players_experience") != null) {
                var bus = economy.Get(CoachXpField) == null ? 0 : ReadNumber(economy, CoachXpField);
                var total = checked(ReadNumber(economy, TruckXpField) + bus);
                if (total < 0 || total > uint.MaxValue) throw new InvalidDataException(L.T("Сумма опыта превышает предел сводки сохранения."));
                summary.Set("info_players_experience", Sii.Num(total));
                changed = true;
            }
            return changed;
        }
    }
}
