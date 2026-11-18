using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

namespace QuperDesk {
    internal static class L {
        private static readonly string Preferences = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quper TruckKit", "language.txt");
        private static readonly Lazy<Dictionary<string, string>> English = new Lazy<Dictionary<string, string>>(LoadEnglish);
        private static string selectedLanguage = Detect(CultureInfo.CurrentUICulture);
        public static string Language { get { return selectedLanguage; } private set { selectedLanguage = value; } }
        public static CultureInfo Culture { get { return CultureInfo.GetCultureInfo(Language == "ru" ? "ru-RU" : "en-US"); } }

        internal static string Detect(CultureInfo windowsLanguage) {
            return windowsLanguage.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
        }

        public static void Initialize() {
            try {
                if (!File.Exists(Preferences)) return;
                var saved = File.ReadAllText(Preferences).Trim();
                if (saved == "ru" || saved == "en") Language = saved;
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        public static void Select(string language, bool save = true) {
            if (language != "ru" && language != "en") throw new ArgumentOutOfRangeException("language");
            if (save) {
                Directory.CreateDirectory(Path.GetDirectoryName(Preferences));
                File.WriteAllText(Preferences, language);
            }
            Language = language;
        }

        public static string T(string russian) {
            string translated;
            return Language == "en" && English.Value.TryGetValue(russian, out translated) ? translated : russian;
        }

        internal static bool HasEnglish(string russian) { return English.Value.ContainsKey(russian); }

        private static Dictionary<string, string> LoadEnglish() {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Quper.English"))
            using (var reader = new StreamReader(stream))
                return new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
        }
    }
}
