using System;
using System.Collections.Generic;
using System.Text;

namespace PdfToWord.Models
{
    public enum BlockKind
    {
        Paragraph,
        Heading,
        ListItem,
        Table,
        Image,
        PageBreak
    }

    public enum TextAlignment
    {
        Left,
        Center,
        Right
    }

    /// <summary>書式の変わらない文字列のかたまり。</summary>
    public sealed class InlineRun
    {
        public string Text = "";
        public bool Bold;
        public bool Italic;
        public bool Monospace;
        public bool Superscript;

        public InlineRun() { }

        public InlineRun(string text) { Text = text ?? ""; }

        public InlineRun Clone()
        {
            return new InlineRun
            {
                Text = Text,
                Bold = Bold,
                Italic = Italic,
                Monospace = Monospace,
                Superscript = Superscript
            };
        }
    }

    public sealed class TableCell
    {
        public List<InlineRun> Runs = new List<InlineRun>();
        public int ColumnSpan = 1;
        public int RowSpan = 1;
        public string PlainText
        {
            get
            {
                var sb = new StringBuilder();
                foreach (InlineRun run in Runs) sb.Append(run.Text);
                return sb.ToString();
            }
        }
    }

    public sealed class TableRow
    {
        public List<TableCell> Cells = new List<TableCell>();
    }

    public sealed class TableBlock
    {
        public List<TableRow> Rows = new List<TableRow>();
        public List<double> ColumnWidths = new List<double>();
        public bool HasHeaderRow = true;
        public int ColumnCount
        {
            get
            {
                int max = 0;
                foreach (TableRow row in Rows) max = Math.Max(max, row.Cells.Count);
                return max;
            }
        }
    }

    /// <summary>文書に埋め込む画像。</summary>
    public sealed class DocImage
    {
        public byte[] Data;
        public string Extension = "png";
        public double WidthPoints;
        public double HeightPoints;
        public int PixelWidth;
        public int PixelHeight;
    }

    /// <summary>文書を構成する 1 ブロック。</summary>
    public sealed class DocBlock
    {
        public BlockKind Kind = BlockKind.Paragraph;
        public List<InlineRun> Runs = new List<InlineRun>();
        public int HeadingLevel = 1;
        public bool Ordered;
        public int ListLevel;
        public TextAlignment Alignment = TextAlignment.Left;
        public TableBlock Table;
        public DocImage Image;
        public int SourcePage = 1;

        public string PlainText
        {
            get
            {
                var sb = new StringBuilder();
                foreach (InlineRun run in Runs) sb.Append(run.Text);
                return sb.ToString();
            }
        }

        public static DocBlock Text(BlockKind kind, string text)
        {
            var block = new DocBlock { Kind = kind };
            block.Runs.Add(new InlineRun(text));
            return block;
        }
    }

    /// <summary>変換結果の文書全体。</summary>
    public sealed class DocumentModel
    {
        public string Title;
        public string Author;
        public List<DocBlock> Blocks = new List<DocBlock>();
        public int PageCount;
        public double PageWidthPoints;
        public double PageHeightPoints;
        public List<string> Notes = new List<string>();

        public int CountOf(BlockKind kind)
        {
            int count = 0;
            foreach (DocBlock block in Blocks) if (block.Kind == kind) count++;
            return count;
        }

        public int CharacterCount
        {
            get
            {
                int count = 0;
                foreach (DocBlock block in Blocks)
                {
                    if (block.Kind == BlockKind.Table && block.Table != null)
                    {
                        foreach (TableRow row in block.Table.Rows)
                            foreach (TableCell cell in row.Cells) count += cell.PlainText.Length;
                    }
                    else
                    {
                        count += block.PlainText.Length;
                    }
                }
                return count;
            }
        }
    }
}
