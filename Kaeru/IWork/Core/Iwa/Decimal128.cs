using System;
using System.Numerics;

namespace IWorkConverter.Core.Iwa
{
    /// <summary>
    /// Numbers はセルの数値を IEEE 754 decimal128（BID エンコード）で保持する。
    /// 一般的な（非特殊値の）ケースのみを double に変換する。
    /// </summary>
    public static class Decimal128
    {
        public static bool TryRead(byte[] buf, int offset, out double value)
        {
            value = 0.0;
            if (buf == null || offset < 0 || offset + 16 > buf.Length) return false;

            ulong lo = BitConverter.ToUInt64(buf, offset);
            ulong hi = BitConverter.ToUInt64(buf, offset + 8);

            bool negative = (hi & 0x8000000000000000UL) != 0UL;
            int topCombo = (int)((hi >> 61) & 0x3UL);
            if (topCombo == 3) return false; // 特殊形式・無限大・NaN は対象外

            int exponent = (int)((hi >> 49) & 0x3FFFUL) - 6176;
            BigInteger coefficient = (new BigInteger(hi & 0x0001FFFFFFFFFFFFUL) << 64) + new BigInteger(lo);

            if (exponent < -30 || exponent > 30) return false;
            if (coefficient > BigInteger.Pow(10, 20)) return false;

            double d = (double)coefficient;
            if (double.IsNaN(d) || double.IsInfinity(d)) return false;
            d = d * Math.Pow(10.0, exponent);
            if (double.IsNaN(d) || double.IsInfinity(d)) return false;
            if (Math.Abs(d) > 1e18) return false;

            value = negative ? -d : d;
            return true;
        }
    }
}
