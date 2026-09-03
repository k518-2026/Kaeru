using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Kaeru.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Kaeru.Services
{
    /// <summary>
    /// PDF からテキストを抽出し、レイアウト情報（座標・文字サイズ）をもとに
    /// 見出し・段落・箇条書きを推定して中間モデルに変換する。
    /// </summary>
    public class PdfReader
    {
        private static readonly Regex BulletRx =
            new Regex(@"^\s*([-–—•・*‣]|[0-9]{1,2}[.)]|\([0-9]{1,2}\)|[０-９]{1,2}[．)])\s+", RegexOptions.Compiled);
        private static readonly Regex PageNumberRx =
            new Regex(@"^\s*[-–—]?\s*(?:page\s*)?[0-9ivxlcIVXLC]{1,6}\s*[-–—]?\s*(?:/\s*[0-9]{1,6})?\s*$",
                      RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>ページ番号やヘッダー・フッターらしい行を除去する。</summary>
        public bool StripRunningHeads { get; set; } = true;

        private class Line
        {
            public string Text;
            public double Top;      // ページ上端からの距離（大きいほど下）
            public double Left;
            public double Right;
            public double FontSize;
            public int Page;
        }

        public DocumentModel Read(string path)
        {
            var model = new DocumentModel
            {
                SourcePath = path,
                Title = Path.GetFileNameWithoutExtension(path)
            };

            var lines = new List<Line>();

            using (var pdf = PdfDocument.Open(path))
            {
                try
                {
                    var t = pdf.Information?.Title;
                    if (!string.IsNullOrWhiteSpace(t)) model.Title = t;
                }
                catch { }

                int pageNo = 0;
                foreach (var page in pdf.GetPages())
                {
                    pageNo++;
                    lines.AddRange(ExtractLines(page, pageNo));
                }
            }

            if (StripRunningHeads) lines = RemoveRunningHeads(lines);
            if (lines.Count == 0) return model;

            double bodySize = MedianFontSize(lines);
            var headingSizes = lines
                .Where(l => l.FontSize > bodySize * 1.12)
                .Select(l => Math.Round(l.FontSize, 1))
                .Distinct()
                .OrderByDescending(s => s)
                .Take(4)
                .ToList();

            BuildBlocks(model, lines, bodySize, headingSizes);
            return model;
        }

        // ---------- 行の抽出 ----------

        private static List<Line> ExtractLines(Page page, int pageNo)
        {
            var result = new List<Line>();
            List<Word> words;
            try
            {
                words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            }
            catch
            {
                return result;
            }
            if (words.Count == 0) return result;

            double avgHeight = words.Average(w => Math.Abs(w.BoundingBox.Height));
            if (avgHeight <= 0) avgHeight = 10;
            double tolerance = avgHeight * 0.5;

            // ベースライン（下端）でグルーピング。PDF の座標は下が 0 なので降順に並べる。
            var ordered = words.OrderByDescending(w => w.BoundingBox.Bottom).ToList();
            var current = new List<Word>();
            double currentY = ordered[0].BoundingBox.Bottom;

            foreach (var w in ordered)
            {
                if (current.Count > 0 && Math.Abs(w.BoundingBox.Bottom - currentY) > tolerance)
                {
                    result.Add(MakeLine(current, page, pageNo));
                    current = new List<Word>();
                }
                if (current.Count == 0) currentY = w.BoundingBox.Bottom;
                current.Add(w);
            }
            if (current.Count > 0) result.Add(MakeLine(current, page, pageNo));

            return result.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        }

        private static Line MakeLine(List<Word> words, Page page, int pageNo)
        {
            var sorted = words.OrderBy(w => w.BoundingBox.Left).ToList();
            var sb = new StringBuilder();
            foreach (var w in sorted)
            {
                if (sb.Length > 0)
                {
                    char prev = sb[sb.Length - 1];
                    char next = w.Text.Length > 0 ? w.Text[0] : ' ';
                    if (!(IsCjk(prev) && IsCjk(next))) sb.Append(' ');
                }
                sb.Append(w.Text);
            }

            double size = 0;
            var letters = sorted.SelectMany(w => w.Letters).ToList();
            if (letters.Count > 0) size = letters.Average(l => l.PointSize);
            if (size <= 0) size = sorted.Average(w => Math.Abs(w.BoundingBox.Height));

            return new Line
            {
                Text = sb.ToString().Trim(),
                Top = page.Height - sorted.Max(w => w.BoundingBox.Top),
                Left = sorted.Min(w => w.BoundingBox.Left),
                Right = sorted.Max(w => w.BoundingBox.Right),
                FontSize = size,
                Page = pageNo
            };
        }

        // ---------- ヘッダー・フッター除去 ----------

        private static List<Line> RemoveRunningHeads(List<Line> lines)
        {
            int pageCount = lines.Count == 0 ? 0 : lines.Max(l => l.Page);
            if (pageCount < 3) return lines.Where(l => !PageNumberRx.IsMatch(l.Text)).ToList();

            // 3 ページ以上で同じ位置に繰り返し現れる短い行はヘッダー・フッターとみなす
            var repeated = lines
                .Where(l => l.Text.Length <= 60)
                .GroupBy(l => l.Text.Trim())
                .Where(g => g.Select(x => x.Page).Distinct().Count() >= Math.Max(3, pageCount / 2))
                .Select(g => g.Key)
                .ToHashSet();

            return lines
                .Where(l => !repeated.Contains(l.Text.Trim()))
                .Where(l => !PageNumberRx.IsMatch(l.Text))
                .ToList();
        }

        // ---------- ブロック組み立て ----------

        private static void BuildBlocks(DocumentModel model, List<Line> lines, double bodySize, List<double> headingSizes)
        {
            double pageRight = lines.Max(l => l.Right);
            double avgLineHeight = bodySize * 1.6;

            Block current = null;
            Line prev = null;

            foreach (var line in lines)
            {
                int headingLevel = HeadingLevelOf(line, bodySize, headingSizes);
                bool isBullet = BulletRx.IsMatch(line.Text);

                bool startNew =
                    current == null ||
                    headingLevel > 0 ||
                    isBullet ||
                    prev == null ||
                    prev.Page != line.Page ||
                    current.Kind != BlockKind.Paragraph ||
                    line.Top - prev.Top > avgLineHeight * 1.7 ||
                    prev.Right < pageRight - bodySize * 2.5 ||
                    line.Left > prev.Left + bodySize * 1.2 ||
                    EndsSentence(prev.Text);

                if (startNew)
                {
                    if (current != null) model.Blocks.Add(current);

                    if (headingLevel > 0)
                    {
                        current = Block.Text(BlockKind.Heading, line.Text);
                        current.Level = headingLevel;
                    }
                    else if (isBullet)
                    {
                        var m = BulletRx.Match(line.Text);
                        string marker = m.Groups[1].Value;
                        bool ordered = char.IsDigit(marker[0]) || (marker.Length > 0 && marker[0] >= '０' && marker[0] <= '９');
                        current = Block.Text(BlockKind.ListItem, line.Text.Substring(m.Length).Trim());
                        current.Ordered = ordered;
                        current.Level = 0;
                    }
                    else
                    {
                        current = Block.Text(BlockKind.Paragraph, line.Text);
                    }
                }
                else
                {
                    AppendToBlock(current, line.Text);
                }

                prev = line;
            }

            if (current != null) model.Blocks.Add(current);

            // 見出しやリストが 1 行で終わるように、末尾の空白を整理
            foreach (var b in model.Blocks)
                foreach (var r in b.Runs)
                    r.Text = r.Text.Trim();

            model.Blocks = model.Blocks.Where(b => !b.IsEmpty).ToList();
        }

        private static void AppendToBlock(Block block, string text)
        {
            var run = block.Runs[block.Runs.Count - 1];
            string prevText = run.Text.TrimEnd();

            if (prevText.EndsWith("-") && prevText.Length > 1 && char.IsLetter(prevText[prevText.Length - 2]))
            {
                run.Text = prevText.Substring(0, prevText.Length - 1) + text.TrimStart();
                return;
            }

            char last = prevText.Length > 0 ? prevText[prevText.Length - 1] : ' ';
            char next = text.Length > 0 ? text[0] : ' ';
            string sep = (IsCjk(last) && IsCjk(next)) ? "" : " ";
            run.Text = prevText + sep + text.TrimStart();
        }

        private static int HeadingLevelOf(Line line, double bodySize, List<double> headingSizes)
        {
            if (line.Text.Length > 80) return 0;
            double size = Math.Round(line.FontSize, 1);
            if (size <= bodySize * 1.12) return 0;

            int idx = headingSizes.FindIndex(s => Math.Abs(s - size) < 0.15);
            if (idx < 0)
            {
                idx = headingSizes.Count(s => s > size);
            }
            return Math.Min(6, idx + 1);
        }

        private static bool EndsSentence(string s)
        {
            s = (s ?? "").TrimEnd();
            if (s.Length == 0) return false;
            char c = s[s.Length - 1];
            return c == '。' || c == '．' || c == '！' || c == '？' || c == '」' || c == '）'
                   || c == '.' || c == '!' || c == '?' || c == ':' || c == ';';
        }

        private static bool IsCjk(char c)
        {
            return (c >= 0x3000 && c <= 0x30ff)     // 句読点・かな
                || (c >= 0x3400 && c <= 0x4dbf)
                || (c >= 0x4e00 && c <= 0x9fff)     // 漢字
                || (c >= 0xf900 && c <= 0xfaff)
                || (c >= 0xff00 && c <= 0xffef);    // 全角記号
        }

        private static double MedianFontSize(List<Line> lines)
        {
            var sizes = lines.Select(l => l.FontSize).Where(s => s > 0).OrderBy(s => s).ToList();
            if (sizes.Count == 0) return 10.5;
            return sizes[sizes.Count / 2];
        }
    }
}
