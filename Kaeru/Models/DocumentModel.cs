using System.Collections.Generic;
using System.Linq;

namespace Kaeru.Models
{
    /// <summary>ブロックの種類。</summary>
    public enum BlockKind
    {
        Paragraph,
        Heading,
        ListItem,
        Quote,
        Table,
        Image,
        PageBreak
    }

    /// <summary>段落内の文字列（書式つき）。</summary>
    public class InlineRun
    {
        public string Text { get; set; } = "";
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Code { get; set; }
        /// <summary>ハイパーリンク先。無い場合は null。</summary>
        public string Link { get; set; }

        public InlineRun Clone()
        {
            return new InlineRun { Text = Text, Bold = Bold, Italic = Italic, Code = Code, Link = Link };
        }
    }

    /// <summary>埋め込み画像。</summary>
    public class ImageRef
    {
        public string FileName { get; set; }
        public byte[] Data { get; set; }
        public string MediaType { get; set; } = "image/png";
        public double WidthCm { get; set; } = 12.0;
        public double HeightCm { get; set; } = 8.0;
    }

    public class TableCellModel
    {
        public List<InlineRun> Runs { get; set; } = new List<InlineRun>();
        public string PlainText
        {
            get { return string.Concat(Runs.Select(r => r.Text)); }
        }
    }

    public class TableRowModel
    {
        public List<TableCellModel> Cells { get; set; } = new List<TableCellModel>();
    }

    public class TableModel
    {
        public List<TableRowModel> Rows { get; set; } = new List<TableRowModel>();
        public bool HasHeader { get; set; } = true;

        public int ColumnCount
        {
            get { return Rows.Count == 0 ? 0 : Rows.Max(r => r.Cells.Count); }
        }
    }

    /// <summary>文書を構成する 1 ブロック。</summary>
    public class Block
    {
        public BlockKind Kind { get; set; } = BlockKind.Paragraph;
        /// <summary>見出しレベル（1-6）またはリストのネスト深さ（0 起点）。</summary>
        public int Level { get; set; } = 1;
        /// <summary>番号付きリストなら true。</summary>
        public bool Ordered { get; set; }
        public List<InlineRun> Runs { get; set; } = new List<InlineRun>();
        public TableModel Table { get; set; }
        public ImageRef Image { get; set; }

        public string PlainText
        {
            get { return string.Concat(Runs.Select(r => r.Text)); }
        }

        public bool IsEmpty
        {
            get
            {
                if (Kind == BlockKind.Table || Kind == BlockKind.Image || Kind == BlockKind.PageBreak) return false;
                return string.IsNullOrWhiteSpace(PlainText);
            }
        }

        public static Block Text(BlockKind kind, string text)
        {
            var b = new Block { Kind = kind, Level = kind == BlockKind.Heading ? 1 : 0 };
            b.Runs.Add(new InlineRun { Text = text });
            return b;
        }
    }

    /// <summary>入力ファイルを読み込んだ結果。</summary>
    public class DocumentModel
    {
        public string Title { get; set; }
        public string SourcePath { get; set; }
        public List<Block> Blocks { get; set; } = new List<Block>();
        public List<ImageRef> Images { get; set; } = new List<ImageRef>();
    }
}
