using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Kaeru.Models;

namespace Kaeru.Writers
{
    public enum LatexTemplate
    {
        /// <summary>欧文 article（pdfLaTeX / XeLaTeX）</summary>
        Article,
        /// <summary>日本語 ltjsarticle（LuaLaTeX）</summary>
        JapaneseLuaLatex,
        /// <summary>日本語 jsarticle（upLaTeX + dvipdfmx）</summary>
        JapaneseUpLatex
    }

    /// <summary>中間モデルを LaTeX ソースに変換する。</summary>
    public class LatexWriter
    {
        public LatexTemplate Template { get; set; } = LatexTemplate.JapaneseLuaLatex;
        public string ImageFolder { get; set; } = "media";
        /// <summary>完全な文書（プリアンブル付き）を出力する。false なら本文断片のみ。</summary>
        public bool Standalone { get; set; } = true;

        private static readonly string[] SectionCommands =
        {
            "section", "subsection", "subsubsection", "paragraph", "subparagraph", "textbf"
        };

        public string Write(DocumentModel doc)
        {
            var sb = new StringBuilder();

            if (Standalone)
            {
                sb.Append(Preamble());
                if (!string.IsNullOrWhiteSpace(doc.Title))
                {
                    sb.AppendLine("\\title{" + Escape(doc.Title) + "}");
                    sb.AppendLine("\\date{\\today}");
                }
                sb.AppendLine();
                sb.AppendLine("\\begin{document}");
                if (!string.IsNullOrWhiteSpace(doc.Title)) sb.AppendLine("\\maketitle");
                sb.AppendLine();
            }

            bool inList = false;
            bool listOrdered = false;

            for (int i = 0; i < doc.Blocks.Count; i++)
            {
                var b = doc.Blocks[i];

                bool listKindChanged = inList && b.Kind == BlockKind.ListItem && b.Ordered != listOrdered;

                if ((b.Kind != BlockKind.ListItem || listKindChanged) && inList)
                {
                    sb.AppendLine(listOrdered ? "\\end{enumerate}" : "\\end{itemize}");
                    sb.AppendLine();
                    inList = false;
                }

                switch (b.Kind)
                {
                    case BlockKind.Heading:
                        int lvl = Math.Min(SectionCommands.Length, Math.Max(1, b.Level)) - 1;
                        if (lvl == SectionCommands.Length - 1)
                            sb.AppendLine("\\noindent\\textbf{" + Inline(b.Runs) + "}\\par");
                        else
                            sb.AppendLine("\\" + SectionCommands[lvl] + "{" + Inline(b.Runs) + "}");
                        sb.AppendLine();
                        break;

                    case BlockKind.ListItem:
                        if (!inList)
                        {
                            listOrdered = b.Ordered;
                            sb.AppendLine(listOrdered ? "\\begin{enumerate}" : "\\begin{itemize}");
                            inList = true;
                        }
                        sb.AppendLine("  \\item " + Inline(b.Runs));
                        break;

                    case BlockKind.Quote:
                        sb.AppendLine("\\begin{quote}");
                        sb.AppendLine(Inline(b.Runs));
                        sb.AppendLine("\\end{quote}");
                        sb.AppendLine();
                        break;

                    case BlockKind.Image:
                        WriteImage(sb, b.Image);
                        break;

                    case BlockKind.Table:
                        WriteTable(sb, b.Table);
                        break;

                    case BlockKind.PageBreak:
                        sb.AppendLine("\\clearpage");
                        sb.AppendLine();
                        break;

                    default:
                        sb.AppendLine(Inline(b.Runs).Replace("\n", " \\\\" + Environment.NewLine));
                        sb.AppendLine();
                        break;
                }
            }

            if (inList)
            {
                sb.AppendLine(listOrdered ? "\\end{enumerate}" : "\\end{itemize}");
                sb.AppendLine();
            }

            if (Standalone) sb.AppendLine("\\end{document}");
            return sb.ToString();
        }

        private string Preamble()
        {
            var sb = new StringBuilder();
            sb.AppendLine("% Kaeru により自動生成された LaTeX ソース");
            switch (Template)
            {
                case LatexTemplate.JapaneseLuaLatex:
                    sb.AppendLine("% コンパイル: lualatex <file>.tex");
                    sb.AppendLine("\\documentclass[a4paper,11pt]{ltjsarticle}");
                    sb.AppendLine("\\usepackage{graphicx}");
                    sb.AppendLine("\\usepackage[hidelinks]{hyperref}");
                    break;

                case LatexTemplate.JapaneseUpLatex:
                    sb.AppendLine("% コンパイル: uplatex <file>.tex && dvipdfmx <file>.dvi");
                    sb.AppendLine("\\documentclass[a4paper,11pt,uplatex,dvipdfmx]{jsarticle}");
                    sb.AppendLine("\\usepackage[dvipdfmx]{graphicx}");
                    sb.AppendLine("\\usepackage[dvipdfmx,hidelinks]{hyperref}");
                    break;

                default:
                    sb.AppendLine("% コンパイル: pdflatex <file>.tex");
                    sb.AppendLine("\\documentclass[a4paper,11pt]{article}");
                    sb.AppendLine("\\usepackage[T1]{fontenc}");
                    sb.AppendLine("\\usepackage{graphicx}");
                    sb.AppendLine("\\usepackage[hidelinks]{hyperref}");
                    break;
            }
            sb.AppendLine("\\usepackage{booktabs}");
            sb.AppendLine("\\usepackage{longtable}");
            sb.AppendLine("\\usepackage{ragged2e}");
            sb.AppendLine("\\graphicspath{{" + ImageFolder + "/}}");
            sb.AppendLine();
            return sb.ToString();
        }

        private void WriteImage(StringBuilder sb, ImageRef img)
        {
            string width = img.WidthCm.ToString("0.##", CultureInfo.InvariantCulture);
            sb.AppendLine("\\begin{figure}[htbp]");
            sb.AppendLine("  \\centering");
            sb.AppendLine("  \\includegraphics[width=" + width + "cm]{" + img.FileName + "}");
            sb.AppendLine("\\end{figure}");
            sb.AppendLine();
        }

        private void WriteTable(StringBuilder sb, TableModel table)
        {
            int cols = table.ColumnCount;
            if (cols == 0) return;

            string spec = string.Join("", Enumerable.Repeat("l", cols));
            sb.AppendLine("\\begin{table}[htbp]");
            sb.AppendLine("  \\centering");
            sb.AppendLine("  \\begin{tabular}{" + spec + "}");
            sb.AppendLine("    \\toprule");

            for (int r = 0; r < table.Rows.Count; r++)
            {
                var cells = new List<string>();
                for (int c = 0; c < cols; c++)
                {
                    string text = c < table.Rows[r].Cells.Count
                        ? Inline(table.Rows[r].Cells[c].Runs).Replace("\n", " ")
                        : "";
                    cells.Add(text);
                }
                sb.AppendLine("    " + string.Join(" & ", cells) + " \\\\");
                if (r == 0 && table.HasHeader) sb.AppendLine("    \\midrule");
            }

            sb.AppendLine("    \\bottomrule");
            sb.AppendLine("  \\end{tabular}");
            sb.AppendLine("\\end{table}");
            sb.AppendLine();
        }

        private string Inline(List<InlineRun> runs)
        {
            var sb = new StringBuilder();
            foreach (var r in runs)
            {
                string core = Escape(r.Text);
                if (core.Length == 0) continue;

                if (r.Code) core = "\\texttt{" + core + "}";
                if (r.Bold) core = "\\textbf{" + core + "}";
                if (r.Italic) core = "\\textit{" + core + "}";
                if (!string.IsNullOrEmpty(r.Link))
                    core = "\\href{" + EscapeUrl(r.Link) + "}{" + core + "}";

                sb.Append(core);
            }
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\textbackslash{}"); break;
                    case '{': sb.Append("\\{"); break;
                    case '}': sb.Append("\\}"); break;
                    case '$': sb.Append("\\$"); break;
                    case '&': sb.Append("\\&"); break;
                    case '#': sb.Append("\\#"); break;
                    case '%': sb.Append("\\%"); break;
                    case '_': sb.Append("\\_"); break;
                    case '~': sb.Append("\\textasciitilde{}"); break;
                    case '^': sb.Append("\\textasciicircum{}"); break;
                    case '<': sb.Append("\\textless{}"); break;
                    case '>': sb.Append("\\textgreater{}"); break;
                    case '|': sb.Append("\\textbar{}"); break;
                    case '\t': sb.Append("\\quad "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private static string EscapeUrl(string url)
        {
            return (url ?? "").Replace("%", "\\%").Replace("#", "\\#");
        }
    }
}
