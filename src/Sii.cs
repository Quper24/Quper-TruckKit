using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace QuperDesk {
    internal sealed class Unit {
        public string Type, Id;
        public readonly List<KeyValuePair<string, string>> Fields = new List<KeyValuePair<string, string>>();
        public string Get(string key) { return Fields.FirstOrDefault(f => f.Key == key).Value; }
        public void Add(string key, string value) { Fields.Add(new KeyValuePair<string, string>(key, value)); }
        public void Set(string key, string value) {
            var i = Fields.FindIndex(f => f.Key == key);
            if (i < 0) Add(key, value); else Fields[i] = new KeyValuePair<string, string>(key, value);
        }
        public List<string> Array(string key) {
            int count;
            if (!int.TryParse(Get(key), out count) || count < 0 || count > Sii.MaxItems)
                throw new InvalidDataException(L.T("Неверный список «") + key + "».");
            var rows = new SortedDictionary<int, string>();
            var prefix = key + "[";
            foreach (var f in Fields.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal))) {
                var raw = f.Key.Substring(prefix.Length, f.Key.Length - prefix.Length - 1);
                int index;
                if (raw.Length == 0) index = rows.Count;
                else if (!int.TryParse(raw, out index)) throw new InvalidDataException(L.T("Неверный индекс «") + key + "».");
                if (index < 0 || index >= count || rows.ContainsKey(index)) throw new InvalidDataException(L.T("Неверный индекс «") + key + "».");
                rows.Add(index, f.Value);
            }
            if (rows.Count != count) throw new InvalidDataException(L.T("Неполный список «") + key + "».");
            return rows.Values.ToList();
        }
        public void SetArray(string key, IList<string> values) {
            var i = Fields.FindIndex(f => f.Key == key || f.Key.StartsWith(key + "[", StringComparison.Ordinal));
            if (i < 0) i = Fields.Count;
            Fields.RemoveAll(f => f.Key == key || f.Key.StartsWith(key + "[", StringComparison.Ordinal));
            Fields.Insert(i++, new KeyValuePair<string, string>(key, Sii.Num(values.Count)));
            for (var n = 0; n < values.Count; n++) Fields.Insert(i++, new KeyValuePair<string, string>(key + "[" + Sii.Num(n) + "]", values[n]));
        }
    }

    internal sealed class Sii {
        public const int MaxItems = 1000000;
        public const int MaxBytes = 96 * 1024 * 1024;
        public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly Regex UnitLine = new Regex(@"^([\w]+)\s*:\s*([.\w]+)\s*\{\s*$");
        private static readonly Regex FieldLine = new Regex(@"^([\w]+(?:\[\d*\])?)\s*:\s*(.*)$");
        private static readonly Regex SimpleToken = new Regex(@"^[A-Za-z0-9_]+$");
        public readonly List<Unit> Units = new List<Unit>();
        public readonly Dictionary<string, Unit> ById = new Dictionary<string, Unit>(StringComparer.Ordinal);

        public static string Num(long value) { return value.ToString(Invariant); }
        public static Sii Load(string path) {
            var info = new FileInfo(path);
            if (info.Length > MaxBytes) throw new InvalidDataException(L.T("Файл сохранения слишком большой."));
            return Read(File.ReadAllBytes(path));
        }
        public static Sii Read(byte[] data) {
            for (var depth = 0; depth < 3; depth++) {
                var magic = data.Length >= 4 ? Encoding.ASCII.GetString(data, 0, 4) : "";
                if (magic == "ScsC") data = Unwrap(data);
                else if (magic == "3nK\x01" && data.Length >= 6) {
                    var payload = new byte[data.Length - 6];
                    for (var i = 0; i < payload.Length; i++) {
                        var index = (data[5] + i) & 255;
                        payload[i] = (byte)(data[6 + i] ^ ((((index << 2) ^ ~index) << 3) ^ index));
                    }
                    data = payload;
                } else if (magic == "BSII") return new BinarySii(data).Decode();
                else return FromText(Utf8.GetString(data));
            }
            throw new InvalidDataException(L.T("Неподдерживаемая вложенность SII."));
        }
        private static byte[] Unwrap(byte[] data) {
            if (data.Length < 72 || (data.Length - 56) % 16 != 0) throw new InvalidDataException(L.T("Повреждён заголовок ScsC."));
            var size = BitConverter.ToInt32(data, 52);
            if (size < 4 || size > MaxBytes) throw new InvalidDataException(L.T("Некорректный размер ScsC."));
            byte[] compressed;
            using (var aes = Aes.Create()) {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                aes.Key = new byte[] { 0x2a,0x5f,0xcb,0x17,0x91,0xd2,0x2f,0xb6,0x02,0x45,0xb3,0xd8,0x36,0x9e,0xd0,0xb2,
                    0xc2,0x73,0x71,0x56,0x3f,0xbf,0x1f,0x3c,0x9e,0xdf,0x6b,0x11,0x82,0x5a,0x5d,0x0a };
                aes.IV = data.Skip(36).Take(16).ToArray();
                using (var decrypt = aes.CreateDecryptor()) compressed = decrypt.TransformFinalBlock(data, 56, data.Length - 56);
            }
            if ((compressed[0] & 15) != 8 || (compressed[1] & 32) != 0 || ((compressed[0] << 8) + compressed[1]) % 31 != 0)
                throw new InvalidDataException(L.T("Повреждён поток ScsC."));
            using (var input = new MemoryStream(compressed, 2, compressed.Length - 2))
            using (var inflate = new DeflateStream(input, CompressionMode.Decompress)) {
                var payload = new byte[size];
                int offset = 0, n;
                while (offset < size && (n = inflate.Read(payload, offset, size - offset)) != 0) offset += n;
                if (offset != size || inflate.ReadByte() != -1) throw new InvalidDataException(L.T("Размер распакованного SII не совпадает."));
                return payload;
            }
        }
        public void Add(Unit unit) {
            if (ById.ContainsKey(unit.Id)) throw new InvalidDataException(L.T("Повторяющийся блок SII: ") + unit.Id);
            ById.Add(unit.Id, unit);
            Units.Add(unit);
        }
        public Unit One(string type) {
            var hits = Units.Where(u => u.Type == type).Take(2).ToArray();
            if (hits.Length != 1) throw new InvalidDataException(L.T("Не удалось однозначно найти блок «") + type + "».");
            return hits[0];
        }
        public static Sii FromText(string text) {
            using (var reader = new StringReader(text)) {
                var doc = new Sii();
                var header = false; var opened = false; var closed = false;
                Unit current = null;
                string line; int lineNo = 0;
                while ((line = reader.ReadLine()) != null) {
                    lineNo++;
                    line = line.Trim().TrimStart('\ufeff');
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                    if (!header && (line == "SiiNunit" || line == "SiiNunit {")) { header = true; opened = line.EndsWith("{"); continue; }
                    if (header && !opened && line == "{") { opened = true; continue; }
                    if (opened && !closed) {
                        if (line == "}") {
                            if (current == null) closed = true;
                            else { doc.Add(current); current = null; }
                            continue;
                        }
                        if (current == null) {
                            var unit = UnitLine.Match(line);
                            if (unit.Success) { current = new Unit { Type = unit.Groups[1].Value, Id = unit.Groups[2].Value }; continue; }
                        } else {
                            var field = FieldLine.Match(line);
                            if (field.Success) { current.Add(field.Groups[1].Value, field.Groups[2].Value); continue; }
                        }
                    }
                    throw new InvalidDataException(L.T("Не распознана строка SII № ") + lineNo + ".");
                }
                if (!closed || doc.Units.Count == 0) throw new InvalidDataException(L.T("Файл SII пуст или обрезан."));
                return doc;
            }
        }
        public string ToText() {
            var text = new StringBuilder("SiiNunit\n{\n");
            foreach (var unit in Units) {
                text.Append(unit.Type).Append(" : ").Append(unit.Id).Append(" {\n");
                foreach (var field in unit.Fields) text.Append(' ').Append(field.Key).Append(": ").Append(field.Value).Append('\n');
                text.Append("}\n\n");
            }
            return text.Append("}\n").ToString();
        }
        public static string Quote(string value) {
            var result = new StringBuilder("\"");
            foreach (var b in Utf8.GetBytes(value)) {
                if (b < 32 || b > 126 || b == 34 || b == 92) result.Append("\\x").Append(b.ToString("x2", Invariant));
                else result.Append((char)b);
            }
            return result.Append('"').ToString();
        }
        public static string Unquote(string value) {
            if (string.IsNullOrEmpty(value) || value[0] != '"') return value;
            if (value.Length < 2 || value[value.Length - 1] != '"') throw new InvalidDataException("Незакрытая строка SII.");
            using (var ms = new MemoryStream()) {
                for (var i = 1; i < value.Length - 1;) {
                    if (value[i] != '\\') {
                        var end = value.IndexOf('\\', i);
                        if (end < 0) end = value.Length - 1;
                        var bytes = Utf8.GetBytes(value.Substring(i, end - i));
                        ms.Write(bytes, 0, bytes.Length);
                        i = end;
                    } else if (i + 3 < value.Length - 1 && value[i + 1] == 'x') {
                        byte b;
                        if (!byte.TryParse(value.Substring(i + 2, 2), NumberStyles.HexNumber, Invariant, out b))
                            throw new InvalidDataException(L.T("Неверный символ SII."));
                        ms.WriteByte(b); i += 4;
                    } else if (i + 1 < value.Length - 1 && (value[i + 1] == '"' || value[i + 1] == '\\')) {
                        ms.WriteByte((byte)value[i + 1]); i += 2;
                    } else { ms.WriteByte(92); i++; }
                }
                return Utf8.GetString(ms.ToArray());
            }
        }
        private static string StringToken(string value) { return SimpleToken.IsMatch(value) ? value : Quote(value); }

        private sealed class BinarySii {
            private sealed class Spec {
                public string Name;
                public uint Type;
                public Dictionary<uint, string> Enum;
            }
            private sealed class Shape {
                public string Name;
                public readonly List<Spec> Specs = new List<Spec>();
            }
            private static readonly HashSet<uint> Arrays = new HashSet<uint> {
                2,4,6,8,10,0x12,0x18,0x1a,0x26,0x28,0x2a,0x2c,0x32,0x34,0x36,0x38,0x3a,0x3c,0x3e
            };
            private readonly byte[] bytes;
            private int p = 4;
            private uint version;
            public BinarySii(byte[] bytes) { this.bytes = bytes; }
            private int Consume(int n) {
                if (n < 0 || n > bytes.Length - p) throw new EndOfStreamException(L.T("Двоичный SII обрезан."));
                var start = p; p += n; return start;
            }
            private byte Byte() { return bytes[Consume(1)]; }
            private uint U32() { return BitConverter.ToUInt32(bytes, Consume(4)); }
            private ulong U64() { return BitConverter.ToUInt64(bytes, Consume(8)); }
            private int Count(int max) {
                var n = U32();
                if (n > max) throw new InvalidDataException(L.T("Слишком большой блок SII."));
                return (int)n;
            }
            private string Str() { var n = Count(MaxBytes); return Utf8.GetString(bytes, Consume(n), n); }
            private static string Encoded(ulong n) {
                const string alphabet = " 0123456789abcdefghijklmnopqrstuvwxyz_";
                n &= 0x7fffffffffffffffUL;
                var b = new StringBuilder(12);
                while (n != 0) { b.Append(alphabet[(int)(n % 38)]); n /= 38; }
                return b.ToString();
            }
            private string Id() {
                var parts = Byte();
                if (parts == 0) return "null";
                if (parts == 255) {
                    var hex = U64().ToString("x16", Invariant);
                    var id = string.Join(".", Enumerable.Range(0, 4).Select(i => hex.Substring(i * 4, 4)).SkipWhile(s => s == "0000").ToArray()).TrimStart('0');
                    return "_nameless." + (id.Length == 0 ? "0" : id);
                }
                var list = new string[parts];
                for (var i = 0; i < parts; i++) list[i] = Encoded(U64());
                return string.Join(".", list);
            }
            private static string Float(uint bits) { return "&" + bits.ToString("x8", Invariant); }
            private string Float() { return Float(U32()); }
            private string Vector(int count, bool quaternion) {
                var v = new string[count];
                for (var i = 0; i < count; i++) v[i] = Float();
                return quaternion ? "(" + v[0] + "; " + string.Join(", ", v.Skip(1)) + ")" : "(" + string.Join(", ", v) + ")";
            }
            private string Placement() {
                if (version == 1) return Vector(3, false) + " " + Vector(4, true);
                var x = U32(); var y = U32(); var z = U32();
                var bias = (int)BitConverter.ToSingle(bytes, Consume(4));
                var dx = ((bias & 4095) - 2048) << 9;
                var dz = (((bias >> 12) & 4095) - 2048) << 9;
                if (dx != 0) x = BitConverter.ToUInt32(BitConverter.GetBytes(BitConverter.ToSingle(BitConverter.GetBytes(x), 0) + dx), 0);
                if (dz != 0) z = BitConverter.ToUInt32(BitConverter.GetBytes(BitConverter.ToSingle(BitConverter.GetBytes(z), 0) + dz), 0);
                return "(" + Float(x) + ", " + Float(y) + ", " + Float(z) + ") " + Vector(4, true);
            }
            private static string Unsigned(ulong v, ulong max) { return v == max ? "nil" : v.ToString(Invariant); }
            private string Value(uint type, Spec spec) {
                switch (type) {
                    case 1: return StringToken(Str());
                    case 3: return StringToken(Encoded(U64()));
                    case 5: return Float();
                    case 7: return Vector(2, false);
                    case 9: return Vector(3, false);
                    case 0x11: return "(" + Num(unchecked((int)U32())) + ", " + Num(unchecked((int)U32())) + ", " + Num(unchecked((int)U32())) + ")";
                    case 0x17: return Vector(4, true);
                    case 0x19: return Placement();
                    case 0x25: return Num(unchecked((int)U32()));
                    case 0x27: case 0x2f: return Unsigned(U32(), uint.MaxValue);
                    case 0x29: return Num(BitConverter.ToInt16(bytes, Consume(2)));
                    case 0x2b: return Unsigned(BitConverter.ToUInt16(bytes, Consume(2)), ushort.MaxValue);
                    case 0x31: return Num(unchecked((long)U64()));
                    case 0x33: return Unsigned(U64(), ulong.MaxValue);
                    case 0x35: return Byte() == 0 ? "false" : "true";
                    case 0x37:
                        string name;
                        if (spec.Enum.TryGetValue(U32(), out name)) return StringToken(name);
                        throw new InvalidDataException(L.T("Неизвестное значение SII: ") + spec.Name);
                    case 0x39: case 0x3b: case 0x3d: return Id();
                    default: throw new InvalidDataException(L.T("Неподдерживаемое поле BSII 0x") + type.ToString("x") + ": " + spec.Name);
                }
            }
            public Sii Decode() {
                version = U32();
                if (version < 1 || version > 3) throw new InvalidDataException(L.T("Неподдерживаемая версия BSII: ") + version);
                var shapes = new Dictionary<uint, Shape>(); var doc = new Sii();
                while (true) {
                    var type = U32();
                    if (type == 0) {
                        if (Byte() == 0) break;
                        var index = U32(); var shape = new Shape { Name = Str() };
                        while ((type = U32()) != 0) {
                            var spec = new Spec { Name = Str(), Type = type };
                            if (type == 0x37 || type == 0x38) {
                                var count = Count(65536); spec.Enum = new Dictionary<uint, string>();
                                for (var i = 0; i < count; i++) spec.Enum.Add(U32(), Str());
                            }
                            shape.Specs.Add(spec);
                        }
                        shapes.Add(index, shape);
                    } else {
                        Shape shape;
                        if (!shapes.TryGetValue(type, out shape)) throw new InvalidDataException(L.T("Неизвестный блок BSII."));
                        var unit = new Unit { Type = shape.Name, Id = Id() };
                        foreach (var spec in shape.Specs) {
                            if (!Arrays.Contains(spec.Type)) unit.Add(spec.Name, Value(spec.Type, spec));
                            else {
                                var count = Count(MaxItems);
                                unit.Add(spec.Name, Num(count));
                                for (var i = 0; i < count; i++) unit.Add(spec.Name + "[" + Num(i) + "]", Value(spec.Type - 1, spec));
                            }
                        }
                        doc.Add(unit);
                    }
                }
                if (doc.Units.Count == 0 || p != bytes.Length) throw new InvalidDataException(L.T("Некорректный конец BSII."));
                return doc;
            }
        }
    }
}
