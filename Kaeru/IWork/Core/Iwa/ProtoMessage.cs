using System;
using System.Collections.Generic;
using System.Text;

namespace IWorkConverter.Core.Iwa
{
    /// <summary>protobuf の 1 フィールド。スキーマを持たない汎用表現。</summary>
    public sealed class ProtoField
    {
        public int Number;
        public int WireType;      // 0=varint, 1=fixed64, 2=bytes, 5=fixed32
        public ulong Varint;
        public byte[] Data = Array.Empty<byte>();

        public ProtoMessage AsMessage()
        {
            if (WireType != 2) return null;
            return ProtoMessage.Parse(Data);
        }

        public string AsText()
        {
            if (WireType != 2) return null;
            return ProtoText.TryDecode(Data);
        }
    }

    /// <summary>
    /// .proto 定義を使わずに protobuf メッセージを解析する汎用リーダー。
    /// iWork の内部形式は非公開のため、フィールド番号と構造から内容を推定する。
    /// </summary>
    public sealed class ProtoMessage
    {
        private readonly Dictionary<int, List<ProtoField>> _fields = new Dictionary<int, List<ProtoField>>();

        public IReadOnlyDictionary<int, List<ProtoField>> Fields { get { return _fields; } }

        public static ProtoMessage Parse(byte[] data)
        {
            return Parse(data, 0, data == null ? 0 : data.Length);
        }

        public static ProtoMessage Parse(byte[] data, int offset, int length)
        {
            var msg = new ProtoMessage();
            if (data == null) return msg;
            int pos = offset;
            int end = Math.Min(data.Length, offset + length);
            while (pos < end)
            {
                ulong key;
                if (!Varints.TryRead(data, ref pos, end, out key)) break;
                int number = (int)(key >> 3);
                int wire = (int)(key & 7);
                if (number <= 0) break;

                var f = new ProtoField { Number = number, WireType = wire };
                if (wire == 0)
                {
                    ulong v;
                    if (!Varints.TryRead(data, ref pos, end, out v)) break;
                    f.Varint = v;
                }
                else if (wire == 1)
                {
                    if (pos + 8 > end) break;
                    f.Data = Slice(data, pos, 8);
                    f.Varint = BitConverter.ToUInt64(f.Data, 0);
                    pos += 8;
                }
                else if (wire == 2)
                {
                    ulong len;
                    if (!Varints.TryRead(data, ref pos, end, out len)) break;
                    if (len > (ulong)(end - pos)) break;
                    f.Data = Slice(data, pos, (int)len);
                    pos += (int)len;
                }
                else if (wire == 5)
                {
                    if (pos + 4 > end) break;
                    f.Data = Slice(data, pos, 4);
                    f.Varint = BitConverter.ToUInt32(f.Data, 0);
                    pos += 4;
                }
                else
                {
                    break; // group（廃止）は未対応
                }

                List<ProtoField> list;
                if (!msg._fields.TryGetValue(number, out list))
                {
                    list = new List<ProtoField>();
                    msg._fields[number] = list;
                }
                list.Add(f);
            }
            return msg;
        }

        private static byte[] Slice(byte[] src, int offset, int count)
        {
            var dst = new byte[count];
            Buffer.BlockCopy(src, offset, dst, 0, count);
            return dst;
        }

        public bool Has(int number) { return _fields.ContainsKey(number); }

        public List<ProtoField> Get(int number)
        {
            List<ProtoField> list;
            return _fields.TryGetValue(number, out list) ? list : null;
        }

        public ProtoField First(int number)
        {
            var list = Get(number);
            return (list != null && list.Count > 0) ? list[0] : null;
        }

        public ulong? ULong(int number)
        {
            var f = First(number);
            if (f == null) return null;
            if (f.WireType == 0 || f.WireType == 1 || f.WireType == 5) return f.Varint;
            return null;
        }

        public string Text(int number)
        {
            var f = First(number);
            return f == null ? null : f.AsText();
        }

        public ProtoMessage Message(int number)
        {
            var f = First(number);
            return f == null ? null : f.AsMessage();
        }

        public IEnumerable<ProtoMessage> Messages(int number)
        {
            var list = Get(number);
            if (list == null) yield break;
            foreach (var f in list)
            {
                if (f.WireType != 2) continue;
                var m = f.AsMessage();
                if (m != null) yield return m;
            }
        }

        public IEnumerable<string> Texts(int number)
        {
            var list = Get(number);
            if (list == null) yield break;
            foreach (var f in list)
            {
                if (f.WireType != 2) continue;
                var s = f.AsText();
                if (s != null) yield return s;
            }
        }
    }

    /// <summary>バイト列が「文字列」かどうかを厳密 UTF-8 で判定する。</summary>
    public static class ProtoText
    {
        private static readonly UTF8Encoding Strict = new UTF8Encoding(false, true);

        public static string TryDecode(byte[] data)
        {
            if (data == null) return null;
            if (data.Length == 0) return string.Empty;
            try { return Strict.GetString(data); }
            catch { return null; }
        }

        public static bool LooksLikeText(byte[] data)
        {
            var s = TryDecode(data);
            if (s == null) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\uFFFD') return false;
                if (c < 0x20 && c != '\n' && c != '\r' && c != '\t' && c != '\v' && c != '\f') return false;
                if (c == 0x7F) return false;
            }
            return true;
        }
    }
}
