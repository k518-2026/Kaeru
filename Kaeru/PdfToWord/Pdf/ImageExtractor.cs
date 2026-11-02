using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using PdfToWord.Models;

namespace PdfToWord.Pdf
{
    /// <summary>PDF の画像 XObject を PNG / JPEG として取り出す。</summary>
    public static class ImageExtractor
    {
        public static DocImage Extract(PdfStreamObject stream, PdfFile file, out string reason)
        {
            reason = null;
            if (stream == null) { reason = "画像データがありません"; return null; }

            PdfDictionary dictionary = stream.Dictionary;
            int width = file.GetInt(dictionary, "Width", file.GetInt(dictionary, "W", 0));
            int height = file.GetInt(dictionary, "Height", file.GetInt(dictionary, "H", 0));
            if (width <= 0 || height <= 0) { reason = "画像サイズが不明です"; return null; }
            if ((long)width * height > 60000000L) { reason = "画像が大きすぎます"; return null; }

            var maskFlag = file.Resolve(dictionary.Get("ImageMask")) as PdfBoolean;
            if (maskFlag != null && maskFlag.Value) { reason = "ステンシルマスク画像"; return null; }

            string imageFilter;
            byte[] data = PdfFilters.Decode(stream, file, out imageFilter);

            if (imageFilter == "DCTDecode" || imageFilter == "DCT")
            {
                return new DocImage
                {
                    Data = data,
                    Extension = "jpg",
                    PixelWidth = width,
                    PixelHeight = height
                };
            }
            if (imageFilter != null)
            {
                reason = imageFilter + " 形式の画像には未対応です";
                return null;
            }

            int bitsPerComponent = file.GetInt(dictionary, "BitsPerComponent", file.GetInt(dictionary, "BPC", 8));
            PdfObject colorSpace = file.Resolve(dictionary.Get("ColorSpace"));
            if (colorSpace == null) colorSpace = file.Resolve(dictionary.Get("CS"));

            byte[] rgb = ToRgb(data, width, height, bitsPerComponent, colorSpace, file, dictionary, out reason);
            if (rgb == null) return null;

            return new DocImage
            {
                Data = EncodePng(rgb, width, height),
                Extension = "png",
                PixelWidth = width,
                PixelHeight = height
            };
        }

        private static byte[] ToRgb(byte[] data, int width, int height, int bits, PdfObject colorSpace,
            PdfFile file, PdfDictionary dictionary, out string reason)
        {
            reason = null;
            string spaceName = null;
            PdfArray spaceArray = colorSpace as PdfArray;
            var spaceNameObject = colorSpace as PdfName;
            if (spaceNameObject != null) spaceName = spaceNameObject.Value;
            if (spaceArray != null)
            {
                var first = file.Resolve(spaceArray[0]) as PdfName;
                if (first != null) spaceName = first.Value;
            }
            if (spaceName == null) spaceName = "DeviceGray";

            int components;
            switch (spaceName)
            {
                case "DeviceRGB":
                case "RGB":
                case "CalRGB":
                case "Lab":
                    components = 3; break;
                case "DeviceCMYK":
                case "CMYK":
                    components = 4; break;
                case "DeviceGray":
                case "G":
                case "CalGray":
                    components = 1; break;
                case "Indexed":
                case "I":
                    components = 1; break;
                case "ICCBased":
                    {
                        var iccStream = spaceArray != null ? file.Resolve(spaceArray[1]) as PdfStreamObject : null;
                        components = iccStream != null ? file.GetInt(iccStream.Dictionary, "N", 3) : 3;
                        break;
                    }
                case "Separation":
                    components = 1; break;
                case "DeviceN":
                    {
                        var names = spaceArray != null ? file.Resolve(spaceArray[1]) as PdfArray : null;
                        components = names != null ? names.Count : 1;
                        break;
                    }
                default:
                    components = 1; break;
            }

            byte[] palette = null;
            if (spaceName == "Indexed" || spaceName == "I")
            {
                palette = ReadPalette(spaceArray, file);
                if (palette == null) { reason = "パレット情報を読めません"; return null; }
            }

            long expected = ((long)width * components * bits + 7) / 8 * height;
            if (data.Length < expected)
            {
                if (data.Length < expected / 2) { reason = "画像データが不足しています"; return null; }
                var padded = new byte[expected];
                Buffer.BlockCopy(data, 0, padded, 0, data.Length);
                data = padded;
            }

            bool invert = false;
            var decode = file.Resolve(dictionary.Get("Decode")) as PdfArray;
            if (decode == null) decode = file.Resolve(dictionary.Get("D")) as PdfArray;
            if (decode != null && decode.Count >= 2)
            {
                var firstValue = file.Resolve(decode[0]) as PdfNumber;
                if (firstValue != null && firstValue.Value == 1) invert = true;
            }

            var output = new byte[width * height * 3];
            int rowBytes = (int)(((long)width * components * bits + 7) / 8);
            int maxValue = (1 << bits) - 1;

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * rowBytes;
                for (int x = 0; x < width; x++)
                {
                    int target = (y * width + x) * 3;
                    int[] sample = new int[components];
                    for (int c = 0; c < components; c++)
                    {
                        int index = x * components + c;
                        sample[c] = ReadSample(data, rowOffset, index, bits);
                    }

                    byte r, g, b;
                    if (palette != null)
                    {
                        int paletteIndex = sample[0] * 3;
                        if (paletteIndex + 2 < palette.Length)
                        {
                            r = palette[paletteIndex]; g = palette[paletteIndex + 1]; b = palette[paletteIndex + 2];
                        }
                        else { r = g = b = 0; }
                    }
                    else if (components == 3)
                    {
                        r = Scale(sample[0], maxValue); g = Scale(sample[1], maxValue); b = Scale(sample[2], maxValue);
                    }
                    else if (components == 4)
                    {
                        double c0 = sample[0] / (double)maxValue;
                        double m0 = sample[1] / (double)maxValue;
                        double y0 = sample[2] / (double)maxValue;
                        double k0 = sample[3] / (double)maxValue;
                        r = (byte)Math.Max(0, Math.Min(255, 255 * (1 - c0) * (1 - k0)));
                        g = (byte)Math.Max(0, Math.Min(255, 255 * (1 - m0) * (1 - k0)));
                        b = (byte)Math.Max(0, Math.Min(255, 255 * (1 - y0) * (1 - k0)));
                    }
                    else
                    {
                        byte gray = Scale(sample[0], maxValue);
                        if (invert) gray = (byte)(255 - gray);
                        r = g = b = gray;
                    }
                    output[target] = r;
                    output[target + 1] = g;
                    output[target + 2] = b;
                }
            }
            return output;
        }

        private static byte Scale(int value, int maxValue)
        {
            if (maxValue <= 0) return 0;
            if (maxValue == 255) return (byte)Math.Max(0, Math.Min(255, value));
            return (byte)Math.Max(0, Math.Min(255, value * 255 / maxValue));
        }

        private static int ReadSample(byte[] data, int rowOffset, int index, int bits)
        {
            switch (bits)
            {
                case 8:
                    {
                        int position = rowOffset + index;
                        return position < data.Length ? data[position] : 0;
                    }
                case 16:
                    {
                        int position = rowOffset + index * 2;
                        return position < data.Length ? data[position] : 0;
                    }
                case 1:
                case 2:
                case 4:
                    {
                        int bitPosition = index * bits;
                        int position = rowOffset + bitPosition / 8;
                        if (position >= data.Length) return 0;
                        int shift = 8 - bits - (bitPosition % 8);
                        return (data[position] >> shift) & ((1 << bits) - 1);
                    }
                default:
                    {
                        int position = rowOffset + index;
                        return position < data.Length ? data[position] : 0;
                    }
            }
        }

        private static byte[] ReadPalette(PdfArray spaceArray, PdfFile file)
        {
            if (spaceArray == null || spaceArray.Count < 4) return null;
            PdfObject baseSpace = file.Resolve(spaceArray[1]);
            PdfObject lookup = file.Resolve(spaceArray[3]);

            byte[] table = null;
            var lookupString = lookup as PdfStringObject;
            var lookupStream = lookup as PdfStreamObject;
            if (lookupString != null) table = lookupString.Bytes;
            else if (lookupStream != null) table = PdfFilters.Decode(lookupStream, file);
            if (table == null) return null;

            int baseComponents = 3;
            var baseName = baseSpace as PdfName;
            var baseArray = baseSpace as PdfArray;
            if (baseName != null)
            {
                if (baseName.Value == "DeviceGray" || baseName.Value == "CalGray") baseComponents = 1;
                else if (baseName.Value == "DeviceCMYK") baseComponents = 4;
                else baseComponents = 3;
            }
            else if (baseArray != null)
            {
                var kind = file.Resolve(baseArray[0]) as PdfName;
                if (kind != null && kind.Value == "ICCBased")
                {
                    var iccStream = file.Resolve(baseArray[1]) as PdfStreamObject;
                    baseComponents = iccStream != null ? file.GetInt(iccStream.Dictionary, "N", 3) : 3;
                }
            }

            int entries = table.Length / Math.Max(1, baseComponents);
            var palette = new byte[entries * 3];
            for (int i = 0; i < entries; i++)
            {
                int source = i * baseComponents;
                byte r, g, b;
                if (baseComponents == 1)
                {
                    r = g = b = table[source];
                }
                else if (baseComponents == 4)
                {
                    double c = table[source] / 255.0, m = table[source + 1] / 255.0;
                    double y = table[source + 2] / 255.0, k = table[source + 3] / 255.0;
                    r = (byte)(255 * (1 - c) * (1 - k));
                    g = (byte)(255 * (1 - m) * (1 - k));
                    b = (byte)(255 * (1 - y) * (1 - k));
                }
                else
                {
                    r = table[source];
                    g = source + 1 < table.Length ? table[source + 1] : (byte)0;
                    b = source + 2 < table.Length ? table[source + 2] : (byte)0;
                }
                palette[i * 3] = r;
                palette[i * 3 + 1] = g;
                palette[i * 3 + 2] = b;
            }
            return palette;
        }

        /// <summary>RGB バイト列を PNG にまとめる。</summary>
        public static byte[] EncodePng(byte[] rgb, int width, int height)
        {
            var raw = new byte[(width * 3 + 1) * height];
            for (int y = 0; y < height; y++)
            {
                int target = y * (width * 3 + 1);
                raw[target] = 0; // フィルターなし
                Buffer.BlockCopy(rgb, y * width * 3, raw, target + 1, width * 3);
            }

            byte[] compressed;
            using (var buffer = new MemoryStream())
            {
                using (var deflate = new ZLibStream(buffer, CompressionLevel.Optimal, true))
                {
                    deflate.Write(raw, 0, raw.Length);
                }
                compressed = buffer.ToArray();
            }

            using (var output = new MemoryStream())
            {
                output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

                var header = new MemoryStream();
                WriteInt(header, width);
                WriteInt(header, height);
                header.WriteByte(8);   // bit depth
                header.WriteByte(2);   // color type: truecolor
                header.WriteByte(0);
                header.WriteByte(0);
                header.WriteByte(0);
                WriteChunk(output, "IHDR", header.ToArray());
                WriteChunk(output, "IDAT", compressed);
                WriteChunk(output, "IEND", new byte[0]);
                return output.ToArray();
            }
        }

        private static void WriteInt(Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            WriteInt(stream, data.Length);
            var typeBytes = new byte[4];
            for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
            stream.Write(typeBytes, 0, 4);
            stream.Write(data, 0, data.Length);

            uint crc = Crc32(typeBytes, 0xFFFFFFFF);
            crc = Crc32(data, crc);
            crc ^= 0xFFFFFFFF;
            WriteInt(stream, unchecked((int)crc));
        }

        private static uint[] _crcTable;

        private static uint Crc32(byte[] data, uint crc)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint value = i;
                    for (int k = 0; k < 8; k++)
                        value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                    _crcTable[i] = value;
                }
            }
            for (int i = 0; i < data.Length; i++)
                crc = _crcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
