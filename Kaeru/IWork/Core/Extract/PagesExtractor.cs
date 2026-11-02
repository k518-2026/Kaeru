using System;
using System.Collections.Generic;
using System.Linq;
using IWorkConverter.Core.Iwa;
using IWorkConverter.Core.Model;

namespace IWorkConverter.Core.Extract
{
    public static class PagesExtractor
    {
        public static DocModel Extract(IwaArchive archive, ConversionOptions opt, Action<string> note)
        {
            var doc = new DocModel();
            var storages = TextStorageFinder.Find(archive);
            if (storages.Count == 0)
            {
                if (note != null) note("本文テキストを検出できませんでした。");
                return doc;
            }

            // 最も長いストレージを本文とみなす
            storages.Sort(delegate (TextStorage a, TextStorage b) { return b.Text.Length.CompareTo(a.Text.Length); });
            var body = storages[0];

            AppendStorage(doc, archive, body, opt);

            if (opt.IncludeFloatingText)
            {
                var extras = storages.Skip(1)
                    .Where(s => s.Text.Trim().Length >= 2)
                    .OrderBy(s => s.Order)
                    .ToList();
                if (extras.Count > 0)
                {
                    doc.Paragraphs.Add(new DocParagraph { Text = string.Empty, Kind = ParaKind.Body });
                    doc.Paragraphs.Add(new DocParagraph { Text = "［テキストボックス・図形内のテキスト］", Kind = ParaKind.Heading2 });
                    foreach (var s in extras) AppendStorage(doc, archive, s, opt);
                    if (note != null) note("本文以外のテキスト " + extras.Count + " 件を末尾に追加しました。");
                }
            }

            foreach (var p in doc.Paragraphs)
            {
                if (p.Kind == ParaKind.Title || p.Kind == ParaKind.Heading1)
                {
                    if (!string.IsNullOrWhiteSpace(p.Text)) { doc.Title = p.Text; break; }
                }
            }

            return doc;
        }

        private static void AppendStorage(DocModel doc, IwaArchive archive, TextStorage st, ConversionOptions opt)
        {
            var styleCache = new Dictionary<ulong, ParaKind>();
            int cursor = 0;
            string text = st.Text;

            while (cursor <= text.Length)
            {
                int nl = text.IndexOf('\n', cursor);
                int end = nl < 0 ? text.Length : nl;
                string line = text.Substring(cursor, end - cursor);

                var kind = ParaKind.Body;
                if (opt.DetectHeadings)
                {
                    ulong styleId = StyleAt(st, cursor);
                    if (styleId != 0UL)
                    {
                        if (!styleCache.TryGetValue(styleId, out kind))
                        {
                            kind = Classify(TextStorageFinder.ResolveStyleName(archive, styleId));
                            styleCache[styleId] = kind;
                        }
                    }
                }

                int level = 0;
                string trimmed = StripBullet(line, ref kind, ref level);

                if (trimmed.Trim().Length > 0 || opt.KeepEmptyParagraphs)
                    doc.Paragraphs.Add(new DocParagraph { Text = trimmed, Kind = kind, ListLevel = level });

                if (nl < 0) break;
                cursor = nl + 1;
            }
        }

        private static ulong StyleAt(TextStorage st, int charIndex)
        {
            ulong id = 0UL;
            for (int i = 0; i < st.ParaStyles.Count; i++)
            {
                if (st.ParaStyles[i].CharIndex <= charIndex) id = st.ParaStyles[i].StyleId;
                else break;
            }
            return id;
        }

        private static ParaKind Classify(string name)
        {
            if (string.IsNullOrEmpty(name)) return ParaKind.Body;
            string n = name.ToLowerInvariant();

            if (n.Contains("title") || n.Contains("タイトル")) return ParaKind.Title;
            if (n.Contains("caption") || n.Contains("キャプション") || n.Contains("図表番号")) return ParaKind.Caption;
            if (n.Contains("subhead") || n.Contains("subtitle") || n.Contains("サブ")) return ParaKind.Heading2;

            if (n.Contains("heading") || n.Contains("見出し") || n.Contains("head"))
            {
                if (n.Contains("1") || n.Contains("１")) return ParaKind.Heading1;
                if (n.Contains("2") || n.Contains("２")) return ParaKind.Heading2;
                if (n.Contains("3") || n.Contains("３") || n.Contains("4") || n.Contains("5")) return ParaKind.Heading3;
                return ParaKind.Heading1;
            }
            if (n.Contains("bullet") || n.Contains("箇条")) return ParaKind.Bullet;
            if (n.Contains("numbered") || n.Contains("番号")) return ParaKind.Numbered;
            return ParaKind.Body;
        }

        private static readonly string[] BulletMarks = { "•", "◦", "▪", "‣", "·", "・", "-", "–", "*" };

        private static string StripBullet(string line, ref ParaKind kind, ref int level)
        {
            if (string.IsNullOrEmpty(line)) return line;

            int indent = 0;
            while (indent < line.Length && (line[indent] == '\t' || line[indent] == ' ')) indent++;
            string s = line.Substring(indent);
            level = Math.Min(4, indent);

            foreach (var mark in BulletMarks)
            {
                if (s.StartsWith(mark))
                {
                    string rest = s.Substring(mark.Length);
                    if (rest.StartsWith("\t") || rest.StartsWith(" "))
                    {
                        if (kind == ParaKind.Body) kind = ParaKind.Bullet;
                        return rest.TrimStart('\t', ' ');
                    }
                }
            }

            int i = 0;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i > 0 && i < s.Length && (s[i] == '.' || s[i] == ')'))
            {
                string rest = s.Substring(i + 1);
                if (rest.StartsWith("\t") || rest.StartsWith(" "))
                {
                    if (kind == ParaKind.Body) kind = ParaKind.Numbered;
                    return rest.TrimStart('\t', ' ');
                }
            }

            if (indent > 0 && level > 0) return s;
            return line;
        }
    }
}
