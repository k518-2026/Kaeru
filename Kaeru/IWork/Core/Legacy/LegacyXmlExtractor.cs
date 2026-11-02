using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using IWorkConverter.Core.Model;

namespace IWorkConverter.Core.Legacy
{
    /// <summary>
    /// iWork '09 以前の index.xml 形式からの抽出。
    /// 名前空間は無視してローカル名で照合する。
    /// </summary>
    public static class LegacyXmlExtractor
    {
        private static string Local(XElement e) { return e.Name.LocalName; }

        private static IEnumerable<XElement> ByLocal(XElement root, string name)
        {
            return root.Descendants().Where(e => string.Equals(Local(e), name, StringComparison.OrdinalIgnoreCase));
        }

        private static string ParagraphText(XElement p)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var node in p.DescendantNodes())
            {
                var t = node as XText;
                if (t != null) { sb.Append(t.Value); continue; }
                var el = node as XElement;
                if (el != null && (Local(el) == "br" || Local(el) == "lnbr")) sb.Append('\n');
            }
            return sb.ToString().Replace("\r", string.Empty);
        }

        public static DocModel ExtractPages(string xml, Action<string> note)
        {
            var doc = new DocModel();
            var root = XDocument.Parse(xml).Root;
            if (root == null) return doc;

            foreach (var p in ByLocal(root, "p"))
            {
                string style = (string)p.Attributes().FirstOrDefault(a => a.Name.LocalName == "style");
                string text = ParagraphText(p);
                foreach (var line in text.Split('\n'))
                {
                    if (line.Trim().Length == 0) continue;
                    doc.Paragraphs.Add(new DocParagraph { Text = line, Kind = ClassifyStyle(style) });
                }
            }

            if (doc.Paragraphs.Count == 0 && note != null) note("index.xml から段落を取得できませんでした。");
            var head = doc.Paragraphs.FirstOrDefault(x => x.Kind == ParaKind.Title || x.Kind == ParaKind.Heading1);
            if (head != null) doc.Title = head.Text;
            return doc;
        }

        private static ParaKind ClassifyStyle(string style)
        {
            if (string.IsNullOrEmpty(style)) return ParaKind.Body;
            string s = style.ToLowerInvariant();
            if (s.Contains("title")) return ParaKind.Title;
            if (s.Contains("caption")) return ParaKind.Caption;
            if (s.Contains("heading") || s.Contains("header"))
            {
                if (s.Contains("1")) return ParaKind.Heading1;
                if (s.Contains("2")) return ParaKind.Heading2;
                if (s.Contains("3")) return ParaKind.Heading3;
                return ParaKind.Heading1;
            }
            if (s.Contains("bullet")) return ParaKind.Bullet;
            return ParaKind.Body;
        }

        public static DeckModel ExtractKeynote(string xml, ConversionOptions opt, Action<string> note)
        {
            var deck = new DeckModel();
            var root = XDocument.Parse(xml).Root;
            if (root == null) return deck;

            var slides = ByLocal(root, "slide").ToList();
            if (slides.Count == 0) slides = ByLocal(root, "slide-list").SelectMany(s => s.Elements()).ToList();

            foreach (var slideEl in slides)
            {
                var slide = new SlideModel();
                var lines = new List<string>();
                foreach (var p in ByLocal(slideEl, "p"))
                {
                    foreach (var line in ParagraphText(p).Split('\n'))
                    {
                        string t = line.Trim().TrimStart('•', '・', '-', '*', '\t', ' ');
                        if (t.Length > 0) lines.Add(t);
                    }
                }
                if (lines.Count > 0)
                {
                    slide.Title = lines[0];
                    for (int i = 1; i < lines.Count; i++) slide.Bullets.Add(lines[i]);
                }
                if (opt.SkipEmptySlides && lines.Count == 0) continue;
                deck.Slides.Add(slide);
            }

            if (deck.Slides.Count == 0 && note != null) note("index.xml からスライドを取得できませんでした。");
            return deck;
        }

        public static BookModel ExtractNumbers(string xml, Action<string> note)
        {
            var book = new BookModel();
            var root = XDocument.Parse(xml).Root;
            if (root == null) return book;

            int index = 1;
            foreach (var model in ByLocal(root, "tabular-model"))
            {
                var table = new TableModel();
                table.Name = (string)model.Attributes().FirstOrDefault(a => a.Name.LocalName == "name") ?? ("表" + index);

                var grid = model.Descendants().FirstOrDefault(e => Local(e) == "grid");
                if (grid == null) { index++; continue; }

                int rowIndex = 0;
                foreach (var rowEl in grid.Descendants().Where(e => Local(e) == "r"))
                {
                    int colIndex = 0;
                    foreach (var cell in rowEl.Elements())
                    {
                        string ln = Local(cell);
                        if (ln == "s") { colIndex++; continue; } // 空セル
                        var cm = new CellModel();
                        string v = (string)cell.Attributes().FirstOrDefault(a => a.Name.LocalName == "v");
                        if (ln == "n" && v != null)
                        {
                            double d;
                            if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) cm.Number = d;
                        }
                        else
                        {
                            string text = string.Concat(cell.DescendantNodes().OfType<XText>().Select(t => t.Value));
                            if (!string.IsNullOrWhiteSpace(text)) cm.Text = text;
                            else if (v != null) cm.Text = v;
                        }
                        table.Set(rowIndex, colIndex, cm);
                        colIndex++;
                    }
                    rowIndex++;
                }

                if (table.Cells.Count > 0) book.Tables.Add(table);
                index++;
            }

            if (book.Tables.Count == 0 && note != null) note("index.xml から表を取得できませんでした。");
            return book;
        }
    }
}
