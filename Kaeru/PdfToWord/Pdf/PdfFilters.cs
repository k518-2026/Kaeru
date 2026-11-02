using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace PdfToWord.Pdf
{
    /// <summary>ストリームのフィルター解除を行う。</summary>
    public static class PdfFilters
    {
        /// <summary>画像専用フィルター（DCTDecode など）は解除せず、その名前を返す。</summary>
        public static byte[] Decode(PdfStreamObject stream, PdfFile file, out string imageFilter)
        {
            imageFilter = null;
            if (stream == null) return new byte[0];

            byte[] data = stream.Raw;
            var filters = new List<string>();
            var parameters = new List<PdfDictionary>();

            PdfObject filterObject = file != null ? file.Resolve(stream.Dictionary.Get("Filter")) : stream.Dictionary.Get("Filter");
            PdfObject parameterObject = file != null ? file.Resolve(stream.Dictionary.Get("DecodeParms")) : stream.Dictionary.Get("DecodeParms");
            if (parameterObject == null)
                parameterObject = file != null ? file.Resolve(stream.Dictionary.Get("DP")) : stream.Dictionary.Get("DP");

            var singleName = filterObject as PdfName;
            var nameArray = filterObject as PdfArray;
            if (singleName != null)
            {
                filters.Add(singleName.Value);
                parameters.Add(AsDictionary(parameterObject, file));
            }
            else if (nameArray != null)
            {
                var parameterArray = parameterObject as PdfArray;
                for (int i = 0; i < nameArray.Count; i++)
                {
                    var name = (file != null ? file.Resolve(nameArray[i]) : nameArray[i]) as PdfName;
                    if (name == null) continue;
                    filters.Add(name.Value);
                    PdfObject p = parameterArray != null ? parameterArray[i] : (i == 0 ? parameterObject : null);
                    parameters.Add(AsDictionary(p, file));
                }
            }

            for (int i = 0; i < filters.Count; i++)
            {
                string filter = filters[i];
                PdfDictionary parms = parameters[i];
                switch (filter)
                {
                    case "FlateDecode":
                    case "Fl":
                        data = ApplyPredictor(Inflate(data), parms, file);
                        break;
                    case "LZWDecode":
                    case "LZW":
                        int early = GetInt(parms, "EarlyChange", 1, file);
                        data = ApplyPredictor(LzwDecode(data, early), parms, file);
                        break;
                    case "ASCII85Decode":
                    case "A85":
                        data = Ascii85Decode(data);
                        break;
                    case "ASCIIHexDecode":
                    case "AHx":
                        data = AsciiHexDecode(data);
                        break;
                    case "RunLengthDecode":
                    case "RL":
                        data = RunLengthDecode(data);
                        break;
                    case "Crypt":
                        break;
                    default:
                        imageFilter = filter;
                        return data;
                }
            }
            return data;
        }

        public static byte[] Decode(PdfStreamObject stream, PdfFile file)
        {
            string ignored;
            return Decode(stream, file, out ignored);
        }

        private static PdfDictionary AsDictionary(PdfObject value, PdfFile file)
        {
            if (file != null) value = file.Resolve(value);
            return value as PdfDictionary;
        }

        private static int GetInt(PdfDictionary dictionary, string key, int fallback, PdfFile file)
        {
            if (dictionary == null) return fallback;
            PdfObject value = dictionary.Get(key);
            if (file != null) value = file.Resolve(value);
            var number = value as PdfNumber;
            return number != null ? number.AsInt : fallback;
        }

        /// <summary>zlib / raw deflate のどちらでも展開できるようにする。</summary>
        public static byte[] Inflate(byte[] data)
        {
            if (data == null || data.Length == 0) return new byte[0];

            int start = 0;
            while (start < data.Length && PdfParser.IsWhitespace(data[start])) start++;
            if (start >= data.Length) return new byte[0];

            byte[] body = start == 0 ? data : Slice(data, start, data.Length - start);

            byte[] result = TryInflate(body, true);
            if (result != null && result.Length > 0) return result;
            result = TryInflate(body, false);
            if (result != null && result.Length > 0) return result;
            if (body.Length > 1)
            {
                result = TryInflate(Slice(body, 1, body.Length - 1), false);
                if (result != null && result.Length > 0) return result;
            }
            return new byte[0];
        }

        private static byte[] TryInflate(byte[] data, bool zlibHeader)
        {
            try
            {
                using (var input = new MemoryStream(data))
                using (var output = new MemoryStream())
                {
                    Stream decompressor = zlibHeader
                        ? (Stream)new ZLibStream(input, CompressionMode.Decompress, true)
                        : new DeflateStream(input, CompressionMode.Decompress, true);
                    try
                    {
                        var buffer = new byte[16384];
                        int read;
                        while ((read = decompressor.Read(buffer, 0, buffer.Length)) > 0)
                            output.Write(buffer, 0, read);
                    }
                    catch (InvalidDataException)
                    {
                        // 途中で壊れていても、そこまでの内容は使う
                    }
                    finally
                    {
                        decompressor.Dispose();
                    }
                    return output.ToArray();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static byte[] Slice(byte[] data, int offset, int length)
        {
            var result = new byte[length];
            Buffer.BlockCopy(data, offset, result, 0, length);
            return result;
        }

        public static byte[] ApplyPredictor(byte[] data, PdfDictionary parms, PdfFile file)
        {
            int predictor = GetInt(parms, "Predictor", 1, file);
            if (predictor <= 1 || data.Length == 0) return data;

            int colors = GetInt(parms, "Colors", 1, file);
            int bitsPerComponent = GetInt(parms, "BitsPerComponent", 8, file);
            int columns = GetInt(parms, "Columns", 1, file);
            int bytesPerPixel = Math.Max(1, colors * bitsPerComponent / 8);
            int rowLength = (columns * colors * bitsPerComponent + 7) / 8;

            if (predictor == 2)
            {
                if (bitsPerComponent != 8) return data;
                int rows = data.Length / rowLength;
                for (int r = 0; r < rows; r++)
                {
                    int offset = r * rowLength;
                    for (int i = bytesPerPixel; i < rowLength; i++)
                        data[offset + i] = (byte)((data[offset + i] + data[offset + i - bytesPerPixel]) & 0xFF);
                }
                return data;
            }

            // PNG 系プレディクター
            var output = new List<byte>(data.Length);
            var previous = new byte[rowLength];
            var current = new byte[rowLength];
            int position = 0;
            while (position + 1 <= data.Length)
            {
                int filterType = data[position++];
                int available = Math.Min(rowLength, data.Length - position);
                if (available <= 0) break;
                Array.Clear(current, 0, rowLength);
                Buffer.BlockCopy(data, position, current, 0, available);
                position += available;

                for (int i = 0; i < rowLength; i++)
                {
                    int raw = current[i];
                    int left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                    int up = previous[i];
                    int upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                    int value;
                    switch (filterType)
                    {
                        case 0: value = raw; break;
                        case 1: value = raw + left; break;
                        case 2: value = raw + up; break;
                        case 3: value = raw + ((left + up) >> 1); break;
                        case 4: value = raw + Paeth(left, up, upLeft); break;
                        default: value = raw; break;
                    }
                    current[i] = (byte)(value & 0xFF);
                }
                for (int i = 0; i < rowLength; i++) output.Add(current[i]);
                Buffer.BlockCopy(current, 0, previous, 0, rowLength);
            }
            return output.ToArray();
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            if (pb <= pc) return b;
            return c;
        }

        public static byte[] LzwDecode(byte[] data, int earlyChange)
        {
            var output = new List<byte>(Math.Max(64, data.Length * 4));
            var table = new List<byte[]>(4096);
            Action resetTable = delegate
            {
                table.Clear();
                for (int i = 0; i < 256; i++) table.Add(new byte[] { (byte)i });
                table.Add(new byte[0]); // 256: clear
                table.Add(new byte[0]); // 257: EOD
            };
            resetTable();

            int codeLength = 9;
            int bitBuffer = 0;
            int bitCount = 0;
            byte[] previous = null;

            for (int i = 0; i < data.Length; i++)
            {
                bitBuffer = (bitBuffer << 8) | data[i];
                bitCount += 8;
                while (bitCount >= codeLength)
                {
                    int code = (bitBuffer >> (bitCount - codeLength)) & ((1 << codeLength) - 1);
                    bitCount -= codeLength;

                    if (code == 256)
                    {
                        resetTable();
                        codeLength = 9;
                        previous = null;
                        continue;
                    }
                    if (code == 257) return output.ToArray();

                    byte[] entry;
                    if (code < table.Count)
                    {
                        entry = table[code];
                        if (previous != null)
                        {
                            var added = new byte[previous.Length + 1];
                            Buffer.BlockCopy(previous, 0, added, 0, previous.Length);
                            added[previous.Length] = entry.Length > 0 ? entry[0] : (byte)0;
                            table.Add(added);
                        }
                    }
                    else
                    {
                        if (previous == null) return output.ToArray();
                        entry = new byte[previous.Length + 1];
                        Buffer.BlockCopy(previous, 0, entry, 0, previous.Length);
                        entry[previous.Length] = previous[0];
                        table.Add(entry);
                    }

                    output.AddRange(entry);
                    previous = entry;

                    int limit = table.Count + (earlyChange != 0 ? 1 : 0);
                    if (limit >= 512 && codeLength == 9) codeLength = 10;
                    else if (limit >= 1024 && codeLength == 10) codeLength = 11;
                    else if (limit >= 2048 && codeLength == 11) codeLength = 12;
                }
            }
            return output.ToArray();
        }

        public static byte[] Ascii85Decode(byte[] data)
        {
            var output = new List<byte>(data.Length);
            uint tuple = 0;
            int count = 0;
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                if (PdfParser.IsWhitespace(b)) continue;
                if (b == (byte)'~') break;
                if (b == (byte)'z' && count == 0)
                {
                    output.Add(0); output.Add(0); output.Add(0); output.Add(0);
                    continue;
                }
                if (b < (byte)'!' || b > (byte)'u') continue;
                tuple = tuple * 85 + (uint)(b - (byte)'!');
                count++;
                if (count == 5)
                {
                    output.Add((byte)(tuple >> 24));
                    output.Add((byte)(tuple >> 16));
                    output.Add((byte)(tuple >> 8));
                    output.Add((byte)tuple);
                    tuple = 0;
                    count = 0;
                }
            }
            if (count > 0)
            {
                for (int i = count; i < 5; i++) tuple = tuple * 85 + 84;
                for (int i = 0; i < count - 1; i++) output.Add((byte)(tuple >> (24 - 8 * i)));
            }
            return output.ToArray();
        }

        public static byte[] AsciiHexDecode(byte[] data)
        {
            var output = new List<byte>(data.Length / 2 + 1);
            int high = -1;
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                if (b == (byte)'>') break;
                int value = HexValue(b);
                if (value < 0) continue;
                if (high < 0) high = value;
                else { output.Add((byte)(high * 16 + value)); high = -1; }
            }
            if (high >= 0) output.Add((byte)(high * 16));
            return output.ToArray();
        }

        private static int HexValue(byte b)
        {
            if (b >= (byte)'0' && b <= (byte)'9') return b - (byte)'0';
            if (b >= (byte)'a' && b <= (byte)'f') return b - (byte)'a' + 10;
            if (b >= (byte)'A' && b <= (byte)'F') return b - (byte)'A' + 10;
            return -1;
        }

        public static byte[] RunLengthDecode(byte[] data)
        {
            var output = new List<byte>(data.Length * 2);
            int i = 0;
            while (i < data.Length)
            {
                int length = data[i++];
                if (length == 128) break;
                if (length < 128)
                {
                    for (int k = 0; k <= length && i < data.Length; k++) output.Add(data[i++]);
                }
                else
                {
                    if (i >= data.Length) break;
                    byte value = data[i++];
                    for (int k = 0; k < 257 - length; k++) output.Add(value);
                }
            }
            return output.ToArray();
        }
    }
}
