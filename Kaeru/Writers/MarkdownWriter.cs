using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Kaeru.Models;

namespace Kaeru.Writers
{
    /// <summary>中間モデルを Markdown（GitHub 風）に変換する。</summary>
    public class MarkdownWriter
    {
        /// <summary>画像を参照するときの相対フォルダ名。</summary>
        public string ImageFolder { get; set; } = "media";

        /// <summary>先頭に YAML フロントマターを出力する。</summary>
        public bool FrontMatter { get; set; } = true;

        public string Write(DocumentModel doc)
        {
            var sb = new StringBuilder();

            if (FrontMatter && !string.IsNullOrWhiteSpace(doc.Title))
            {
                sb.AppendLine("---");
                sb.AppendLine("title: \"" + doc.Title.Replace("\"", "\\\"") + "\"");
                sb.AppendLine("---");
                sb.AppendLine();
            }

            for (int i = 0; i < doc.Blocks.Count; i++)
            {
                var b = doc.Blocks[i];
                switch (b.Kind)
                {
                    case BlockKind.Heading:
                        sb.AppendLine(new string('#', Math.Min(6, Math.Max(1, b.Level))) + " " + Inline(b.Runs));
                        sb.AppendLine();
                        break;

                    case BlockKind.ListItem:
                        sb.Append(new string(' ', Math.Max(0, b.Level) * 2));
                        sb.Append(b.Ordered ? "1. " : "- ");
                        sb.AppendLine(Inline(b.Runs));
                        if (!IsNextSameList(doc, i, b.Ordered)) sb.AppendLine();
                        break;

                    case BlockKind.Quote:
                        foreach (var line in Inline(b.Runs).Split('\n'))
                            sb.AppendLine("> " + line);
                        sb.AppendLine();
                        break;

                    case BlockKind.Image:
                        sb.AppendLine("![" + Escape(b.Image.FileName) + "](" +
                                      ImageFolder + "/" + b.Image.FileName + ")");
                        sb.AppendLine();
                        break;

                    case BlockKind.Table:
                        WriteTable(sb, b.Table);
                        break;

                    case BlockKind.PageBreak:
                        sb.AppendLine("<div style=\"page-break-after: always;\"></div>");
                        sb.AppendLine();
                        break;

                    default:
                        sb.AppendLine(Inline(b.Runs).Replace("\n", "  " + Environment.NewLine));
                        sb.AppendLine();
                        break;
                }
            }

            return sb.ToString().TrimEnd() + Environment.NewLine;
        }

        private static bool IsNextSameList(DocumentModel doc, int i, bool ordered)
        {
            return i + 1 < doc.Blocks.Count
                   && doc.Blocks[i + 1].Kind == BlockKind.ListItem
                   && doc.Blocks[i + 1].Ordered == ordered;
        }

        private void WriteTable(StringBuilder sb, TableModel table)
        {
            int cols = table.ColumnCount;
            if (cols == 0) return;

            for (int r = 0; r < table.Rows.Count; r++)
            {
                var cells = new List<string>();
                for (int c = 0; c < cols; c++)
                {
                    string text = c < table.Rows[r].Cells.Count
                        ? Inline(table.Rows[r].Cells[c].Runs).Replace("\n", "<br>").Replace("|", "\\|")
                        : "";
                    cells.Add(text);
                }
                sb.AppendLine("| " + string.Join(" | ", cells) + " |");

                if (r == 0)
                    sb.AppendLine("|" + string.Concat(Enumerable.Repeat(" --- |", cols)));
            }
            sb.AppendLine();
        }

        private string Inline(List<InlineRun> runs)
        {
            var sb = new StringBuilder();
            foreach (var r in runs)
            {
                string t = r.Code ? r.Text : Escape(r.Text);
                if (string.IsNullOrEmpty(t)) continue;

                // 前後の空白は装飾の外に出す（**text ** は無効なため）
                string lead = LeadingSpace(t);
                string trail = TrailingSpace(t);
                string core = t.Substring(lead.Length, t.Length - lead.Length - trail.Length);
                if (core.Length == 0) { sb.Append(t); continue; }

                if (r.Code) core = "`" + core.Replace("`", "\u0060\u200b") + "`";
                if (r.Bold) core = "**" + core + "**";
                if (r.Italic) core = "*" + core + "*";
                if (!string.IsNullOrEmpty(r.Link)) core = "[" + core + "](" + r.Link + ")";

                sb.Append(lead).Append(core).Append(trail);
            }
            return sb.ToString();
        }

        private static string LeadingSpace(string s)
        {
            int i = 0;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            return s.Substring(0, i);
        }

        private static string TrailingSpace(string s)
        {
            int i = s.Length;
            while (i > 0 && (s[i - 1] == ' ' || s[i - 1] == '\t')) i--;
            return s.Substring(i);
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\':
                    case '`':
                    case '*':
                    case '_':
                    case '[':
                    case ']':
                    case '<':
                    case '>':
                        sb.Append('\\').Append(c);
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
