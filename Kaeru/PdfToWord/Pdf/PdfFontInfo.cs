using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PdfToWord.Pdf
{
    /// <summary>文字コード → Unicode の対応表（CMap）。</summary>
    public sealed class CMapTable
    {
        private sealed class Range
        {
            public int Low;
            public int High;
            public string Destination;
            public List<string> Items;
        }

        private readonly Dictionary<int, string> _singles = new Dictionary<int, string>();
        private readonly List<Range> _ranges = new List<Range>();

        public bool IsEmpty { get { return _singles.Count == 0 && _ranges.Count == 0; } }

        public void AddSingle(int code, string value) { _singles[code] = value; }

        public void AddRange(int low, int high, string destinationStart)
        {
            if (high < low) return;
            _ranges.Add(new Range { Low = low, High = high, Destination = destinationStart });
        }

        public void AddRange(int low, int high, List<string> items)
        {
            if (high < low) return;
            _ranges.Add(new Range { Low = low, High = high, Items = items });
        }

        public string Lookup(int code)
        {
            string value;
            if (_singles.TryGetValue(code, out value)) return value;
            for (int i = _ranges.Count - 1; i >= 0; i--)
            {
                Range range = _ranges[i];
                if (code < range.Low || code > range.High) continue;
                int offset = code - range.Low;
                if (range.Items != null)
                    return offset < range.Items.Count ? range.Items[offset] : null;
                if (string.IsNullOrEmpty(range.Destination)) return null;
                var chars = range.Destination.ToCharArray();
                chars[chars.Length - 1] = (char)(chars[chars.Length - 1] + offset);
                return new string(chars);
            }
            return null;
        }
    }

    /// <summary>PDF のフォント辞書 1 つ分の情報。</summary>
    public sealed class PdfFontInfo
    {
        public string BaseFont = "";
        public bool IsBold;
        public bool IsItalic;
        public bool IsFixedPitch;
        public bool IsSerif;
        public bool IsTwoByte;
        public bool IsShiftJisEncoding;

        private CMapTable _toUnicode;
        private string[] _simpleEncoding;          // 単純フォント: コード → Unicode 文字列
        private Dictionary<int, double> _widths = new Dictionary<int, double>();
        private double _defaultWidth = 500;
        private CMapTable _codeToCid;              // 埋め込み CMap 用
        private bool _identityCid = true;
        private bool _ucs2Encoding;

        public static PdfFontInfo Load(PdfDictionary fontDictionary, PdfFile file)
        {
            var font = new PdfFontInfo();
            try { font.Initialize(fontDictionary, file); }
            catch (Exception) { }
            return font;
        }

        private void Initialize(PdfDictionary dictionary, PdfFile file)
        {
            if (dictionary == null)
            {
                _simpleEncoding = PdfEncodings.GetEncoding("WinAnsiEncoding", "");
                return;
            }

            string subtype = file.GetName(dictionary, "Subtype") ?? "";
            BaseFont = file.GetName(dictionary, "BaseFont") ?? "";
            if (BaseFont.Length > 7 && BaseFont[6] == '+') BaseFont = BaseFont.Substring(7);

            string lowerName = BaseFont.ToLowerInvariant();
            IsBold = lowerName.Contains("bold") || lowerName.Contains("black") || lowerName.Contains("heavy")
                     || lowerName.Contains("semib") || lowerName.EndsWith("-b", StringComparison.Ordinal);
            IsItalic = lowerName.Contains("italic") || lowerName.Contains("oblique");

            PdfDictionary descendant = null;
            if (subtype == "Type0")
            {
                IsTwoByte = true;
                var descendants = file.GetArray(dictionary, "DescendantFonts");
                if (descendants != null && descendants.Count > 0)
                    descendant = file.Resolve(descendants[0]) as PdfDictionary;

                PdfObject encoding = file.Resolve(dictionary.Get("Encoding"));
                var encodingName = encoding as PdfName;
                if (encodingName != null)
                {
                    string name = encodingName.Value;
                    if (name.Contains("RKSJ")) { IsShiftJisEncoding = true; IsTwoByte = false; }
                    if (name.Contains("UCS2") || name.Contains("UTF16")) _ucs2Encoding = true;
                    _identityCid = name.StartsWith("Identity", StringComparison.Ordinal);
                }
                else
                {
                    var encodingStream = encoding as PdfStreamObject;
                    if (encodingStream != null)
                    {
                        byte[] content = PdfFilters.Decode(encodingStream, file);
                        _codeToCid = ParseCidCMap(content);
                        _identityCid = false;
                    }
                }
            }

            PdfDictionary descriptorOwner = descendant ?? dictionary;
            var descriptor = file.GetDictionary(descriptorOwner, "FontDescriptor");
            if (descriptor != null)
            {
                int flags = file.GetInt(descriptor, "Flags", 0);
                IsFixedPitch = (flags & 1) != 0;
                IsSerif = (flags & 2) != 0;
                if ((flags & (1 << 18)) != 0) IsBold = true;
                if ((flags & (1 << 6)) != 0) IsItalic = true;
                double italicAngle = file.GetDouble(descriptor, "ItalicAngle", 0);
                if (Math.Abs(italicAngle) > 4) IsItalic = true;
                double stemV = file.GetDouble(descriptor, "StemV", 0);
                if (stemV >= 120) IsBold = true;
                double missingWidth = file.GetDouble(descriptor, "MissingWidth", 0);
                if (missingWidth > 0) _defaultWidth = missingWidth;
            }

            var toUnicodeStream = file.Resolve(dictionary.Get("ToUnicode")) as PdfStreamObject;
            if (toUnicodeStream != null)
            {
                byte[] content = PdfFilters.Decode(toUnicodeStream, file);
                _toUnicode = ParseToUnicode(content);
                if (_toUnicode != null && _toUnicode.IsEmpty) _toUnicode = null;
            }

            if (descendant != null)
            {
                _defaultWidth = file.GetDouble(descendant, "DW", 1000);
                LoadCidWidths(file.GetArray(descendant, "W"), file);
            }
            else
            {
                LoadSimpleEncoding(dictionary, file, subtype);
                LoadSimpleWidths(dictionary, file);
            }
        }

        private void LoadSimpleWidths(PdfDictionary dictionary, PdfFile file)
        {
            int firstChar = file.GetInt(dictionary, "FirstChar", 0);
            var widths = file.GetArray(dictionary, "Widths");
            if (widths == null)
            {
                _defaultWidth = IsFixedPitch ? 600 : 500;
                return;
            }
            for (int i = 0; i < widths.Count; i++)
            {
                var number = file.Resolve(widths[i]) as PdfNumber;
                if (number == null) continue;
                _widths[firstChar + i] = number.Value;
            }
        }

        private void LoadCidWidths(PdfArray widthArray, PdfFile file)
        {
            if (widthArray == null) return;
            int index = 0;
            while (index < widthArray.Count)
            {
                var first = file.Resolve(widthArray[index]) as PdfNumber;
                if (first == null) break;
                if (index + 1 >= widthArray.Count) break;
                PdfObject next = file.Resolve(widthArray[index + 1]);
                var list = next as PdfArray;
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var number = file.Resolve(list[i]) as PdfNumber;
                        if (number != null) _widths[first.AsInt + i] = number.Value;
                    }
                    index += 2;
                }
                else
                {
                    var last = next as PdfNumber;
                    if (last == null || index + 2 >= widthArray.Count) break;
                    var value = file.Resolve(widthArray[index + 2]) as PdfNumber;
                    if (value == null) break;
                    int low = first.AsInt;
                    int high = last.AsInt;
                    if (high - low <= 65535)
                    {
                        for (int c = low; c <= high; c++) _widths[c] = value.Value;
                    }
                    index += 3;
                }
            }
        }

        private void LoadSimpleEncoding(PdfDictionary dictionary, PdfFile file, string subtype)
        {
            var table = new string[256];
            string baseEncoding = null;
            PdfObject encoding = file.Resolve(dictionary.Get("Encoding"));
            var encodingName = encoding as PdfName;
            var encodingDictionary = encoding as PdfDictionary;
            if (encodingName != null) baseEncoding = encodingName.Value;
            else if (encodingDictionary != null) baseEncoding = file.GetName(encodingDictionary, "BaseEncoding");

            string[] baseTable = PdfEncodings.GetEncoding(baseEncoding, subtype);
            for (int i = 0; i < 256; i++) table[i] = baseTable[i];

            if (encodingDictionary != null)
            {
                var differences = file.GetArray(encodingDictionary, "Differences");
                if (differences != null)
                {
                    int code = 0;
                    for (int i = 0; i < differences.Count; i++)
                    {
                        PdfObject item = file.Resolve(differences[i]);
                        var number = item as PdfNumber;
                        if (number != null) { code = number.AsInt; continue; }
                        var name = item as PdfName;
                        if (name != null && code >= 0 && code < 256)
                        {
                            table[code] = PdfEncodings.GlyphToUnicode(name.Value);
                            code++;
                        }
                    }
                }
            }
            _simpleEncoding = table;
        }

        /// <summary>バイト列を「コードの並び」に分解する。</summary>
        public List<int> SplitCodes(byte[] bytes, out List<int> byteLengths)
        {
            var codes = new List<int>();
            byteLengths = new List<int>();
            int i = 0;
            while (i < bytes.Length)
            {
                if (IsTwoByte)
                {
                    int code = bytes[i] << 8;
                    if (i + 1 < bytes.Length) code |= bytes[i + 1];
                    codes.Add(code);
                    byteLengths.Add(2);
                    i += 2;
                }
                else if (IsShiftJisEncoding)
                {
                    byte b = bytes[i];
                    bool lead = (b >= 0x81 && b <= 0x9F) || (b >= 0xE0 && b <= 0xFC);
                    if (lead && i + 1 < bytes.Length)
                    {
                        codes.Add((b << 8) | bytes[i + 1]);
                        byteLengths.Add(2);
                        i += 2;
                    }
                    else
                    {
                        codes.Add(b);
                        byteLengths.Add(1);
                        i++;
                    }
                }
                else
                {
                    codes.Add(bytes[i]);
                    byteLengths.Add(1);
                    i++;
                }
            }
            return codes;
        }

        public int ToCid(int code)
        {
            if (_codeToCid != null)
            {
                string mapped = _codeToCid.Lookup(code);
                if (mapped != null && mapped.Length > 0) return mapped[0];
            }
            return code;
        }

        /// <summary>グリフ幅（1/1000 em 単位）。</summary>
        public double GetWidth(int code)
        {
            double width;
            int key = (_simpleEncoding != null) ? code : ToCid(code);
            if (_widths.TryGetValue(key, out width)) return width;
            if (_simpleEncoding != null && _widths.Count > 0) return _defaultWidth;
            return _defaultWidth;
        }

        /// <summary>コードを Unicode 文字列に変換する。対応が取れない場合は null。</summary>
        public string ToUnicode(int code)
        {
            if (_toUnicode != null)
            {
                string mapped = _toUnicode.Lookup(code);
                if (!string.IsNullOrEmpty(mapped))
                {
                    if (mapped.Length == 1 && mapped[0] == 0) return null;
                    return NormalizePrivateUse(mapped);
                }
            }
            if (_simpleEncoding != null)
            {
                if (code >= 0 && code < 256)
                {
                    string value = _simpleEncoding[code];
                    if (!string.IsNullOrEmpty(value)) return NormalizePrivateUse(value);
                }
                return null;
            }
            if (_ucs2Encoding && code > 0) return char.ConvertFromUtf32(Math.Min(code, 0xFFFF));
            if (IsShiftJisEncoding) return ShiftJisToUnicode(code);
            return null;
        }

        /// <summary>Symbol / Wingdings の私用領域の文字を、実際の記号に置き換える。</summary>
        public static string NormalizePrivateUse(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            bool needsWork = false;
            foreach (char c in value)
            {
                if (c >= 0xF000 && c <= 0xF0FF) { needsWork = true; break; }
            }
            if (!needsWork) return value;

            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (c < 0xF000 || c > 0xF0FF) { sb.Append(c); continue; }
                int ascii = c - 0xF000;
                switch (ascii)
                {
                    case 0xB7: sb.Append('\u2022'); break;  // 中黒（Symbol）
                    case 0x6C: sb.Append('\u2022'); break;  // Wingdings の l
                    case 0xA7: sb.Append('\u25AA'); break;
                    case 0xA8: sb.Append('\u25C6'); break;
                    case 0xD8: sb.Append('\u27A2'); break;
                    case 0xFC: sb.Append('\u2713'); break;
                    case 0xFE: sb.Append('\u25A0'); break;
                    default:
                        // 英字は化けやすいので落とし、記号と数字だけ残す
                        if (ascii >= 0x20 && ascii < 0x7F && !char.IsLetter((char)ascii)) sb.Append((char)ascii);
                        break;
                }
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        private static string ShiftJisToUnicode(int code)
        {
            if (code < 0x80) return ((char)code).ToString();
            return null;
        }

        /// <summary>ToUnicode CMap を解析する。</summary>
        public static CMapTable ParseToUnicode(byte[] content)
        {
            var map = new CMapTable();
            var parser = new PdfParser(content);
            var stack = new List<PdfObject>();

            while (true)
            {
                PdfObject item;
                try { item = parser.ParseObject(); }
                catch (Exception) { break; }
                if (item == null)
                {
                    if (parser.AtEnd) break;
                    continue;
                }
                var keyword = item as PdfKeyword;
                if (keyword == null)
                {
                    stack.Add(item);
                    if (stack.Count > 600) stack.RemoveRange(0, 300);
                    continue;
                }

                if (keyword.Value == "endbfchar")
                {
                    for (int i = 0; i + 1 < stack.Count; i += 2)
                    {
                        var source = stack[i] as PdfStringObject;
                        var target = stack[i + 1] as PdfStringObject;
                        if (source == null || target == null) continue;
                        map.AddSingle(ToCode(source.Bytes), Utf16BeToString(target.Bytes));
                    }
                    stack.Clear();
                }
                else if (keyword.Value == "endbfrange")
                {
                    for (int i = 0; i + 2 < stack.Count; i += 3)
                    {
                        var low = stack[i] as PdfStringObject;
                        var high = stack[i + 1] as PdfStringObject;
                        if (low == null || high == null) continue;
                        int lowCode = ToCode(low.Bytes);
                        int highCode = ToCode(high.Bytes);
                        var destination = stack[i + 2] as PdfStringObject;
                        if (destination != null)
                        {
                            map.AddRange(lowCode, highCode, Utf16BeToString(destination.Bytes));
                            continue;
                        }
                        var list = stack[i + 2] as PdfArray;
                        if (list != null)
                        {
                            var items = new List<string>();
                            for (int k = 0; k < list.Count; k++)
                            {
                                var entry = list[k] as PdfStringObject;
                                items.Add(entry != null ? Utf16BeToString(entry.Bytes) : null);
                            }
                            map.AddRange(lowCode, highCode, items);
                        }
                    }
                    stack.Clear();
                }
                else if (keyword.Value == "beginbfchar" || keyword.Value == "beginbfrange"
                         || keyword.Value == "begincodespacerange" || keyword.Value == "endcodespacerange")
                {
                    stack.Clear();
                }
            }
            return map;
        }

        /// <summary>埋め込み CMap（コード → CID）を解析する。</summary>
        public static CMapTable ParseCidCMap(byte[] content)
        {
            var map = new CMapTable();
            var parser = new PdfParser(content);
            var stack = new List<PdfObject>();
            while (true)
            {
                PdfObject item;
                try { item = parser.ParseObject(); }
                catch (Exception) { break; }
                if (item == null)
                {
                    if (parser.AtEnd) break;
                    continue;
                }
                var keyword = item as PdfKeyword;
                if (keyword == null)
                {
                    stack.Add(item);
                    if (stack.Count > 600) stack.RemoveRange(0, 300);
                    continue;
                }
                if (keyword.Value == "endcidchar")
                {
                    for (int i = 0; i + 1 < stack.Count; i += 2)
                    {
                        var source = stack[i] as PdfStringObject;
                        var target = stack[i + 1] as PdfNumber;
                        if (source == null || target == null) continue;
                        map.AddSingle(ToCode(source.Bytes), ((char)Math.Max(0, Math.Min(0xFFFF, target.AsInt))).ToString());
                    }
                    stack.Clear();
                }
                else if (keyword.Value == "endcidrange")
                {
                    for (int i = 0; i + 2 < stack.Count; i += 3)
                    {
                        var low = stack[i] as PdfStringObject;
                        var high = stack[i + 1] as PdfStringObject;
                        var start = stack[i + 2] as PdfNumber;
                        if (low == null || high == null || start == null) continue;
                        map.AddRange(ToCode(low.Bytes), ToCode(high.Bytes),
                            ((char)Math.Max(0, Math.Min(0xFFFF, start.AsInt))).ToString());
                    }
                    stack.Clear();
                }
                else if (keyword.Value.StartsWith("begin", StringComparison.Ordinal))
                {
                    stack.Clear();
                }
            }
            return map;
        }

        private static int ToCode(byte[] bytes)
        {
            int value = 0;
            for (int i = 0; i < bytes.Length && i < 4; i++) value = (value << 8) | bytes[i];
            return value;
        }

        private static string Utf16BeToString(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            if (bytes.Length == 1) return ((char)bytes[0]).ToString();
            var sb = new StringBuilder();
            for (int i = 0; i + 1 < bytes.Length; i += 2)
                sb.Append((char)((bytes[i] << 8) | bytes[i + 1]));
            return sb.ToString();
        }
    }

    /// <summary>単純フォントの符号化表とグリフ名変換。</summary>
    public static class PdfEncodings
    {
        private static string[] _winAnsi;
        private static string[] _macRoman;
        private static string[] _standard;
        private static Dictionary<string, string> _glyphNames;

        private static readonly int[] Cp1252High = new int[]
        {
            0x20AC,0x0000,0x201A,0x0192,0x201E,0x2026,0x2020,0x2021,0x02C6,0x2030,0x0160,0x2039,0x0152,0x0000,0x017D,0x0000,
            0x0000,0x2018,0x2019,0x201C,0x201D,0x2022,0x2013,0x2014,0x02DC,0x2122,0x0161,0x203A,0x0153,0x0000,0x017E,0x0178
        };

        private static readonly int[] MacRomanHigh = new int[]
        {
            0x00C4,0x00C5,0x00C7,0x00C9,0x00D1,0x00D6,0x00DC,0x00E1,0x00E0,0x00E2,0x00E4,0x00E3,0x00E5,0x00E7,0x00E9,0x00E8,
            0x00EA,0x00EB,0x00ED,0x00EC,0x00EE,0x00EF,0x00F1,0x00F3,0x00F2,0x00F4,0x00F6,0x00F5,0x00FA,0x00F9,0x00FB,0x00FC,
            0x2020,0x00B0,0x00A2,0x00A3,0x00A7,0x2022,0x00B6,0x00DF,0x00AE,0x00A9,0x2122,0x00B4,0x00A8,0x2260,0x00C6,0x00D8,
            0x221E,0x00B1,0x2264,0x2265,0x00A5,0x00B5,0x2202,0x2211,0x220F,0x03C0,0x222B,0x00AA,0x00BA,0x03A9,0x00E6,0x00F8,
            0x00BF,0x00A1,0x00AC,0x221A,0x0192,0x2248,0x2206,0x00AB,0x00BB,0x2026,0x00A0,0x00C0,0x00C3,0x00D5,0x0152,0x0153,
            0x2013,0x2014,0x201C,0x201D,0x2018,0x2019,0x00F7,0x25CA,0x00FF,0x0178,0x2044,0x20AC,0x2039,0x203A,0xFB01,0xFB02,
            0x2021,0x00B7,0x201A,0x201E,0x2030,0x00C2,0x00CA,0x00C1,0x00CB,0x00C8,0x00CD,0x00CE,0x00CF,0x00CC,0x00D3,0x00D4,
            0xF8FF,0x00D2,0x00DA,0x00DB,0x00D9,0x0131,0x02C6,0x02DC,0x00AF,0x02D8,0x02D9,0x02DA,0x00B8,0x02DD,0x02DB,0x02C7
        };

        public static string[] GetEncoding(string name, string subtype)
        {
            EnsureTables();
            if (name == "WinAnsiEncoding") return _winAnsi;
            if (name == "MacRomanEncoding") return _macRoman;
            if (name == "StandardEncoding" || name == "MacExpertEncoding") return _standard;
            return _standard;
        }

        private static void EnsureTables()
        {
            if (_winAnsi != null) return;

            var winAnsi = new string[256];
            var standard = new string[256];
            var macRoman = new string[256];
            for (int i = 32; i < 127; i++)
            {
                string value = ((char)i).ToString();
                winAnsi[i] = value;
                standard[i] = value;
                macRoman[i] = value;
            }
            standard[39] = "\u2019";
            standard[96] = "\u2018";
            for (int i = 0; i < 32; i++)
            {
                winAnsi[0x80 + i] = Cp1252High[i] == 0 ? null : char.ConvertFromUtf32(Cp1252High[i]);
            }
            for (int i = 0xA0; i < 256; i++)
            {
                winAnsi[i] = char.ConvertFromUtf32(i);
                standard[i] = null;
            }
            for (int i = 0; i < 128; i++)
            {
                macRoman[128 + i] = char.ConvertFromUtf32(MacRomanHigh[i]);
            }
            // StandardEncoding の高位部分でよく使うものだけ補う
            standard[161] = "!"; standard[164] = "/"; standard[169] = "'"; standard[170] = "\u2018";
            standard[177] = "\u2013"; standard[180] = "\u00B7"; standard[183] = "\u2022";
            standard[184] = "\u201A"; standard[185] = "\u201E"; standard[186] = "\u201D";
            standard[187] = "\u00BB"; standard[188] = "\u2026"; standard[189] = "\u2030";
            standard[191] = "\u00BF"; standard[208] = "\u2014"; standard[241] = "\u00E6";
            standard[245] = "\u0131"; standard[248] = "\u0142"; standard[249] = "\u00F8";
            standard[250] = "\u0153"; standard[251] = "\u00DF";

            _winAnsi = winAnsi;
            _standard = standard;
            _macRoman = macRoman;
            _glyphNames = BuildGlyphNames();
        }

        /// <summary>グリフ名を Unicode に変換する。</summary>
        public static string GlyphToUnicode(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            EnsureTables();

            string value;
            if (_glyphNames.TryGetValue(name, out value)) return value;

            if (name.Length == 1) return name;

            if (name.StartsWith("uni", StringComparison.Ordinal) && name.Length >= 7)
            {
                int code;
                if (int.TryParse(name.Substring(3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                    return char.ConvertFromUtf32(code);
            }
            if (name.StartsWith("u", StringComparison.Ordinal) && name.Length >= 5 && name.Length <= 7)
            {
                int code;
                if (int.TryParse(name.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)
                    && code > 0 && code <= 0x10FFFF)
                    return char.ConvertFromUtf32(code);
            }
            int dot = name.IndexOf('.');
            if (dot > 0) return GlyphToUnicode(name.Substring(0, dot));
            return null;
        }

        private static Dictionary<string, string> BuildGlyphNames()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] names =
            {
                "space"," ","exclam","!","quotedbl","\"","numbersign","#","dollar","$","percent","%",
                "ampersand","&","quotesingle","'","parenleft","(","parenright",")","asterisk","*","plus","+",
                "comma",",","hyphen","-","period",".","slash","/","zero","0","one","1","two","2","three","3",
                "four","4","five","5","six","6","seven","7","eight","8","nine","9","colon",":","semicolon",";",
                "less","<","equal","=","greater",">","question","?","at","@","bracketleft","[","backslash","\\",
                "bracketright","]","asciicircum","^","underscore","_","grave","`","braceleft","{","bar","|",
                "braceright","}","asciitilde","~","quoteleft","\u2018","quoteright","\u2019",
                "quotedblleft","\u201C","quotedblright","\u201D","quotedblbase","\u201E","quotesinglbase","\u201A",
                "endash","\u2013","emdash","\u2014","bullet","\u2022","ellipsis","\u2026","dagger","\u2020",
                "daggerdbl","\u2021","perthousand","\u2030","fraction","\u2044","florin","\u0192",
                "guilsinglleft","\u2039","guilsinglright","\u203A","guillemotleft","\u00AB","guillemotright","\u00BB",
                "fi","\uFB01","fl","\uFB02","dotlessi","\u0131","germandbls","\u00DF","ae","\u00E6","AE","\u00C6",
                "oe","\u0153","OE","\u0152","oslash","\u00F8","Oslash","\u00D8","aring","\u00E5","Aring","\u00C5",
                "ccedilla","\u00E7","Ccedilla","\u00C7","ntilde","\u00F1","Ntilde","\u00D1",
                "eacute","\u00E9","egrave","\u00E8","ecircumflex","\u00EA","edieresis","\u00EB",
                "aacute","\u00E1","agrave","\u00E0","acircumflex","\u00E2","adieresis","\u00E4","atilde","\u00E3",
                "iacute","\u00ED","igrave","\u00EC","icircumflex","\u00EE","idieresis","\u00EF",
                "oacute","\u00F3","ograve","\u00F2","ocircumflex","\u00F4","odieresis","\u00F6","otilde","\u00F5",
                "uacute","\u00FA","ugrave","\u00F9","ucircumflex","\u00FB","udieresis","\u00FC",
                "yacute","\u00FD","ydieresis","\u00FF","thorn","\u00FE","eth","\u00F0",
                "Eacute","\u00C9","Egrave","\u00C8","Ecircumflex","\u00CA","Edieresis","\u00CB",
                "Aacute","\u00C1","Agrave","\u00C0","Acircumflex","\u00C2","Adieresis","\u00C4","Atilde","\u00C3",
                "Iacute","\u00CD","Igrave","\u00CC","Icircumflex","\u00CE","Idieresis","\u00CF",
                "Oacute","\u00D3","Ograve","\u00D2","Ocircumflex","\u00D4","Odieresis","\u00D6","Otilde","\u00D5",
                "Uacute","\u00DA","Ugrave","\u00D9","Ucircumflex","\u00DB","Udieresis","\u00DC",
                "cent","\u00A2","sterling","\u00A3","yen","\u00A5","currency","\u00A4","euro","\u20AC",
                "Euro","\u20AC","section","\u00A7","paragraph","\u00B6","copyright","\u00A9","registered","\u00AE",
                "trademark","\u2122","degree","\u00B0","plusminus","\u00B1","multiply","\u00D7","divide","\u00F7",
                "minus","\u2212","periodcentered","\u00B7","logicalnot","\u00AC","onequarter","\u00BC",
                "onehalf","\u00BD","threequarters","\u00BE","exclamdown","\u00A1","questiondown","\u00BF",
                "acute","\u00B4","dieresis","\u00A8","macron","\u00AF","cedilla","\u00B8","circumflex","\u02C6",
                "tilde","\u02DC","breve","\u02D8","dotaccent","\u02D9","ring","\u02DA","hungarumlaut","\u02DD",
                "ogonek","\u02DB","caron","\u02C7","brokenbar","\u00A6","ordfeminine","\u00AA","ordmasculine","\u00BA",
                "onesuperior","\u00B9","twosuperior","\u00B2","threesuperior","\u00B3","mu","\u00B5",
                "nbspace","\u00A0","softhyphen","\u00AD"
            };
            for (int i = 0; i + 1 < names.Length; i += 2) map[names[i]] = names[i + 1];
            for (char c = 'A'; c <= 'Z'; c++) map[c.ToString()] = c.ToString();
            for (char c = 'a'; c <= 'z'; c++) map[c.ToString()] = c.ToString();
            return map;
        }
    }
}
