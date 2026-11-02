using System;
using System.Collections.Generic;

namespace IWorkConverter.Core.Model
{
    public enum ParaKind
    {
        Body = 0,
        Title = 1,
        Heading1 = 2,
        Heading2 = 3,
        Heading3 = 4,
        Caption = 5,
        Bullet = 6,
        Numbered = 7
    }

    public sealed class DocParagraph
    {
        public string Text = string.Empty;
        public ParaKind Kind = ParaKind.Body;
        public int ListLevel;
    }

    /// <summary>Pages → docx 用の中間モデル。</summary>
    public sealed class DocModel
    {
        public string Title = string.Empty;
        public readonly List<DocParagraph> Paragraphs = new List<DocParagraph>();
    }

    public sealed class CellModel
    {
        public string Text;
        public double? Number;
        public DateTime? Date;
        public bool IsEmpty { get { return Text == null && Number == null && Date == null; } }
    }

    public sealed class TableModel
    {
        public string Name = "表";
        public int RowCount;
        public int ColumnCount;
        public Dictionary<long, CellModel> Cells = new Dictionary<long, CellModel>();

        public static long Key(int row, int col) { return ((long)row << 20) | (uint)col; }

        public CellModel Get(int row, int col)
        {
            CellModel c;
            return Cells.TryGetValue(Key(row, col), out c) ? c : null;
        }

        public void Set(int row, int col, CellModel c)
        {
            if (c == null || c.IsEmpty) return;
            Cells[Key(row, col)] = c;
            if (row + 1 > RowCount) RowCount = row + 1;
            if (col + 1 > ColumnCount) ColumnCount = col + 1;
        }
    }

    /// <summary>Numbers → xlsx 用の中間モデル。</summary>
    public sealed class BookModel
    {
        public readonly List<TableModel> Tables = new List<TableModel>();
    }

    public sealed class SlideModel
    {
        public string Title = string.Empty;
        public readonly List<string> Bullets = new List<string>();
        public string Notes = string.Empty;
    }

    /// <summary>Keynote → pptx 用の中間モデル。</summary>
    public sealed class DeckModel
    {
        public readonly List<SlideModel> Slides = new List<SlideModel>();
    }
}
