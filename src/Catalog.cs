using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace QuperDesk {
    internal sealed class Mod {
        public string key { get; set; }
        public string name { get; set; }
        public string filename { get; set; }
    }
    internal sealed class Combo {
        public string id { get; set; }
        public string title { get; set; }
        public string game { get; set; }
        public string version { get; set; }
        public string release { get; set; }
        public string page { get; set; }
        public string orderUrl { get; set; }
        public string[] dlcs { get; set; }
        public Mod[] mods { get; set; }
        public string DisplayTitle {
            get {
                const string suffix = " · с модами";
                return title.EndsWith(suffix, StringComparison.Ordinal) ? title.Substring(0, title.Length - suffix.Length) + L.T(suffix) : title;
            }
        }
        public override string ToString() { return version + "   ·   " + DisplayTitle; }
    }
    internal sealed class CatalogOrigin {
        private readonly bool cached;
        private readonly DateTime? timestamp;
        public CatalogOrigin(bool cached, DateTime? timestamp) { this.cached = cached; this.timestamp = timestamp; }
        public string Label {
            get {
                if (!timestamp.HasValue) return L.T("Каталог из проекта сайта");
                return (cached ? L.T("Кэш сайта · ") : L.T("С сайта · ")) + timestamp.Value.ToString("g", L.Culture);
            }
        }
    }
    internal sealed class Catalog {
        public const string Url = "https://qupersimulator.ru/app/catalog.json";
        private const int Max = 4 * 1024 * 1024;
        private static readonly string Cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quper Simulator", "catalog.json");
        public int schema { get; set; }
        public List<Combo> combos { get; set; }
        public static Catalog Parse(string json) {
            var catalog = new JavaScriptSerializer { MaxJsonLength = Max, RecursionLimit = 16 }.Deserialize<Catalog>(json);
            if (catalog == null || catalog.schema != 1 || catalog.combos == null || catalog.combos.Count < 1 || catalog.combos.Count > 500)
                throw new InvalidDataException(L.T("Неверный формат каталога."));
            var ids = new HashSet<string>();
            foreach (var combo in catalog.combos) {
                if (combo == null || !SafeText(combo.id, 160) || !ids.Add(combo.id) || !SafeText(combo.title, 200) ||
                    (combo.game != "ats" && combo.game != "ets2") || combo.version == null || !Regex.IsMatch(combo.version, @"^\d{1,2}\.\d{1,3}$") ||
                    !SiteUrl(combo.page) || (combo.orderUrl != null && !SiteUrl(combo.orderUrl)) || combo.mods == null || combo.mods.Length == 0 || combo.mods.Length > 3000)
                    throw new InvalidDataException(L.T("Каталог содержит некорректную сборку."));
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var mod in combo.mods) if (mod == null || !SafeText(mod.key, 255) || mod.key.Contains("|") || !keys.Add(mod.key) || mod.name == null || !SafeText(mod.name.Replace('\t', ' '), 500))
                    throw new InvalidDataException(L.T("Повреждён порядок модов: ") + combo.title + ".");
            }
            return catalog;
        }
        private static bool SafeText(string value, int length) { return !string.IsNullOrWhiteSpace(value) && value.Length <= length && !value.Any(char.IsControl); }
        private static bool SiteUrl(string value) {
            Uri url;
            return Uri.TryCreate(value, UriKind.Absolute, out url) && url.Scheme == "https" && url.Host == "qupersimulator.ru" && url.IsDefaultPort && url.UserInfo.Length == 0;
        }
        public static Catalog Local(out CatalogOrigin source) {
            try {
                if (File.Exists(Cache) && new FileInfo(Cache).Length <= Max) {
                    var cached = Parse(File.ReadAllText(Cache, Sii.Utf8));
                    source = new CatalogOrigin(true, File.GetLastWriteTime(Cache));
                    return cached;
                }
            } catch (Exception) { }
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Quper.Catalog"))
            using (var reader = new StreamReader(stream, Sii.Utf8)) {
                source = new CatalogOrigin(false, null);
                return Parse(reader.ReadToEnd());
            }
        }
        public static Catalog Fetch() {
            var req = (HttpWebRequest)WebRequest.Create(Url);
            req.Timeout = 5000; req.ReadWriteTimeout = 5000; req.AllowAutoRedirect = false;
            req.Accept = "application/json"; req.UserAgent = "QuperSimulatorDesktop/0.1";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.Reload);
            string content;
            using (var response = (HttpWebResponse)req.GetResponse()) {
                if (response.StatusCode != HttpStatusCode.OK || response.ContentLength > Max || !response.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(L.T("Сайт пока не отдаёт каталог приложения."));
                using (var input = response.GetResponseStream())
                using (var output = new MemoryStream()) {
                    var buffer = new byte[8192]; int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0) {
                        if (output.Length + n > Max) throw new InvalidDataException(L.T("Каталог превышает допустимый размер."));
                        output.Write(buffer, 0, n);
                    }
                    content = Sii.Utf8.GetString(output.ToArray()).TrimStart('\ufeff');
                }
            }
            var catalog = Parse(content);
            var temp = Cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(Cache));
                File.WriteAllText(temp, content, Sii.Utf8);
                if (File.Exists(Cache)) File.Replace(temp, Cache, null); else File.Move(temp, Cache);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
            finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            return catalog;
        }
    }
}
