using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace QuperDesk {
    internal enum ModStatus { Found, Missing, Outdated, Different }

    internal sealed class ModCheck {
        public readonly Mod Mod;
        public readonly ModStatus Status;
        public readonly string Installed;

        public ModCheck(Mod mod, ModStatus status, string installed = null) {
            Mod = mod;
            Status = status;
            Installed = installed;
        }
    }

    internal sealed class ModInventory {
        private static readonly Regex MarkedVersion = new Regex(@"^(?<family>.+?)[ ._-]+v(?<version>[0-9]+(?:[._][0-9]+)*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex NumberedVersion = new Regex(@"^(?<family>.+?)[ ._-]+(?<version>[0-9]+(?:[._][0-9]+)+)$", RegexOptions.CultureInvariant);
        private readonly HashSet<string> installed;
        private readonly Dictionary<string, List<VersionedName>> families = new Dictionary<string, List<VersionedName>>(StringComparer.OrdinalIgnoreCase);

        public ModInventory(IEnumerable<string> installedNames) {
            installed = new HashSet<string>(installedNames.Select(Stem), StringComparer.OrdinalIgnoreCase);
            foreach (var name in installedNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)) {
                var candidate = VersionedName.Parse(name);
                if (candidate == null) continue;
                List<VersionedName> family;
                if (!families.TryGetValue(candidate.Family, out family)) {
                    family = new List<VersionedName>();
                    families.Add(candidate.Family, family);
                }
                family.Add(candidate);
            }
        }

        public ModCheck Inspect(Mod mod) {
            var expectedNames = new[] { mod.filename, mod.key }.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
            foreach (var name in expectedNames)
                if (installed.Contains(Stem(name))) return new ModCheck(mod, ModStatus.Found);

            foreach (var name in expectedNames) {
                var expected = VersionedName.Parse(name);
                List<VersionedName> family;
                if (expected == null || !families.TryGetValue(expected.Family, out family)) continue;
                var compatible = family.Where(candidate => candidate.Numbers.Length == expected.Numbers.Length).ToList();
                if (compatible.Count == 0) continue;
                var latest = compatible.Aggregate((left, right) => left.CompareTo(right) >= 0 ? left : right);
                return new ModCheck(mod, latest.CompareTo(expected) < 0 ? ModStatus.Outdated : ModStatus.Different, latest.Name);
            }
            return new ModCheck(mod, ModStatus.Missing);
        }

        private static string Stem(string name) {
            if (name.EndsWith(".scs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - 4);
            return name;
        }

        private sealed class VersionedName {
            public string Name, Family;
            public int[] Numbers;

            public static VersionedName Parse(string name) {
                var stem = Stem(name);
                var match = MarkedVersion.Match(stem);
                if (!match.Success) match = NumberedVersion.Match(stem);
                if (!match.Success) return null;
                var parts = match.Groups["version"].Value.Split('.', '_');
                var numbers = new int[parts.Length];
                for (var i = 0; i < parts.Length; i++)
                    if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
                return new VersionedName { Name = name, Family = match.Groups["family"].Value, Numbers = numbers };
            }

            public int CompareTo(VersionedName other) {
                for (var i = 0; i < Numbers.Length; i++) {
                    var result = Numbers[i].CompareTo(other.Numbers[i]);
                    if (result != 0) return result;
                }
                return 0;
            }
        }
    }
}
