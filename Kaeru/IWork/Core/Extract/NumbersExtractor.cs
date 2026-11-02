using System;
using System.Collections.Generic;
using System.Linq;
using IWorkConverter.Core.Iwa;
using IWorkConverter.Core.Model;

namespace IWorkConverter.Core.Extract
{
    /// <summary>
    /// Numbers の表を復元する。表の実体は
    ///   表モデル → タイル（セル格納バッファ）＋ 文字列テーブル
    /// という構成になっている。スキーマが非公開のため、構造の形からそれぞれを推定する。
    /// </summary>
    public static class NumbersExtractor
    {
        private static readonly DateTime Epoch = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private sealed class TileRow
        {
            public int RowIndex;
            public byte[] Storage;
            public int[] Offsets;
        }

        private sealed class RefEntry
        {
            public int Index;
            public ulong Id;
        }

        public static BookModel Extract(IwaArchive archive, ConversionOptions opt, Action<string> note)
        {
            var book = new BookModel();

            var stringTables = new Dictionary<ulong, Dictionary<uint, string>>();
            var tiles = new Dictionary<ulong, List<TileRow>>();

            foreach (var obj in archive.Objects)
            {
                var st = TryReadStringTable(obj.Message);
                if (st != null && st.Count > 0 && !stringTables.ContainsKey(obj.Id)) stringTables[obj.Id] = st;

                var rows = TryReadTile(obj.Message);
                if (rows != null && rows.Count > 0 && !tiles.ContainsKey(obj.Id)) tiles[obj.Id] = rows;
            }

            if (tiles.Count == 0)
            {
                if (note != null) note("表データを検出できませんでした。文字列だけを 1 列に書き出します。");
                var fallback = new TableModel { Name = "抽出テキスト" };
                int r = 0;
                foreach (var st in stringTables.Values)
                    foreach (var s in st.OrderBy(k => k.Key).Select(k => k.Value))
                        fallback.Set(r++, 0, new CellModel { Text = s });
                if (fallback.Cells.Count > 0) book.Tables.Add(fallback);
                return book;
            }

            // 表モデル = タイルと文字列テーブルの両方を参照しているオブジェクト
            var used = new HashSet<ulong>();
            foreach (var obj in archive.Objects)
            {
                var refs = new List<RefEntry>();
                CollectRefs(obj.Message, refs, -1, 0);
                if (refs.Count == 0) continue;

                var tileRefs = refs.Where(r => tiles.ContainsKey(r.Id)).ToList();
                if (tileRefs.Count == 0) continue;
                if (tileRefs.Any(r => used.Contains(r.Id))) continue;

                var strRefs = refs.Where(r => stringTables.ContainsKey(r.Id)).ToList();
                Dictionary<uint, string> strings = null;
                foreach (var sr in strRefs)
                {
                    var cand = stringTables[sr.Id];
                    if (strings == null || cand.Count > strings.Count) strings = cand;
                }
                if (strings == null) strings = new Dictionary<uint, string>();

                var table = new TableModel { Name = GuessTableName(obj.Message) };

                int tileOrder = 0;
                foreach (var tr in tileRefs)
                {
                    int startRow = (tr.Index >= 0 ? tr.Index : tileOrder) * 256;
                    foreach (var row in tiles[tr.Id])
                        ReadRow(table, startRow + row.RowIndex, row, strings);
                    used.Add(tr.Id);
                    tileOrder++;
                }

                if (table.Cells.Count > 0) book.Tables.Add(table);
            }

            // どの表モデルにも結びつかなかったタイルを救済
            var orphans = tiles.Keys.Where(k => !used.Contains(k)).ToList();
            if (orphans.Count > 0)
            {
                Dictionary<uint, string> strings = stringTables.Values.OrderByDescending(v => v.Count).FirstOrDefault()
                                                  ?? new Dictionary<uint, string>();
                var table = new TableModel { Name = "表" };
                int order = 0;
                foreach (var id in orphans)
                {
                    foreach (var row in tiles[id]) ReadRow(table, order * 256 + row.RowIndex, row, strings);
                    order++;
                }
                if (table.Cells.Count > 0) book.Tables.Add(table);
                if (note != null) note("シートに割り当てられないタイル " + orphans.Count + " 件をまとめて出力しました。");
            }

            int n = 1;
            foreach (var t in book.Tables)
                if (string.IsNullOrWhiteSpace(t.Name)) t.Name = "表" + (n++);

            return book;
        }

        private static string GuessTableName(ProtoMessage m)
        {
            foreach (var kv in m.Fields)
            {
                foreach (var f in kv.Value)
                {
                    if (f.WireType != 2) continue;
                    if (!ProtoText.LooksLikeText(f.Data)) continue;
                    var s = ProtoText.TryDecode(f.Data);
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    if (s.Length > 40 || s.IndexOf('\n') >= 0) continue;
                    return s;
                }
            }
            return null;
        }

        private static void CollectRefs(ProtoMessage m, List<RefEntry> refs, int inheritedIndex, int depth)
        {
            if (m == null || depth > 6 || refs.Count > 4096) return;
            foreach (var kv in m.Fields)
            {
                foreach (var f in kv.Value)
                {
                    if (f.WireType != 2) continue;
                    var child = f.AsMessage();
                    if (child == null || child.Fields.Count == 0) continue;

                    // {1: <id>} だけのメッセージは参照とみなす
                    if (child.Fields.Count == 1)
                    {
                        var one = child.First(1);
                        if (one != null && one.WireType == 0 && one.Varint > 0UL)
                        {
                            refs.Add(new RefEntry { Index = inheritedIndex, Id = one.Varint });
                            continue;
                        }
                    }

                    int idx = inheritedIndex;
                    var f1 = child.First(1);
                    if (f1 != null && f1.WireType == 0 && f1.Varint < 4096UL) idx = (int)f1.Varint;

                    CollectRefs(child, refs, idx, depth + 1);
                }
            }
        }

        /// <summary>文字列テーブル（キー → 文字列）を推定する。</summary>
        private static Dictionary<uint, string> TryReadStringTable(ProtoMessage m)
        {
            var entries = m.Get(3);
            if (entries == null || entries.Count == 0) return null;

            var map = new Dictionary<uint, string>();
            int matched = 0;
            foreach (var f in entries)
            {
                if (f.WireType != 2) return null;
                var e = f.AsMessage();
                if (e == null) return null;
                ulong? key = e.ULong(1);
                if (key == null || key.Value > uint.MaxValue) return null;

                string value = null;
                foreach (var kv in e.Fields)
                {
                    if (kv.Key == 1) continue;
                    foreach (var vf in kv.Value)
                    {
                        if (vf.WireType != 2) continue;
                        if (!ProtoText.LooksLikeText(vf.Data)) continue;
                        var s = ProtoText.TryDecode(vf.Data);
                        if (s != null) { value = s; break; }
                    }
                    if (value != null) break;
                }
                if (value == null) continue;
                map[(uint)key.Value] = value;
                matched++;
            }

            return matched > 0 ? map : null;
        }

        /// <summary>タイル（行ごとのセル格納バッファ）を推定する。</summary>
        private static List<TileRow> TryReadTile(ProtoMessage m)
        {
            List<TileRow> rows = null;
            foreach (var kv in m.Fields)
            {
                var candidate = new List<TileRow>();
                foreach (var f in kv.Value)
                {
                    if (f.WireType != 2) continue;
                    var child = f.AsMessage();
                    if (child == null) continue;
                    var row = TryReadRowInfo(child);
                    if (row != null) candidate.Add(row);
                }
                if (candidate.Count > 0 && (rows == null || candidate.Count > rows.Count)) rows = candidate;
            }
            return rows;
        }

        private static TileRow TryReadRowInfo(ProtoMessage m)
        {
            ulong? idx = m.ULong(1);
            if (idx == null || idx.Value > 1024UL) return null;

            var blobs = new List<byte[]>();
            foreach (var kv in m.Fields)
                foreach (var f in kv.Value)
                    if (f.WireType == 2 && f.Data.Length >= 4) blobs.Add(f.Data);
            if (blobs.Count < 2) return null;

            byte[] storage = null;
            foreach (var b in blobs) if (storage == null || b.Length > storage.Length) storage = b;
            if (storage == null || storage.Length < 4) return null;

            int[] bestOffsets = null;
            int bestScale = 1;
            int bestScore = 0;

            foreach (var b in blobs)
            {
                if (ReferenceEquals(b, storage)) continue;
                if (b.Length % 2 != 0) continue;
                var offs = ToInt16Array(b);

                for (int scale = 1; scale <= 4; scale *= 4)
                {
                    int score = ScoreOffsets(storage, offs, scale);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestOffsets = offs;
                        bestScale = scale;
                    }
                }
            }

            if (bestOffsets == null || bestScore == 0) return null;

            var scaled = new int[bestOffsets.Length];
            for (int i = 0; i < bestOffsets.Length; i++)
                scaled[i] = bestOffsets[i] < 0 ? -1 : bestOffsets[i] * bestScale;

            return new TileRow { RowIndex = (int)idx.Value, Storage = storage, Offsets = scaled };
        }

        private static int ScoreOffsets(byte[] storage, int[] offsets, int scale)
        {
            int good = 0;
            int bad = 0;
            int last = -1;
            foreach (var raw in offsets)
            {
                if (raw < 0) continue;
                int o = raw * scale;
                if (o >= storage.Length) { bad++; continue; }
                if (o <= last) { bad++; continue; }
                last = o;
                byte version = storage[o];
                if (version >= 1 && version <= 5) good++; else bad++;
            }
            if (good == 0 || bad > good) return 0;
            return good;
        }

        private static int[] ToInt16Array(byte[] data)
        {
            var arr = new int[data.Length / 2];
            for (int i = 0; i < arr.Length; i++) arr[i] = BitConverter.ToInt16(data, i * 2);
            return arr;
        }

        private static void ReadRow(TableModel table, int rowIndex, TileRow row, Dictionary<uint, string> strings)
        {
            var offsets = row.Offsets;
            for (int col = 0; col < offsets.Length; col++)
            {
                int start = offsets[col];
                if (start < 0 || start >= row.Storage.Length) continue;

                int end = row.Storage.Length;
                for (int j = col + 1; j < offsets.Length; j++)
                {
                    if (offsets[j] >= 0 && offsets[j] > start) { end = Math.Min(end, offsets[j]); break; }
                }

                var cell = DecodeCell(row.Storage, start, end, strings);
                if (cell != null) table.Set(rowIndex, col, cell);
            }
        }

        /// <summary>
        /// セル格納バッファのレイアウトは非公開のため、
        /// 「文字列テーブルのキーと一致する uint32」「妥当な decimal128 / double」を
        /// 走査して値を推定する。
        /// </summary>
        private static CellModel DecodeCell(byte[] buf, int start, int end, Dictionary<uint, string> strings)
        {
            int len = end - start;
            if (len < 4) return null;

            byte type = buf[start + 1];
            bool preferText = (type == 2 || type == 3 || type == 7 || type == 9);
            bool preferDate = (type == 5);

            string text = FindString(buf, start, end, strings);
            double num;
            bool hasNum = FindNumber(buf, start, end, out num);

            if (preferText && text != null) return new CellModel { Text = text };

            if (preferDate && hasNum)
            {
                var d = TryDate(num);
                if (d != null) return new CellModel { Date = d };
            }

            if (hasNum && (text == null || !preferText)) return new CellModel { Number = num };
            if (text != null) return new CellModel { Text = text };
            return null;
        }

        private static DateTime? TryDate(double seconds)
        {
            if (seconds < -3.2e9 || seconds > 6.4e9) return null;
            try { return Epoch.AddSeconds(seconds); }
            catch { return null; }
        }

        private static string FindString(byte[] buf, int start, int end, Dictionary<uint, string> strings)
        {
            if (strings.Count == 0) return null;
            for (int p = start + 4; p + 4 <= end; p += 4)
            {
                uint key = BitConverter.ToUInt32(buf, p);
                if (key == 0U || key > 0x00FFFFFFU) continue;
                string s;
                if (strings.TryGetValue(key, out s)) return s;
            }
            return null;
        }

        private static bool FindNumber(byte[] buf, int start, int end, out double value)
        {
            value = 0.0;
            for (int p = start + 4; p + 16 <= end; p += 4)
            {
                double d;
                if (Decimal128.TryRead(buf, p, out d)) { value = d; return true; }
            }
            for (int p = start + 4; p + 8 <= end; p += 4)
            {
                double d = BitConverter.ToDouble(buf, p);
                if (double.IsNaN(d) || double.IsInfinity(d)) continue;
                if (d == 0.0) continue;
                if (Math.Abs(d) < 1e-9 || Math.Abs(d) > 1e15) continue;
                value = d;
                return true;
            }
            return false;
        }
    }
}
