using System;
using System.IO;

namespace IWorkConverter.Core.Iwa
{
    /// <summary>
    /// iWork の .iwa は Apple 独自フレーミングの Snappy で圧縮されている。
    /// フレーム = [0x00][長さ 3 バイト(LE)][Snappy 生ブロック] の繰り返し。
    /// </summary>
    public static class AppleSnappy
    {
        public static byte[] Decompress(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                int pos = 0;
                while (pos + 4 <= data.Length)
                {
                    byte type = data[pos];
                    int len = data[pos + 1] | (data[pos + 2] << 8) | (data[pos + 3] << 16);
                    pos += 4;
                    if (len < 0 || pos + len > data.Length) break;
                    if (type == 0)
                    {
                        DecompressBlock(data, pos, len, ms);
                    }
                    else
                    {
                        ms.Write(data, pos, len); // 非圧縮フレーム
                    }
                    pos += len;
                }
                return ms.ToArray();
            }
        }

        private static void DecompressBlock(byte[] src, int offset, int length, MemoryStream output)
        {
            int pos = offset;
            int end = offset + length;

            ulong uncompressed;
            if (!Varints.TryRead(src, ref pos, end, out uncompressed)) return;
            if (uncompressed > 64 * 1024 * 1024) return;

            var dst = new byte[(int)uncompressed];
            int w = 0;

            while (pos < end && w < dst.Length)
            {
                byte tag = src[pos++];
                int kind = tag & 0x03;

                if (kind == 0)
                {
                    int litLen = tag >> 2;
                    if (litLen >= 60)
                    {
                        int extra = litLen - 59;
                        if (pos + extra > end) break;
                        int v = 0;
                        for (int i = 0; i < extra; i++) v |= src[pos + i] << (8 * i);
                        pos += extra;
                        litLen = v;
                    }
                    litLen += 1;
                    if (pos + litLen > end || w + litLen > dst.Length) break;
                    Buffer.BlockCopy(src, pos, dst, w, litLen);
                    pos += litLen;
                    w += litLen;
                }
                else
                {
                    int copyLen;
                    int copyOffset;
                    if (kind == 1)
                    {
                        if (pos >= end) break;
                        copyLen = 4 + ((tag >> 2) & 0x07);
                        copyOffset = ((tag >> 5) & 0x07) << 8 | src[pos++];
                    }
                    else if (kind == 2)
                    {
                        if (pos + 2 > end) break;
                        copyLen = (tag >> 2) + 1;
                        copyOffset = src[pos] | (src[pos + 1] << 8);
                        pos += 2;
                    }
                    else
                    {
                        if (pos + 4 > end) break;
                        copyLen = (tag >> 2) + 1;
                        copyOffset = src[pos] | (src[pos + 1] << 8) | (src[pos + 2] << 16) | (src[pos + 3] << 24);
                        pos += 4;
                    }

                    if (copyOffset <= 0 || copyOffset > w) break;
                    if (w + copyLen > dst.Length) copyLen = dst.Length - w;
                    int from = w - copyOffset;
                    for (int i = 0; i < copyLen; i++) dst[w + i] = dst[from + i]; // 重なりコピーのため 1 バイトずつ
                    w += copyLen;
                }
            }

            output.Write(dst, 0, w);
        }
    }
}
