using System;
using System.Collections.Generic;
using System.Linq;
using IWorkConverter.Core.Iwa;

namespace IWorkConverter.Core.Extract
{
    public sealed class ParaStyleRef
    {
        public int CharIndex;
        public ulong StyleId;
    }

    /// <summary>TSWP のテキストストレージ相当のオブジェクト。</summary>
    public sealed class TextStorage
    {
        public ulong Id;
        public string Component;
        public int Order;
        public string Text = string.Empty;
        public readonly List<ParaStyleRef> ParaStyles = new List<ParaStyleRef>();
    }

    /// <summary>
    /// スキーマなしで「本文を保持しているオブジェクト」を推定して取り出す。
    /// TSWP.StorageArchive はフィールド 3 に本文文字列、フィールド 4 に段落スタイル表を持つ。
    /// </summary>
    public static class TextStorageFinder
    {
        public static List<TextStorage> Find(IwaArchive archive)
        {
            var candidates = new List<TextStorage>();
            var typeCount = new Dictionary<uint, int>();

            foreach (var obj in archive.Objects)
            {
                if (!LooksLikeStorage(obj.Message)) continue;

                var st = new TextStorage
                {
                    Id = obj.Id,
                    Component = obj.Component,
                    Order = obj.Order
                };

                var sb = new System.Text.StringBuilder();
                foreach (var s in obj.Message.Texts(3)) sb.Append(s);
                st.Text = Normalize(sb.ToString());
                if (st.Text.Length == 0) continue;

                foreach (var e in obj.Message.Messages(4))
                {
                    int idx = (int)(e.ULong(1) ?? 0UL);
                    ulong styleId = 0UL;
                    var refMsg = e.Message(2);
                    if (refMsg != null) styleId = refMsg.ULong(1) ?? 0UL;
                    if (styleId == 0UL) styleId = e.ULong(2) ?? 0UL;
                    st.ParaStyles.Add(new ParaStyleRef { CharIndex = idx, StyleId = styleId });
                }
                st.ParaStyles.Sort(delegate (ParaStyleRef a, ParaStyleRef b) { return a.CharIndex.CompareTo(b.CharIndex); });

                candidates.Add(st);
                int n;
                typeCount.TryGetValue(obj.Type, out n);
                typeCount[obj.Type] = n + 1;
            }

            // 最も多く現れた型 ID を本文ストレージ型とみなし、それ以外を除外する
            if (typeCount.Count > 1)
            {
                uint dominant = 0; int best = 0;
                foreach (var kv in typeCount) if (kv.Value > best) { best = kv.Value; dominant = kv.Key; }
                if (best >= 2)
                {
                    var byId = archive.ById;
                    candidates = candidates.Where(delegate (TextStorage s)
                    {
                        IwaObject o;
                        return !byId.TryGetValue(s.Id, out o) || o.Type == dominant;
                    }).ToList();
                }
            }

            return candidates;
        }

        private static bool LooksLikeStorage(ProtoMessage m)
        {
            var f3 = m.Get(3);
            if (f3 == null || f3.Count == 0) return false;
            if (!m.Has(4) && !m.Has(5)) return false;

            int textCount = 0;
            foreach (var f in f3)
            {
                if (f.WireType != 2) return false;
                if (!ProtoText.LooksLikeText(f.Data)) return false;
                if (f.Data.Length > 0) textCount++;
            }
            return textCount > 0;
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace("\r\n", "\n").Replace('\r', '\n');
            s = s.Replace('\u2028', '\n').Replace('\u2029', '\n');
            s = s.Replace("\uFFFC", string.Empty); // オブジェクト置換文字（画像などのアンカー）
            s = s.Replace("\u0000", string.Empty);
            return s;
        }

        /// <summary>スタイルオブジェクトからスタイル名らしき文字列を取り出す。</summary>
        public static string ResolveStyleName(IwaArchive archive, ulong styleId)
        {
            IwaObject obj;
            if (styleId == 0UL || !archive.ById.TryGetValue(styleId, out obj)) return null;
            foreach (var s in CollectStrings(obj.Message, 0))
            {
                if (s.Length == 0 || s.Length > 48) continue;
                if (s.IndexOf('\n') >= 0) continue;
                return s;
            }
            return null;
        }

        private static IEnumerable<string> CollectStrings(ProtoMessage m, int depth)
        {
            if (m == null || depth > 3) yield break;
            foreach (var kv in m.Fields)
            {
                foreach (var f in kv.Value)
                {
                    if (f.WireType != 2) continue;
                    if (ProtoText.LooksLikeText(f.Data) && f.Data.Length > 0)
                    {
                        var s = ProtoText.TryDecode(f.Data);
                        if (!string.IsNullOrWhiteSpace(s)) yield return s;
                    }
                    var child = f.AsMessage();
                    if (child != null && child.Fields.Count > 0)
                        foreach (var s in CollectStrings(child, depth + 1)) yield return s;
                }
            }
        }
    }
}
