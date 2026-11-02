using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PdfToWord.Models;

namespace PdfToWord.Pdf
{
    /// <summary>同じ行に属する文字の集まり。</summary>
    public sealed class TextLine
    {
        public List<Glyph> Glyphs = new List<Glyph>();
        public string Text = "";
        public List<InlineRun> Runs = new List<InlineRun>();
        public double Left, Right, Top, Bottom, Baseline, FontSize;
        public bool AllBold;
        public int PageNumber;
        public int Column;
        public bool IsMarginCandidate;
        public bool SpansColumns;   // 表題や図など、段をまたいで置かれている行

        public bool IsEmpty { get { return string.IsNullOrWhiteSpace(Text); } }
    }

    /// <summary>ページ上の並べ替え対象（行・表・画像）。</summary>
    public sealed class PageItem
    {
        public TextLine Line;
        public TableBlock Table;
        public ImagePlacement Image;
        public double Top;
        public double Bottom;
        public double Left;
        public double Right;
        public int Column;
    }

    /// <summary>文字の座標から、行・段落・見出し・表を組み立てる。</summary>
    public static class LayoutAnalyzer
    {
        public static bool IsCjk(char c)
        {
            return (c >= 0x2E80 && c <= 0x9FFF)
                || (c >= 0xF900 && c <= 0xFAFF)
                || (c >= 0xFF00 && c <= 0xFF60)
                || (c >= 0xFFE0 && c <= 0xFFE6)
                || (c >= 0x3000 && c <= 0x303F);
        }

        /// <summary>文字を行にまとめる。</summary>
        public static List<TextLine> BuildLines(List<Glyph> glyphs)
        {
            var lines = new List<TextLine>();
            if (glyphs == null || glyphs.Count == 0) return lines;

            var sorted = new List<Glyph>(glyphs);
            sorted.Sort(delegate (Glyph a, Glyph b)
            {
                int compare = a.Baseline.CompareTo(b.Baseline);
                if (compare != 0) return compare;
                return a.X0.CompareTo(b.X0);
            });

            var current = new List<Glyph>();
            double reference = sorted[0].Baseline;
            double referenceSize = sorted[0].Size;

            for (int i = 0; i < sorted.Count; i++)
            {
                Glyph glyph = sorted[i];
                double tolerance = Math.Max(1.2, Math.Max(referenceSize, glyph.Size) * 0.32);
                if (current.Count > 0 && Math.Abs(glyph.Baseline - reference) > tolerance)
                {
                    lines.Add(MakeLine(current));
                    current = new List<Glyph>();
                }
                if (current.Count == 0)
                {
                    reference = glyph.Baseline;
                    referenceSize = glyph.Size;
                }
                else
                {
                    reference = (reference * current.Count + glyph.Baseline) / (current.Count + 1);
                    referenceSize = Math.Max(referenceSize, glyph.Size);
                }
                current.Add(glyph);
            }
            if (current.Count > 0) lines.Add(MakeLine(current));

            lines.Sort(delegate (TextLine a, TextLine b)
            {
                int compare = a.Baseline.CompareTo(b.Baseline);
                if (compare != 0) return compare;
                return a.Left.CompareTo(b.Left);
            });
            return lines;
        }

        /// <summary>
        /// 行の中で、小さくて少し上に置かれている文字を上付きとして印を付ける。
        /// 参照番号（例：…である 1, 2．）を Word でも上付きのまま残すため。
        /// </summary>
        private static void MarkRaisedGlyphs(List<Glyph> glyphs)
        {
            if (glyphs.Count < 2) return;

            // 行の中でいちばん多い文字の大きさを、その行の基準にする
            var counts = new Dictionary<double, int>();
            foreach (Glyph glyph in glyphs)
            {
                double key = Math.Round(glyph.Size * 2) / 2;
                int count;
                counts.TryGetValue(key, out count);
                counts[key] = count + 1;
            }
            double bodySize = 0;
            int best = 0;
            foreach (var pair in counts)
            {
                if (pair.Value > best || (pair.Value == best && pair.Key > bodySize))
                {
                    best = pair.Value;
                    bodySize = pair.Key;
                }
            }
            if (bodySize <= 0) return;

            var baselines = new List<double>();
            foreach (Glyph glyph in glyphs)
            {
                if (Math.Abs(Math.Round(glyph.Size * 2) / 2 - bodySize) < 0.01) baselines.Add(glyph.Baseline);
            }
            if (baselines.Count == 0) return;
            baselines.Sort();
            double bodyBaseline = baselines[baselines.Count / 2];

            foreach (Glyph glyph in glyphs)
            {
                if (glyph.Size >= bodySize * 0.86) continue;
                if (bodyBaseline - glyph.Baseline < bodySize * 0.12) continue;
                glyph.Superscript = true;
            }
        }

        /// <summary>文字の並びから、書式ごとの Run と文字列を作る。</summary>
        public static List<InlineRun> BuildRuns(List<Glyph> glyphs, out string plainText)
        {
            var text = new StringBuilder();
            var runs = new List<InlineRun>();
            InlineRun currentRun = null;
            Glyph previous = null;

            foreach (Glyph glyph in glyphs)
            {
                string value = glyph.Text;
                if (previous != null)
                {
                    double gap = glyph.X0 - previous.X1;
                    double size = Math.Max(6, Math.Min(previous.Size, glyph.Size));
                    bool cjkPair = previous.Text.Length > 0 && value.Length > 0
                        && IsCjk(previous.Text[previous.Text.Length - 1]) && IsCjk(value[0]);
                    double threshold = cjkPair ? size * 0.55 : size * 0.22;
                    // 上付きの参照番号の前後には空白を入れない
                    if (glyph.Superscript != previous.Superscript) threshold = double.MaxValue;
                    bool alreadySpaced = value.StartsWith(" ", StringComparison.Ordinal)
                        || (text.Length > 0 && text[text.Length - 1] == ' ');
                    if (gap > threshold && !alreadySpaced)
                    {
                        text.Append(' ');
                        if (currentRun != null) currentRun.Text += " ";
                    }
                }

                if (currentRun == null || currentRun.Bold != glyph.Bold || currentRun.Italic != glyph.Italic
                    || currentRun.Monospace != glyph.Monospace || currentRun.Superscript != glyph.Superscript)
                {
                    currentRun = new InlineRun
                    {
                        Bold = glyph.Bold,
                        Italic = glyph.Italic,
                        Monospace = glyph.Monospace,
                        Superscript = glyph.Superscript
                    };
                    runs.Add(currentRun);
                }
                currentRun.Text += value;
                text.Append(value);
                previous = glyph;
            }

            plainText = text.ToString();
            return runs;
        }

        private static TextLine MakeLine(List<Glyph> glyphs)
        {
            glyphs.Sort(delegate (Glyph a, Glyph b) { return a.X0.CompareTo(b.X0); });

            var line = new TextLine();
            line.Glyphs = glyphs;
            line.Left = double.MaxValue;
            line.Right = double.MinValue;

            var sizes = new List<double>();
            double baselineSum = 0;
            bool allBold = true;

            foreach (Glyph glyph in glyphs)
            {
                line.Left = Math.Min(line.Left, glyph.X0);
                line.Right = Math.Max(line.Right, glyph.X1);
                sizes.Add(glyph.Size);
                baselineSum += glyph.Baseline;
                if (!glyph.Bold) allBold = false;
            }

            MarkRaisedGlyphs(glyphs);

            string text;
            line.Runs = BuildRuns(glyphs, out text);
            line.Text = text;
            line.AllBold = allBold && glyphs.Count > 0;
            line.Baseline = baselineSum / Math.Max(1, glyphs.Count);
            sizes.Sort();
            line.Glyphs = glyphs;
            line.FontSize = sizes.Count > 0 ? sizes[sizes.Count / 2] : 10;
            line.Top = line.Baseline - line.FontSize * 0.85;
            line.Bottom = line.Baseline + line.FontSize * 0.25;
            if (line.Left > line.Right) { line.Left = 0; line.Right = 0; }
            return line;
        }

        /// <summary>段組みの切れ目（縦にのびる余白）。</summary>
        public sealed class ColumnGutter
        {
            public double Left;
            public double Right;
            public double Center { get { return (Left + Right) / 2; } }
        }

        /// <summary>
        /// 段組みの切れ目を探す。表題・抄録・参考文献など段をまたぐ行が混ざっていても
        /// 見つけられるよう、「その帯に文字がある行が全体の何割か」で判断する。
        /// </summary>
        public static ColumnGutter FindGutter(List<Glyph> glyphs, List<RuleLine> rules, double pageWidth)
        {
            if (glyphs == null || glyphs.Count < 150 || pageWidth <= 0) return null;

            const int bins = 300;
            var rowsInBin = new HashSet<int>[bins];
            var allRows = new HashSet<int>();

            foreach (Glyph glyph in glyphs)
            {
                int row = (int)Math.Round(glyph.Baseline / 3);
                allRows.Add(row);

                int first = (int)Math.Floor(glyph.X0 / pageWidth * bins);
                int last = (int)Math.Ceiling(glyph.X1 / pageWidth * bins);
                first = Math.Max(0, Math.Min(bins - 1, first));
                last = Math.Max(first + 1, Math.Min(bins, last));
                for (int i = first; i < last; i++)
                {
                    if (rowsInBin[i] == null) rowsInBin[i] = new HashSet<int>();
                    rowsInBin[i].Add(row);
                }
            }
            if (allRows.Count < 12) return null;

            double limit = allRows.Count * 0.45;
            int low = (int)(bins * 0.30), high = (int)(bins * 0.70);
            int bestStart = -1, bestLength = 0;
            int runStart = -1;

            for (int i = low; i <= high && i < bins; i++)
            {
                int count = rowsInBin[i] == null ? 0 : rowsInBin[i].Count;
                if (count <= limit)
                {
                    if (runStart < 0) runStart = i;
                    if (i - runStart + 1 > bestLength) { bestLength = i - runStart + 1; bestStart = runStart; }
                }
                else runStart = -1;
            }
            if (bestStart < 0 || bestLength < bins * 0.025) return null;

            // 帯の中と外で、行の数がはっきり違うことを確かめる（段の間の余白かどうか）
            int inside = 0;
            for (int i = bestStart; i < bestStart + bestLength; i++)
                inside = Math.Max(inside, rowsInBin[i] == null ? 0 : rowsInBin[i].Count);
            int outsideLeft = RowsAt(rowsInBin, bestStart - 4);
            int outsideRight = RowsAt(rowsInBin, bestStart + bestLength + 3);
            int outside = Math.Min(outsideLeft, outsideRight);
            if (outside < 8 || inside > outside * 0.6) return null;

            var gutter = new ColumnGutter();
            gutter.Left = bestStart / (double)bins * pageWidth;
            gutter.Right = (bestStart + bestLength) / (double)bins * pageWidth;

            // 帯の中に長い縦罫線があるときは、段組みではなく表とみなす
            if (rules != null)
            {
                foreach (RuleLine rule in rules)
                {
                    if (rule.IsHorizontal) continue;
                    if (rule.X0 < gutter.Left - 2 || rule.X0 > gutter.Right + 2) continue;
                    if (Math.Abs(rule.Y1 - rule.Y0) > 12) return null;
                }
            }

            int left = 0, right = 0;
            var leftRows = new HashSet<int>();
            var rightRows = new HashSet<int>();
            foreach (Glyph glyph in glyphs)
            {
                int row = (int)Math.Round(glyph.Baseline / 3);
                if (glyph.X1 <= gutter.Left) { left++; leftRows.Add(row); }
                else if (glyph.X0 >= gutter.Right) { right++; rightRows.Add(row); }
            }
            if (left < glyphs.Count * 0.15 || right < glyphs.Count * 0.15) return null;
            if (leftRows.Count < 8 || rightRows.Count < 8) return null;

            int shared = 0;
            foreach (int row in leftRows) if (rightRows.Contains(row)) shared++;
            if (shared < Math.Min(leftRows.Count, rightRows.Count) * 0.4) return null;

            return gutter;
        }

        private static int RowsAt(HashSet<int>[] rowsInBin, int index)
        {
            if (index < 0 || index >= rowsInBin.Length) return 0;
            return rowsInBin[index] == null ? 0 : rowsInBin[index].Count;
        }

        /// <summary>
        /// 段の切れ目をまたがないように行を組み立てる。
        /// 表題や図の見出しのように帯の中にも文字がある行は、1 行としてつなぎ直す。
        /// </summary>
        public static List<TextLine> BuildLines(List<Glyph> glyphs, ColumnGutter gutter)
        {
            if (gutter == null) return BuildLines(glyphs);

            double center = gutter.Center;
            var leftGlyphs = new List<Glyph>();
            var rightGlyphs = new List<Glyph>();
            var crossing = new List<Glyph>();
            foreach (Glyph glyph in glyphs)
            {
                // 帯の中心を突き抜けている文字だけを「段をまたぐ文字」とみなす。
                // 段の端から少しはみ出しただけの括弧などで、左右がつながってしまうのを防ぐ。
                if (glyph.X1 <= center) leftGlyphs.Add(glyph);
                else if (glyph.X0 >= center) rightGlyphs.Add(glyph);
                else crossing.Add(glyph);
            }

            List<TextLine> leftLines = BuildLines(leftGlyphs);
            foreach (TextLine line in leftLines) line.Column = 0;
            List<TextLine> rightLines = BuildLines(rightGlyphs);
            foreach (TextLine line in rightLines) line.Column = 1;

            var result = new List<TextLine>();
            result.AddRange(leftLines);
            result.AddRange(rightLines);

            // 帯の中の文字は、同じ高さの左右の行と合わせて 1 行に戻す
            foreach (TextLine bridge in BuildLines(crossing))
            {
                var merged = new List<Glyph>(bridge.Glyphs);
                double tolerance = Math.Max(2.0, bridge.FontSize * 0.4);

                TextLine leftPart = NearestLine(leftLines, bridge.Baseline, tolerance);
                TextLine rightPart = NearestLine(rightLines, bridge.Baseline, tolerance);
                if (leftPart != null) { merged.AddRange(leftPart.Glyphs); result.Remove(leftPart); }
                if (rightPart != null) { merged.AddRange(rightPart.Glyphs); result.Remove(rightPart); }

                TextLine line = MakeLine(merged);
                line.SpansColumns = true;
                line.Column = 0;
                result.Add(line);
            }

            // 帯の真ん中に空白がある表題などは、左右の切れ端が帯へ入り込んでいるかで判断する
            double reach = (gutter.Right - gutter.Left) * 0.3;   // 帯へ十分に入り込んでいること
            var pending = new List<TextLine>(result);
            foreach (TextLine leftLine in pending)
            {
                if (leftLine.Column != 0 || leftLine.SpansColumns) continue;
                if (leftLine.Right <= gutter.Left + reach) continue;
                if (!result.Contains(leftLine)) continue;

                double tolerance = Math.Max(2.0, leftLine.FontSize * 0.4);
                TextLine partner = null;
                foreach (TextLine rightLine in result)
                {
                    if (rightLine.Column != 1 || rightLine.SpansColumns) continue;
                    if (rightLine.Left >= gutter.Right - reach) continue;
                    if (Math.Abs(rightLine.Baseline - leftLine.Baseline) > tolerance) continue;
                    partner = rightLine;
                    break;
                }
                if (partner == null) continue;

                var merged = new List<Glyph>(leftLine.Glyphs);
                merged.AddRange(partner.Glyphs);
                result.Remove(leftLine);
                result.Remove(partner);
                TextLine line = MakeLine(merged);
                line.SpansColumns = true;
                line.Column = 0;
                result.Add(line);
            }

            result.Sort(delegate (TextLine a, TextLine b)
            {
                int compare = a.Baseline.CompareTo(b.Baseline);
                if (compare != 0) return compare;
                return a.Left.CompareTo(b.Left);
            });
            return result;
        }

        private static TextLine NearestLine(List<TextLine> lines, double baseline, double tolerance)
        {
            TextLine best = null;
            double bestDistance = tolerance;
            foreach (TextLine line in lines)
            {
                double distance = Math.Abs(line.Baseline - baseline);
                if (distance <= bestDistance) { bestDistance = distance; best = line; }
            }
            return best;
        }

        /// <summary>
        /// 参照番号などの上付き文字を、すぐ下の行に取り込む。
        /// 小さな文字が少し上に置かれているだけで別の行になってしまうのを防ぐ。
        /// </summary>
        public static List<TextLine> MergeSuperscripts(List<TextLine> lines)
        {
            if (lines == null || lines.Count < 2) return lines;

            var removed = new HashSet<TextLine>();
            var rebuilt = new Dictionary<TextLine, List<Glyph>>();

            for (int i = 0; i < lines.Count; i++)
            {
                TextLine small = lines[i];
                if (removed.Contains(small) || small.IsEmpty) continue;
                if (small.Glyphs.Count == 0 || small.Glyphs.Count > 8) continue;

                TextLine host = null;
                for (int j = 0; j < lines.Count; j++)
                {
                    if (i == j) continue;
                    TextLine candidate = lines[j];
                    if (removed.Contains(candidate) || candidate.IsEmpty) continue;
                    if (candidate.Column != small.Column) continue;
                    if (small.FontSize > candidate.FontSize * 0.82) continue;

                    double rise = candidate.Baseline - small.Baseline;         // 上付きは少し上にある
                    if (rise <= 0 || rise > candidate.FontSize * 0.6) continue;
                    if (small.Left < candidate.Left - candidate.FontSize
                        || small.Left > candidate.Right + candidate.FontSize * 1.5) continue;

                    host = candidate;
                    break;
                }
                if (host == null) continue;

                foreach (Glyph glyph in small.Glyphs) glyph.Superscript = true;
                if (!rebuilt.ContainsKey(host)) rebuilt[host] = new List<Glyph>(host.Glyphs);
                rebuilt[host].AddRange(small.Glyphs);
                removed.Add(small);
            }

            if (removed.Count == 0) return lines;

            var result = new List<TextLine>();
            foreach (TextLine line in lines)
            {
                if (removed.Contains(line)) continue;
                if (rebuilt.ContainsKey(line))
                {
                    TextLine merged = MakeLine(rebuilt[line]);
                    merged.Column = line.Column;
                    merged.SpansColumns = line.SpansColumns;
                    merged.PageNumber = line.PageNumber;
                    result.Add(merged);
                }
                else result.Add(line);
            }
            result.Sort(delegate (TextLine a, TextLine b)
            {
                int compare = a.Baseline.CompareTo(b.Baseline);
                if (compare != 0) return compare;
                return a.Left.CompareTo(b.Left);
            });
            return result;
        }

        /// <summary>
        /// ページ上端・下端にあって、本文から離れて置かれている行に印を付ける。
        /// 本文の 1 行目をヘッダーと誤判定しないように、行間の広さも条件に入れる。
        /// </summary>
        public static void MarkMarginCandidates(List<TextLine> lines, double pageHeight)
        {
            if (pageHeight <= 0) return;

            // 段組みの場合、左右の柱が同じ高さに並ぶので、段ごとに調べる
            var byColumn = new Dictionary<int, List<TextLine>>();
            foreach (TextLine line in lines)
            {
                List<TextLine> bucket;
                int key = line.SpansColumns ? -1 : line.Column;
                if (!byColumn.TryGetValue(key, out bucket))
                {
                    bucket = new List<TextLine>();
                    byColumn[key] = bucket;
                }
                bucket.Add(line);
            }
            if (byColumn.Count > 1)
            {
                foreach (var pair in byColumn) MarkMarginCandidatesInColumn(pair.Value, pageHeight);
                return;
            }

            MarkMarginCandidatesInColumn(lines, pageHeight);
        }

        private static void MarkMarginCandidatesInColumn(List<TextLine> lines, double pageHeight)
        {
            var sorted = new List<TextLine>(lines);
            sorted.Sort(delegate (TextLine a, TextLine b) { return a.Baseline.CompareTo(b.Baseline); });

            for (int i = 0; i < sorted.Count; i++)
            {
                TextLine line = sorted[i];
                line.IsMarginCandidate = false;
                if (line.IsEmpty) continue;

                bool nearTop = line.Top < pageHeight * 0.10;
                bool nearBottom = line.Bottom > pageHeight * 0.90;
                if (!nearTop && !nearBottom) continue;

                double separation = Math.Max(12.0, line.FontSize * 2.0);
                double sameLine = Math.Max(2.0, line.FontSize * 0.5);
                TextLine neighbor = null;
                if (nearTop)
                {
                    // 同じ高さに並ぶ行（左右に分かれた柱など）は隣とみなさない
                    for (int j = i + 1; j < sorted.Count; j++)
                    {
                        if (sorted[j].Baseline - line.Baseline < sameLine) continue;
                        neighbor = sorted[j];
                        break;
                    }
                }
                else
                {
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (line.Baseline - sorted[j].Baseline < sameLine) continue;
                        neighbor = sorted[j];
                        break;
                    }
                }
                if (neighbor != null && Math.Abs(neighbor.Baseline - line.Baseline) < separation) continue;
                line.IsMarginCandidate = true;
            }
        }

        /// <summary>ヘッダー・フッターとして繰り返される行を判定する。</summary>
        public static HashSet<string> FindRepeatedLines(List<List<TextLine>> pages, double pageHeight)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (pages.Count < 3) return result;

            var counter = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (List<TextLine> lines in pages)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (TextLine line in lines)
                {
                    if (!line.IsMarginCandidate) continue;
                    string key = Normalize(line.Text);
                    if (key.Length == 0 || key.Length > 90) continue;
                    if (seen.Add(key))
                    {
                        int count;
                        counter.TryGetValue(key, out count);
                        counter[key] = count + 1;
                    }
                }
            }

            // 左右のページで柱が入れ替わる体裁もあるので、半数まで下げる
            int threshold = Math.Max(2, (int)Math.Ceiling(pages.Count * 0.3));
            foreach (var pair in counter)
            {
                if (pair.Value >= threshold) result.Add(pair.Key);
            }
            return result;
        }

        public static bool InMargin(TextLine line, double pageHeight)
        {
            if (pageHeight <= 0) return false;
            return line.Top < pageHeight * 0.09 || line.Bottom > pageHeight * 0.91;
        }

        public static string Normalize(string text)
        {
            if (text == null) return "";
            var sb = new StringBuilder();
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) continue;
                if (char.IsDigit(c)) { sb.Append('#'); continue; }
                sb.Append(c);
            }
            string value = sb.ToString();
            while (value.Contains("##")) value = value.Replace("##", "#");
            return value;
        }

        /// <summary>ページ番号らしい行かどうか。</summary>
        public static bool LooksLikePageNumber(string text)
        {
            string trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0 || trimmed.Length > 20) return false;
            var sb = new StringBuilder();
            foreach (char c in trimmed)
            {
                if (char.IsDigit(c)) sb.Append('d');
                else if (c == '-' || c == '\u2013' || c == '/' || c == '.' || c == ' '
                         || c == '\u30FC' || c == '\u2015' || c == '|') sb.Append('s');
                else if (char.IsLetter(c)) sb.Append('a');
                else sb.Append('x');
            }
            string pattern = sb.ToString();
            bool hasDigit = pattern.Contains("d");
            if (!hasDigit) return false;
            foreach (char c in pattern)
            {
                if (c != 'd' && c != 's') return false;
            }
            return true;
        }
    }
}
