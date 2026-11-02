using System;

namespace IWorkConverter.Core.Iwa
{
    /// <summary>protobuf の可変長整数（varint）の読み出し。</summary>
    public static class Varints
    {
        public static bool TryRead(byte[] data, ref int pos, int end, out ulong value)
        {
            value = 0UL;
            int shift = 0;
            while (pos < end)
            {
                byte b = data[pos++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
                if (shift > 63) return false;
            }
            return false;
        }
    }
}
