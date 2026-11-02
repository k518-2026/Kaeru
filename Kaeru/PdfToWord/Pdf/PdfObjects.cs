using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PdfToWord.Pdf
{
    /// <summary>PDF の基本オブジェクト。</summary>
    public abstract class PdfObject
    {
    }

    public sealed class PdfNull : PdfObject
    {
        public static readonly PdfNull Instance = new PdfNull();
        public override string ToString() { return "null"; }
    }

    public sealed class PdfBoolean : PdfObject
    {
        public bool Value;
        public PdfBoolean(bool value) { Value = value; }
        public override string ToString() { return Value ? "true" : "false"; }
    }

    public sealed class PdfNumber : PdfObject
    {
        public double Value;
        public PdfNumber(double value) { Value = value; }
        public int AsInt { get { return (int)Math.Round(Value); } }
        public override string ToString()
        {
            return Value.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }

    public sealed class PdfName : PdfObject
    {
        public string Value;
        public PdfName(string value) { Value = value; }
        public override string ToString() { return "/" + Value; }
    }

    /// <summary>文字列オブジェクト。中身はバイト列のまま保持する。</summary>
    public sealed class PdfStringObject : PdfObject
    {
        public byte[] Bytes;
        public PdfStringObject(byte[] bytes) { Bytes = bytes ?? new byte[0]; }

        /// <summary>PDFDocEncoding / UTF-16BE を考慮してテキスト化する。</summary>
        public string AsText()
        {
            if (Bytes.Length >= 2 && Bytes[0] == 0xFE && Bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(Bytes, 2, Bytes.Length - 2);
            var sb = new StringBuilder(Bytes.Length);
            for (int i = 0; i < Bytes.Length; i++) sb.Append((char)Bytes[i]);
            return sb.ToString();
        }

        public override string ToString() { return AsText(); }
    }

    public sealed class PdfArray : PdfObject
    {
        public List<PdfObject> Items = new List<PdfObject>();
        public int Count { get { return Items.Count; } }
        public PdfObject this[int index]
        {
            get { return index >= 0 && index < Items.Count ? Items[index] : null; }
        }
    }

    public sealed class PdfDictionary : PdfObject
    {
        public Dictionary<string, PdfObject> Items =
            new Dictionary<string, PdfObject>(StringComparer.Ordinal);

        public bool Has(string key) { return Items.ContainsKey(key); }

        public PdfObject Get(string key)
        {
            PdfObject value;
            return Items.TryGetValue(key, out value) ? value : null;
        }

        public void Set(string key, PdfObject value) { Items[key] = value; }
    }

    /// <summary>ストリームオブジェクト。復号前の生バイト列を保持する。</summary>
    public sealed class PdfStreamObject : PdfObject
    {
        public PdfDictionary Dictionary = new PdfDictionary();
        public byte[] Raw = new byte[0];
    }

    /// <summary>間接参照（"12 0 R"）。</summary>
    public sealed class PdfReference : PdfObject
    {
        public int Number;
        public int Generation;
        public PdfReference(int number, int generation) { Number = number; Generation = generation; }
        public override string ToString() { return Number + " " + Generation + " R"; }
    }

    /// <summary>演算子など、オブジェクトではないキーワード。コンテンツストリームの解析で使う。</summary>
    public sealed class PdfKeyword : PdfObject
    {
        public string Value;
        public PdfKeyword(string value) { Value = value; }
        public override string ToString() { return Value; }
    }

    /// <summary>バイト列から PDF オブジェクトを読み出す低水準パーサー。</summary>
    public sealed class PdfParser
    {
        private readonly byte[] _data;
        private int _pos;

        /// <summary>ストリーム長など、間接参照を解決するためのコールバック。</summary>
        public Func<PdfObject, PdfObject> Resolver;

        public PdfParser(byte[] data) : this(data, 0) { }

        public PdfParser(byte[] data, int position)
        {
            _data = data ?? new byte[0];
            _pos = position;
        }

        public byte[] Data { get { return _data; } }
        public int Position { get { return _pos; } set { _pos = value; } }
        public bool AtEnd { get { return _pos >= _data.Length; } }

        public static bool IsWhitespace(byte b)
        {
            return b == 0 || b == 9 || b == 10 || b == 12 || b == 13 || b == 32;
        }

        public static bool IsDelimiter(byte b)
        {
            return b == (byte)'(' || b == (byte)')' || b == (byte)'<' || b == (byte)'>'
                || b == (byte)'[' || b == (byte)']' || b == (byte)'{' || b == (byte)'}'
                || b == (byte)'/' || b == (byte)'%';
        }

        public static bool IsRegular(byte b)
        {
            return !IsWhitespace(b) && !IsDelimiter(b);
        }

        public void SkipWhitespace()
        {
            while (_pos < _data.Length)
            {
                byte b = _data[_pos];
                if (IsWhitespace(b))
                {
                    _pos++;
                }
                else if (b == (byte)'%')
                {
                    while (_pos < _data.Length && _data[_pos] != (byte)'\n' && _data[_pos] != (byte)'\r') _pos++;
                }
                else
                {
                    break;
                }
            }
        }

        /// <summary>正規文字の並びをそのまま読む（演算子やキーワードの取得用）。</summary>
        public string ReadToken()
        {
            SkipWhitespace();
            int start = _pos;
            while (_pos < _data.Length && IsRegular(_data[_pos])) _pos++;
            if (_pos == start) return string.Empty;
            return Encoding.ASCII.GetString(_data, start, _pos - start);
        }

        /// <summary>次のオブジェクトを 1 つ読む。読めない場合は null。</summary>
        public PdfObject ParseObject()
        {
            SkipWhitespace();
            if (_pos >= _data.Length) return null;

            byte c = _data[_pos];

            if (c == (byte)'/') return ParseName();
            if (c == (byte)'(') return ParseLiteralString();
            if (c == (byte)'<')
            {
                if (_pos + 1 < _data.Length && _data[_pos + 1] == (byte)'<') return ParseDictionaryOrStream();
                return ParseHexString();
            }
            if (c == (byte)'[')
            {
                _pos++;
                var array = new PdfArray();
                while (true)
                {
                    SkipWhitespace();
                    if (_pos >= _data.Length) break;
                    if (_data[_pos] == (byte)']') { _pos++; break; }
                    int before = _pos;
                    PdfObject item = ParseObject();
                    if (item == null) { if (_pos == before) _pos++; continue; }
                    if (item is PdfKeyword) continue;
                    array.Items.Add(item);
                }
                return array;
            }
            if (c == (byte)']' || c == (byte)'>' || c == (byte)')' || c == (byte)'}')
            {
                _pos++;
                return null;
            }
            if (c == (byte)'{')
            {
                _pos++;
                return null;
            }
            if (c == (byte)'+' || c == (byte)'-' || c == (byte)'.' || (c >= (byte)'0' && c <= (byte)'9'))
                return ParseNumberOrReference();

            string token = ReadToken();
            if (token.Length == 0) { _pos++; return null; }
            if (token == "true") return new PdfBoolean(true);
            if (token == "false") return new PdfBoolean(false);
            if (token == "null") return PdfNull.Instance;
            return new PdfKeyword(token);
        }

        private PdfObject ParseName()
        {
            _pos++; // '/'
            var sb = new StringBuilder();
            while (_pos < _data.Length && IsRegular(_data[_pos]))
            {
                byte b = _data[_pos++];
                if (b == (byte)'#' && _pos + 1 < _data.Length)
                {
                    int hi = HexValue(_data[_pos]);
                    int lo = HexValue(_data[_pos + 1]);
                    if (hi >= 0 && lo >= 0)
                    {
                        sb.Append((char)(hi * 16 + lo));
                        _pos += 2;
                        continue;
                    }
                }
                sb.Append((char)b);
            }
            return new PdfName(sb.ToString());
        }

        private static int HexValue(byte b)
        {
            if (b >= (byte)'0' && b <= (byte)'9') return b - (byte)'0';
            if (b >= (byte)'a' && b <= (byte)'f') return b - (byte)'a' + 10;
            if (b >= (byte)'A' && b <= (byte)'F') return b - (byte)'A' + 10;
            return -1;
        }

        private PdfObject ParseLiteralString()
        {
            _pos++; // '('
            var bytes = new List<byte>();
            int depth = 1;
            while (_pos < _data.Length)
            {
                byte b = _data[_pos++];
                if (b == (byte)'\\')
                {
                    if (_pos >= _data.Length) break;
                    byte e = _data[_pos++];
                    switch ((char)e)
                    {
                        case 'n': bytes.Add((byte)'\n'); break;
                        case 'r': bytes.Add((byte)'\r'); break;
                        case 't': bytes.Add((byte)'\t'); break;
                        case 'b': bytes.Add(8); break;
                        case 'f': bytes.Add(12); break;
                        case '(': bytes.Add((byte)'('); break;
                        case ')': bytes.Add((byte)')'); break;
                        case '\\': bytes.Add((byte)'\\'); break;
                        case '\r':
                            if (_pos < _data.Length && _data[_pos] == (byte)'\n') _pos++;
                            break;
                        case '\n':
                            break;
                        default:
                            if (e >= (byte)'0' && e <= (byte)'7')
                            {
                                int value = e - (byte)'0';
                                for (int k = 0; k < 2 && _pos < _data.Length; k++)
                                {
                                    byte d = _data[_pos];
                                    if (d < (byte)'0' || d > (byte)'7') break;
                                    value = value * 8 + (d - (byte)'0');
                                    _pos++;
                                }
                                bytes.Add((byte)(value & 0xFF));
                            }
                            else
                            {
                                bytes.Add(e);
                            }
                            break;
                    }
                }
                else if (b == (byte)'(')
                {
                    depth++;
                    bytes.Add(b);
                }
                else if (b == (byte)')')
                {
                    depth--;
                    if (depth == 0) break;
                    bytes.Add(b);
                }
                else
                {
                    bytes.Add(b);
                }
            }
            return new PdfStringObject(bytes.ToArray());
        }

        private PdfObject ParseHexString()
        {
            _pos++; // '<'
            var bytes = new List<byte>();
            int high = -1;
            while (_pos < _data.Length)
            {
                byte b = _data[_pos++];
                if (b == (byte)'>') break;
                int v = HexValue(b);
                if (v < 0) continue;
                if (high < 0) high = v;
                else { bytes.Add((byte)(high * 16 + v)); high = -1; }
            }
            if (high >= 0) bytes.Add((byte)(high * 16));
            return new PdfStringObject(bytes.ToArray());
        }

        private PdfObject ParseNumberOrReference()
        {
            int start = _pos;
            double value = ReadNumber();
            bool isInteger = Math.Abs(value - Math.Round(value)) < 1e-9 && value >= 0 && value < int.MaxValue;
            if (isInteger)
            {
                int save = _pos;
                SkipWhitespace();
                if (_pos < _data.Length && _data[_pos] >= (byte)'0' && _data[_pos] <= (byte)'9')
                {
                    double gen = ReadNumber();
                    bool genIsInt = Math.Abs(gen - Math.Round(gen)) < 1e-9 && gen >= 0;
                    if (genIsInt)
                    {
                        int save2 = _pos;
                        string token = ReadToken();
                        if (token == "R")
                            return new PdfReference((int)Math.Round(value), (int)Math.Round(gen));
                        _pos = save2;
                    }
                }
                _pos = save;
            }
            if (_pos == start) _pos++;
            return new PdfNumber(value);
        }

        private double ReadNumber()
        {
            SkipWhitespace();
            int start = _pos;
            if (_pos < _data.Length && (_data[_pos] == (byte)'+' || _data[_pos] == (byte)'-')) _pos++;
            while (_pos < _data.Length)
            {
                byte b = _data[_pos];
                if ((b >= (byte)'0' && b <= (byte)'9') || b == (byte)'.' || b == (byte)'-' || b == (byte)'+' ||
                    b == (byte)'e' || b == (byte)'E')
                    _pos++;
                else break;
            }
            string text = Encoding.ASCII.GetString(_data, start, Math.Max(0, _pos - start));
            double value;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return value;
            // "--5" や "3.4.5" のような壊れた数値への保険
            var sb = new StringBuilder();
            bool dotSeen = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '-' && sb.Length == 0) sb.Append(ch);
                else if (ch == '.' && !dotSeen) { dotSeen = true; sb.Append(ch); }
                else if (ch >= '0' && ch <= '9') sb.Append(ch);
            }
            if (double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return value;
            return 0;
        }

        private PdfObject ParseDictionaryOrStream()
        {
            _pos += 2; // '<<'
            var dict = new PdfDictionary();
            while (true)
            {
                SkipWhitespace();
                if (_pos >= _data.Length) break;
                if (_data[_pos] == (byte)'>')
                {
                    _pos++;
                    if (_pos < _data.Length && _data[_pos] == (byte)'>') _pos++;
                    break;
                }
                if (_data[_pos] != (byte)'/')
                {
                    int before = _pos;
                    PdfObject skipped = ParseObject();
                    if (skipped == null && _pos == before) _pos++;
                    continue;
                }
                var key = ParseName() as PdfName;
                PdfObject value = ParseObject();
                if (key != null && value != null && !(value is PdfKeyword)) dict.Set(key.Value, value);
            }

            int savedPosition = _pos;
            SkipWhitespace();
            if (MatchKeyword("stream"))
            {
                if (_pos < _data.Length && _data[_pos] == (byte)'\r') _pos++;
                if (_pos < _data.Length && _data[_pos] == (byte)'\n') _pos++;
                int dataStart = _pos;
                int length = -1;
                PdfObject lengthObject = dict.Get("Length");
                if (Resolver != null) lengthObject = Resolver(lengthObject);
                var lengthNumber = lengthObject as PdfNumber;
                if (lengthNumber != null) length = lengthNumber.AsInt;

                bool lengthLooksValid = length >= 0 && dataStart + length <= _data.Length;
                if (lengthLooksValid)
                {
                    int check = dataStart + length;
                    var probe = new PdfParser(_data, check);
                    probe.SkipWhitespace();
                    if (!probe.MatchKeyword("endstream")) lengthLooksValid = false;
                }
                if (!lengthLooksValid)
                {
                    int found = IndexOf(_data, Encoding.ASCII.GetBytes("endstream"), dataStart);
                    if (found < 0) found = _data.Length;
                    int end = found;
                    if (end > dataStart && _data[end - 1] == (byte)'\n') end--;
                    if (end > dataStart && _data[end - 1] == (byte)'\r') end--;
                    length = Math.Max(0, end - dataStart);
                }

                var stream = new PdfStreamObject();
                stream.Dictionary = dict;
                stream.Raw = new byte[length];
                Buffer.BlockCopy(_data, dataStart, stream.Raw, 0, length);
                _pos = Math.Min(_data.Length, dataStart + length);
                SkipWhitespace();
                MatchKeyword("endstream");
                return stream;
            }
            _pos = savedPosition;
            return dict;
        }

        /// <summary>現在位置がキーワードと一致すれば読み進めて true を返す。</summary>
        public bool MatchKeyword(string keyword)
        {
            SkipWhitespace();
            if (_pos + keyword.Length > _data.Length) return false;
            for (int i = 0; i < keyword.Length; i++)
            {
                if (_data[_pos + i] != (byte)keyword[i]) return false;
            }
            int after = _pos + keyword.Length;
            if (after < _data.Length && IsRegular(_data[after])) return false;
            _pos = after;
            return true;
        }

        public static int IndexOf(byte[] haystack, byte[] needle, int startIndex)
        {
            if (needle.Length == 0) return -1;
            int limit = haystack.Length - needle.Length;
            for (int i = Math.Max(0, startIndex); i <= limit; i++)
            {
                if (haystack[i] != needle[0]) continue;
                int k = 1;
                while (k < needle.Length && haystack[i + k] == needle[k]) k++;
                if (k == needle.Length) return i;
            }
            return -1;
        }
    }
}
