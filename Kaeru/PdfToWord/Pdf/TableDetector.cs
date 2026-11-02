using System;
using System.Collections.Generic;
using PdfToWord.Models;

namespace PdfToWord.Pdf
{
    /// <summary>検出した表とその範囲。</summary>
    public sealed class DetectedTable
    {
        public TableBlock Table;
        public double Top, Bottom, Left, Right;
        public List<TextLine> UsedLines = new List<TextLine>();
    }

    /// <summary>罫線から表の格子を復元する。</summary>
    public static class TableDetector
    {
        private const double Tolerance = 3.5;

        public static List<DetectedTable> Detect(List<RuleLine> rules, List<TextLine> lines)
        {
            var result = new List<DetectedTable>();
            if (rules == null || rules.Count < 4 || lines == null) return result;
            if (rules.Count > 6000) return result;

            List<List<RuleLine>> groups = GroupRules(rules);
            foreach (List<RuleLine> group in groups)
            {
                DetectedTable table = BuildTable(group, lines);
                if (table != null) result.Add(table);
            }
            result.Sort(delegate (DetectedTable a, DetectedTable b) { return a.Top.CompareTo(b.Top); });
            return result;
        }

        /// <summary>交差する罫線どうしをまとめる（union-find）。</summary>
        private static List<List<RuleLine>> GroupRules(List<RuleLine> rules)
        {
            int count = rules.Count;
            var parent = new int[count];
            for (int i = 0; i < count; i++) parent[i] = i;

            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    if (Touches(rules[i], rules[j])) Union(parent, i, j);
                }
            }

            var buckets = new Dictionary<int, List<RuleLine>>();
            for (int i = 0; i < count; i++)
            {
                int root = Find(parent, i);
                List<RuleLine> bucket;
                if (!buckets.TryGetValue(root, out bucket))
                {
                    bucket = new List<RuleLine>();
                    buckets[root] = bucket;
                }
                bucket.Add(rules[i]);
            }

            var groups = new List<List<RuleLine>>();
            foreach (var pair in buckets)
            {
                if (pair.Value.Count >= 4) groups.Add(pair.Value);
            }
            return groups;
        }

        private static bool Touches(RuleLine a, RuleLine b)
        {
            double margin = 4.0;
            return a.X0 - margin <= b.X1 && b.X0 - margin <= a.X1
                && a.Y0 - margin <= b.Y1 && b.Y0 - margin <= a.Y1;
        }

        private static int Find(int[] parent, int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }
            return index;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int rootA = Find(parent, a), rootB = Find(parent, b);
            if (rootA != rootB) parent[rootB] = rootA;
        }

        private static DetectedTable BuildTable(List<RuleLine> group, List<TextLine> lines)
        {
            var horizontal = new List<RuleLine>();
            var vertical = new List<RuleLine>();
            foreach (RuleLine rule in group)
            {
                if (rule.IsHorizontal) horizontal.Add(rule); else vertical.Add(rule);
            }
            if (horizontal.Count < 2 || vertical.Count < 2) return null;

            List<double> rowPositions = Cluster(YValues(horizontal));
            List<double> columnPositions = Cluster(XValues(vertical));
            if (rowPositions.Count < 2 || columnPositions.Count < 2) return null;

            int rowCount = rowPositions.Count - 1;
            int columnCount = columnPositions.Count - 1;
            if (rowCount < 1 || columnCount < 1) return null;
            if (rowCount < 2 && columnCount < 2) return null;
            if (rowCount * columnCount > 600) return null;

            double left = columnPositions[0];
            double right = columnPositions[columnPositions.Count - 1];
            double top = rowPositions[0];
            double bottom = rowPositions[rowPositions.Count - 1];
            if (right - left < 30 || bottom - top < 12) return null;

            // 行をまたぐセルもあるので、文字ごとにセルへ割り当てる
            var cellGlyphs = new List<List<Glyph>>[rowCount, columnCount];
            var used = new List<TextLine>();
            foreach (TextLine line in lines)
            {
                if (line.IsEmpty) continue;
                double centerY = (line.Top + line.Bottom) / 2;
                if (centerY < top - 2 || centerY > bottom + 2) continue;
                if (line.Right < left - 2 || line.Left > right + 2) continue;

                int row = IndexOfBand(rowPositions, centerY);
                if (row < 0) continue;

                bool usedAny = false;
                var byColumn = new Dictionary<int, List<Glyph>>();
                foreach (Glyph glyph in line.Glyphs)
                {
                    double glyphCenter = (glyph.X0 + glyph.X1) / 2;
                    int column = IndexOfBand(columnPositions, glyphCenter);
                    if (column < 0) continue;
                    List<Glyph> bucket;
                    if (!byColumn.TryGetValue(column, out bucket))
                    {
                        bucket = new List<Glyph>();
                        byColumn[column] = bucket;
                    }
                    bucket.Add(glyph);
                    usedAny = true;
                }
                foreach (var pair in byColumn)
                {
                    if (cellGlyphs[row, pair.Key] == null) cellGlyphs[row, pair.Key] = new List<List<Glyph>>();
                    cellGlyphs[row, pair.Key].Add(pair.Value);
                }
                if (usedAny) used.Add(line);
            }
            if (used.Count == 0) return null;

            var cellText = new List<InlineRun>[rowCount, columnCount];
            for (int r = 0; r < rowCount; r++)
            {
                for (int c = 0; c < columnCount; c++)
                {
                    List<List<Glyph>> groups = cellGlyphs[r, c];
                    if (groups == null) continue;
                    var runs = new List<InlineRun>();
                    foreach (List<Glyph> part in groups)
                    {
                        string plain;
                        List<InlineRun> built = LayoutAnalyzer.BuildRuns(part, out plain);
                        if (string.IsNullOrWhiteSpace(plain)) continue;
                        if (runs.Count > 0) runs.Add(new InlineRun(" "));
                        foreach (InlineRun run in built) runs.Add(run.Clone());
                    }
                    if (runs.Count > 0) cellText[r, c] = runs;
                }
            }

            var table = new TableBlock();
            for (int c = 0; c < columnCount; c++)
                table.ColumnWidths.Add(columnPositions[c + 1] - columnPositions[c]);

            for (int r = 0; r < rowCount; r++)
            {
                var row = new TableRow();
                int c = 0;
                while (c < columnCount)
                {
                    var cell = new TableCell();
                    if (cellText[r, c] != null) cell.Runs.AddRange(cellText[r, c]);

                    int span = 1;
                    while (c + span < columnCount &&
                           !HasVerticalRule(vertical, columnPositions[c + span], rowPositions[r], rowPositions[r + 1]))
                    {
                        if (cellText[r, c + span] != null)
                        {
                            if (cell.Runs.Count > 0) cell.Runs.Add(new InlineRun(" "));
                            cell.Runs.AddRange(cellText[r, c + span]);
                        }
                        span++;
                    }
                    cell.ColumnSpan = span;
                    row.Cells.Add(cell);
                    c += span;
                }
                table.Rows.Add(row);
            }

            table.HasHeaderRow = LooksLikeHeaderRow(table);

            bool hasText = false;
            foreach (TableRow row in table.Rows)
            {
                foreach (TableCell cell in row.Cells)
                {
                    if (!string.IsNullOrWhiteSpace(cell.PlainText)) { hasText = true; break; }
                }
                if (hasText) break;
            }
            if (!hasText) return null;

            return new DetectedTable
            {
                Table = table,
                Top = top,
                Bottom = bottom,
                Left = left,
                Right = right,
                UsedLines = used
            };
        }

        /// <summary>先頭行が見出し行らしいか（短い語が並んでいるか）。</summary>
        private static bool LooksLikeHeaderRow(TableBlock table)
        {
            if (table.Rows.Count < 2) return false;
            TableRow first = table.Rows[0];
            if (first.Cells.Count < 2) return false;
            int filled = 0;
            foreach (TableCell cell in first.Cells)
            {
                string value = cell.PlainText.Trim();
                if (value.Length == 0) continue;
                if (value.Length > 24) return false;
                if (value.EndsWith("。", StringComparison.Ordinal)) return false;
                filled++;
            }
            return filled >= Math.Max(2, first.Cells.Count - 1);
        }

        private static bool HasVerticalRule(List<RuleLine> vertical, double x, double top, double bottom)
        {
            double height = bottom - top;
            if (height <= 0) return true;
            foreach (RuleLine rule in vertical)
            {
                if (Math.Abs(rule.X0 - x) > Tolerance) continue;
                double overlap = Math.Min(rule.Y1, bottom) - Math.Max(rule.Y0, top);
                if (overlap >= height * 0.55) return true;
            }
            return false;
        }

        private static int IndexOfBand(List<double> positions, double value)
        {
            for (int i = 0; i + 1 < positions.Count; i++)
            {
                if (value >= positions[i] - 2 && value <= positions[i + 1] + 2) return i;
            }
            return -1;
        }

        private static List<double> YValues(List<RuleLine> rules)
        {
            var values = new List<double>();
            foreach (RuleLine rule in rules) values.Add((rule.Y0 + rule.Y1) / 2);
            return values;
        }

        private static List<double> XValues(List<RuleLine> rules)
        {
            var values = new List<double>();
            foreach (RuleLine rule in rules) values.Add((rule.X0 + rule.X1) / 2);
            return values;
        }

        private static List<double> Cluster(List<double> values)
        {
            values.Sort();
            var result = new List<double>();
            int index = 0;
            while (index < values.Count)
            {
                double sum = values[index];
                int count = 1;
                int next = index + 1;
                while (next < values.Count && values[next] - values[index] <= Tolerance)
                {
                    sum += values[next];
                    count++;
                    next++;
                }
                result.Add(sum / count);
                index = next;
            }
            return result;
        }
    }
}
