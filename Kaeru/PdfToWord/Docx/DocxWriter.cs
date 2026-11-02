using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using PdfToWord.Models;

namespace PdfToWord.Docx
{
    /// <summary>書き出し時の設定。</summary>
    public sealed class DocxWriteOptions
    {
        public string JapaneseFont = "游明朝";
        public string LatinFont = "Times New Roman";
        public string HeadingJapaneseFont = "游ゴシック Medium";
        public string HeadingLatinFont = "Arial";
        public double BaseFontSize = 10.5;
        public bool IncludeTitle = true;
        public double PageWidthPoints = 595.3;
        public double PageHeightPoints = 841.9;
        public double MarginPoints = 56.7; // 約 2cm
        public bool MatchSourcePageSize = true;
    }

    /// <summary>文書モデルを .docx として書き出す。</summary>
    public sealed class DocxWriter
    {
        private readonly DocxWriteOptions _options;
        private readonly List<KeyValuePair<string, byte[]>> _media = new List<KeyValuePair<string, byte[]>>();
        private int _relationshipId = 10;
        private int _drawingId = 1;

        public DocxWriter(DocxWriteOptions options)
        {
            _options = options ?? new DocxWriteOptions();
        }

        public void Write(DocumentModel model, string path)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                Write(model, stream);
            }
        }

        public void Write(DocumentModel model, Stream output)
        {
            _media.Clear();
            _relationshipId = 10;
            _drawingId = 1;

            if (_options.MatchSourcePageSize && model.PageWidthPoints > 100 && model.PageHeightPoints > 100)
            {
                _options.PageWidthPoints = model.PageWidthPoints;
                _options.PageHeightPoints = model.PageHeightPoints;
            }

            string documentXml = BuildDocument(model);

            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                AddEntry(archive, "[Content_Types].xml", ContentTypes());
                AddEntry(archive, "_rels/.rels", RootRelationships());
                AddEntry(archive, "docProps/core.xml", CoreProperties(model));
                AddEntry(archive, "docProps/app.xml", AppProperties());
                AddEntry(archive, "word/document.xml", documentXml);
                AddEntry(archive, "word/_rels/document.xml.rels", DocumentRelationships());
                AddEntry(archive, "word/styles.xml", Styles());
                AddEntry(archive, "word/numbering.xml", Numbering());
                AddEntry(archive, "word/settings.xml", Settings());

                foreach (var pair in _media)
                {
                    ZipArchiveEntry entry = archive.CreateEntry("word/media/" + pair.Key, CompressionLevel.Fastest);
                    using (Stream entryStream = entry.Open())
                        entryStream.Write(pair.Value, 0, pair.Value.Length);
                }
            }
        }

        private static void AddEntry(ZipArchive archive, string name, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using (Stream stream = entry.Open())
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        private string BuildDocument(DocumentModel model)
        {
            var body = new StringBuilder();

            if (_options.IncludeTitle && !string.IsNullOrWhiteSpace(model.Title))
            {
                var title = new DocBlock { Kind = BlockKind.Heading, HeadingLevel = 1, Alignment = TextAlignment.Center };
                title.Runs.Add(new InlineRun(model.Title));
                body.Append(Paragraph(title));
            }

            bool previousWasTable = false;
            foreach (DocBlock block in model.Blocks)
            {
                if (block.Kind == BlockKind.Table)
                {
                    if (previousWasTable) body.Append("<w:p/>");
                    body.Append(Table(block.Table));
                    previousWasTable = true;
                    continue;
                }
                previousWasTable = false;

                switch (block.Kind)
                {
                    case BlockKind.PageBreak:
                        body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");
                        break;
                    case BlockKind.Image:
                        body.Append(Image(block.Image));
                        break;
                    default:
                        body.Append(Paragraph(block));
                        break;
                }
            }
            body.Append("<w:p/>");
            body.Append(SectionProperties());

            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\""
                + " xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\""
                + " xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\""
                + " xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\""
                + " xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">"
                + "<w:body>" + body + "</w:body></w:document>";
        }

        private string SectionProperties()
        {
            int width = (int)Math.Round(_options.PageWidthPoints * 20);
            int height = (int)Math.Round(_options.PageHeightPoints * 20);
            int margin = (int)Math.Round(_options.MarginPoints * 20);
            return "<w:sectPr><w:pgSz w:w=\"" + width + "\" w:h=\"" + height + "\"/>"
                + "<w:pgMar w:top=\"" + margin + "\" w:right=\"" + margin + "\" w:bottom=\"" + margin
                + "\" w:left=\"" + margin + "\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/>"
                + "<w:docGrid w:linePitch=\"360\"/></w:sectPr>";
        }

        private string Paragraph(DocBlock block)
        {
            var sb = new StringBuilder();
            sb.Append("<w:p>");
            sb.Append("<w:pPr>");

            if (block.Kind == BlockKind.Heading)
            {
                int level = Math.Max(1, Math.Min(6, block.HeadingLevel));
                sb.Append("<w:pStyle w:val=\"Heading").Append(level).Append("\"/>");
            }
            else if (block.Kind == BlockKind.ListItem)
            {
                sb.Append("<w:pStyle w:val=\"ListParagraph\"/>");
                sb.Append("<w:numPr><w:ilvl w:val=\"").Append(Math.Max(0, Math.Min(4, block.ListLevel)))
                  .Append("\"/><w:numId w:val=\"").Append(block.Ordered ? 2 : 1).Append("\"/></w:numPr>");
            }

            if (block.Alignment == TextAlignment.Center) sb.Append("<w:jc w:val=\"center\"/>");
            else if (block.Alignment == TextAlignment.Right) sb.Append("<w:jc w:val=\"right\"/>");
            sb.Append("</w:pPr>");

            foreach (InlineRun run in block.Runs) sb.Append(Run(run, block.Kind == BlockKind.Heading));
            sb.Append("</w:p>");
            return sb.ToString();
        }

        private string Run(InlineRun run, bool insideHeading)
        {
            if (run == null || string.IsNullOrEmpty(run.Text)) return "";
            var sb = new StringBuilder();
            sb.Append("<w:r>");
            bool hasProperties = (run.Bold && !insideHeading) || run.Italic || run.Monospace || run.Superscript;
            if (hasProperties)
            {
                sb.Append("<w:rPr>");
                if (run.Monospace)
                    sb.Append("<w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:eastAsia=\"")
                      .Append(Escape(_options.JapaneseFont)).Append("\"/>");
                if (run.Bold && !insideHeading) sb.Append("<w:b/>");
                if (run.Italic) sb.Append("<w:i/>");
                if (run.Superscript) sb.Append("<w:vertAlign w:val=\"superscript\"/>");
                sb.Append("</w:rPr>");
            }
            sb.Append("<w:t xml:space=\"preserve\">").Append(Escape(run.Text)).Append("</w:t>");
            sb.Append("</w:r>");
            return sb.ToString();
        }

        private string Table(TableBlock table)
        {
            if (table == null || table.Rows.Count == 0) return "";
            int columnCount = table.ColumnCount;
            if (columnCount == 0) return "";

            double totalWidth = 0;
            foreach (double width in table.ColumnWidths) totalWidth += width;
            double available = _options.PageWidthPoints - _options.MarginPoints * 2;
            double scale = totalWidth > 0 && totalWidth > available ? available / totalWidth : 1.0;

            var grid = new List<int>();
            for (int i = 0; i < table.ColumnWidths.Count; i++)
                grid.Add(Math.Max(240, (int)Math.Round(table.ColumnWidths[i] * scale * 20)));
            while (grid.Count < columnCount) grid.Add((int)Math.Round(available * 20 / Math.Max(1, columnCount)));

            var sb = new StringBuilder();
            sb.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/>");
            sb.Append("<w:tblW w:w=\"0\" w:type=\"auto\"/>");
            sb.Append("<w:tblBorders>");
            string[] sides = { "top", "left", "bottom", "right", "insideH", "insideV" };
            foreach (string side in sides)
                sb.Append("<w:").Append(side).Append(" w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"808080\"/>");
            sb.Append("</w:tblBorders>");
            sb.Append("<w:tblLayout w:type=\"fixed\"/>");
            sb.Append("</w:tblPr><w:tblGrid>");
            foreach (int width in grid) sb.Append("<w:gridCol w:w=\"").Append(width).Append("\"/>");
            sb.Append("</w:tblGrid>");

            for (int r = 0; r < table.Rows.Count; r++)
            {
                TableRow row = table.Rows[r];
                sb.Append("<w:tr>");
                if (r == 0 && table.HasHeaderRow)
                    sb.Append("<w:trPr><w:tblHeader/></w:trPr>");

                int columnIndex = 0;
                foreach (TableCell cell in row.Cells)
                {
                    int span = Math.Max(1, cell.ColumnSpan);
                    int cellWidth = 0;
                    for (int i = 0; i < span && columnIndex + i < grid.Count; i++) cellWidth += grid[columnIndex + i];
                    if (cellWidth == 0) cellWidth = 1000;

                    sb.Append("<w:tc><w:tcPr><w:tcW w:w=\"").Append(cellWidth).Append("\" w:type=\"dxa\"/>");
                    if (span > 1) sb.Append("<w:gridSpan w:val=\"").Append(span).Append("\"/>");
                    sb.Append("<w:vAlign w:val=\"center\"/></w:tcPr>");

                    sb.Append("<w:p><w:pPr><w:spacing w:before=\"20\" w:after=\"20\"/></w:pPr>");
                    bool header = r == 0 && table.HasHeaderRow;
                    foreach (InlineRun run in cell.Runs)
                    {
                        InlineRun copy = run.Clone();
                        if (header) copy.Bold = true;
                        sb.Append(Run(copy, false));
                    }
                    sb.Append("</w:p></w:tc>");
                    columnIndex += span;
                }
                sb.Append("</w:tr>");
            }
            sb.Append("</w:tbl>");
            sb.Append("<w:p/>");
            return sb.ToString();
        }

        private string Image(DocImage image)
        {
            if (image == null || image.Data == null || image.Data.Length == 0) return "";

            string fileName = "image" + (_media.Count + 1) + "." + image.Extension;
            _media.Add(new KeyValuePair<string, byte[]>(fileName, image.Data));
            int relationship = _relationshipId++;
            int drawing = _drawingId++;

            double available = _options.PageWidthPoints - _options.MarginPoints * 2;
            double width = image.WidthPoints > 0 ? image.WidthPoints : 200;
            double height = image.HeightPoints > 0 ? image.HeightPoints : 150;
            if (width > available)
            {
                height = height * available / width;
                width = available;
            }
            long cx = (long)Math.Round(width * 12700);
            long cy = (long)Math.Round(height * 12700);
            if (cx <= 0) cx = 1270000;
            if (cy <= 0) cy = 952500;

            var sb = new StringBuilder();
            sb.Append("<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:drawing>");
            sb.Append("<wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">");
            sb.Append("<wp:extent cx=\"").Append(cx).Append("\" cy=\"").Append(cy).Append("\"/>");
            sb.Append("<wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/>");
            sb.Append("<wp:docPr id=\"").Append(drawing).Append("\" name=\"Picture ").Append(drawing).Append("\"/>");
            sb.Append("<wp:cNvGraphicFramePr><a:graphicFrameLocks xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" noChangeAspect=\"1\"/></wp:cNvGraphicFramePr>");
            sb.Append("<a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">");
            sb.Append("<a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">");
            sb.Append("<pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">");
            sb.Append("<pic:nvPicPr><pic:cNvPr id=\"").Append(drawing).Append("\" name=\"").Append(fileName).Append("\"/>");
            sb.Append("<pic:cNvPicPr/></pic:nvPicPr>");
            sb.Append("<pic:blipFill><a:blip r:embed=\"rId").Append(relationship).Append("\"/>");
            sb.Append("<a:stretch><a:fillRect/></a:stretch></pic:blipFill>");
            sb.Append("<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"").Append(cx).Append("\" cy=\"").Append(cy).Append("\"/></a:xfrm>");
            sb.Append("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>");
            sb.Append("</pic:pic></a:graphicData></a:graphic></wp:inline>");
            sb.Append("</w:drawing></w:r></w:p>");
            return sb.ToString();
        }

        private string ContentTypes()
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Default Extension=\"png\" ContentType=\"image/png\"/>");
            sb.Append("<Default Extension=\"jpg\" ContentType=\"image/jpeg\"/>");
            sb.Append("<Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>");
            sb.Append("<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>");
            sb.Append("<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>");
            sb.Append("<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>");
            sb.Append("<Override PartName=\"/word/settings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml\"/>");
            sb.Append("<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>");
            sb.Append("<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>");
            sb.Append("</Types>");
            return sb.ToString();
        }

        private string RootRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>"
                + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>"
                + "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>"
                + "</Relationships>";
        }

        private string DocumentRelationships()
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            sb.Append("<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            sb.Append("<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>");
            sb.Append("<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings\" Target=\"settings.xml\"/>");
            for (int i = 0; i < _media.Count; i++)
            {
                sb.Append("<Relationship Id=\"rId").Append(10 + i)
                  .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/")
                  .Append(_media[i].Key).Append("\"/>");
            }
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private string Settings()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<w:settings xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
                + "<w:zoom w:percent=\"100\"/><w:defaultTabStop w:val=\"840\"/>"
                + "<w:compat><w:compatSetting w:name=\"compatibilityMode\""
                + " w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"15\"/></w:compat>"
                + "</w:settings>";
        }

        private string CoreProperties(DocumentModel model)
        {
            string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\"");
            sb.Append(" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\"");
            sb.Append(" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">");
            if (!string.IsNullOrWhiteSpace(model.Title)) sb.Append("<dc:title>").Append(Escape(model.Title)).Append("</dc:title>");
            if (!string.IsNullOrWhiteSpace(model.Author)) sb.Append("<dc:creator>").Append(Escape(model.Author)).Append("</dc:creator>");
            sb.Append("<cp:lastModifiedBy>PdfToWord</cp:lastModifiedBy>");
            sb.Append("<dcterms:created xsi:type=\"dcterms:W3CDTF\">").Append(now).Append("</dcterms:created>");
            sb.Append("<dcterms:modified xsi:type=\"dcterms:W3CDTF\">").Append(now).Append("</dcterms:modified>");
            sb.Append("</cp:coreProperties>");
            return sb.ToString();
        }

        private string AppProperties()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\""
                + " xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">"
                + "<Application>PdfToWord</Application></Properties>";
        }

        private string Styles()
        {
            double baseSize = _options.BaseFontSize;
            int halfPoints = (int)Math.Round(baseSize * 2);
            string japanese = Escape(_options.JapaneseFont);
            string latin = Escape(_options.LatinFont);
            string headingJapanese = Escape(_options.HeadingJapaneseFont);
            string headingLatin = Escape(_options.HeadingLatinFont);

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">");
            sb.Append("<w:docDefaults><w:rPrDefault><w:rPr>");
            sb.Append("<w:rFonts w:ascii=\"").Append(latin).Append("\" w:hAnsi=\"").Append(latin)
              .Append("\" w:eastAsia=\"").Append(japanese).Append("\" w:cs=\"").Append(latin).Append("\"/>");
            sb.Append("<w:sz w:val=\"").Append(halfPoints).Append("\"/><w:szCs w:val=\"").Append(halfPoints).Append("\"/>");
            sb.Append("<w:lang w:val=\"en-US\" w:eastAsia=\"ja-JP\"/>");
            sb.Append("</w:rPr></w:rPrDefault><w:pPrDefault><w:pPr>");
            sb.Append("<w:spacing w:after=\"0\" w:line=\"280\" w:lineRule=\"auto\"/>");
            sb.Append("</w:pPr></w:pPrDefault></w:docDefaults>");

            sb.Append("<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\">");
            sb.Append("<w:name w:val=\"Normal\"/><w:qFormat/>");
            sb.Append("<w:pPr><w:spacing w:after=\"80\" w:line=\"288\" w:lineRule=\"auto\"/></w:pPr>");
            sb.Append("</w:style>");

            double[] headingScale = { 1.8, 1.5, 1.3, 1.15, 1.05, 1.0 };
            for (int level = 1; level <= 6; level++)
            {
                int size = (int)Math.Round(baseSize * headingScale[level - 1] * 2);
                sb.Append("<w:style w:type=\"paragraph\" w:styleId=\"Heading").Append(level).Append("\">");
                sb.Append("<w:name w:val=\"heading ").Append(level).Append("\"/>");
                sb.Append("<w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/>");
                sb.Append("<w:pPr><w:keepNext/><w:outlineLvl w:val=\"").Append(level - 1).Append("\"/>");
                sb.Append("<w:spacing w:before=\"").Append(level <= 2 ? 320 : 220).Append("\" w:after=\"120\"/>");
                sb.Append("</w:pPr><w:rPr>");
                sb.Append("<w:rFonts w:ascii=\"").Append(headingLatin).Append("\" w:hAnsi=\"").Append(headingLatin)
                  .Append("\" w:eastAsia=\"").Append(headingJapanese).Append("\"/>");
                sb.Append("<w:b/><w:sz w:val=\"").Append(size).Append("\"/><w:szCs w:val=\"").Append(size).Append("\"/>");
                sb.Append("</w:rPr></w:style>");
            }

            sb.Append("<w:style w:type=\"paragraph\" w:styleId=\"ListParagraph\">");
            sb.Append("<w:name w:val=\"List Paragraph\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/>");
            sb.Append("<w:pPr><w:ind w:left=\"420\"/><w:contextualSpacing/></w:pPr></w:style>");

            sb.Append("<w:style w:type=\"table\" w:styleId=\"TableGrid\">");
            sb.Append("<w:name w:val=\"Table Grid\"/><w:tblPr><w:tblBorders>");
            string[] sides = { "top", "left", "bottom", "right", "insideH", "insideV" };
            foreach (string side in sides)
                sb.Append("<w:").Append(side).Append(" w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"808080\"/>");
            sb.Append("</w:tblBorders><w:tblCellMar>");
            sb.Append("<w:top w:w=\"40\" w:type=\"dxa\"/><w:left w:w=\"80\" w:type=\"dxa\"/>");
            sb.Append("<w:bottom w:w=\"40\" w:type=\"dxa\"/><w:right w:w=\"80\" w:type=\"dxa\"/>");
            sb.Append("</w:tblCellMar></w:tblPr></w:style>");

            sb.Append("</w:styles>");
            return sb.ToString();
        }

        private string Numbering()
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<w:numbering xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">");

            string[] bulletCharacters = { "\u25CF", "\u25CB", "\u25AA", "\u2013", "\u25CB" };
            sb.Append("<w:abstractNum w:abstractNumId=\"0\"><w:multiLevelType w:val=\"hybridMultilevel\"/>");
            for (int level = 0; level < 5; level++)
            {
                sb.Append("<w:lvl w:ilvl=\"").Append(level).Append("\">");
                sb.Append("<w:start w:val=\"1\"/><w:numFmt w:val=\"bullet\"/>");
                sb.Append("<w:lvlText w:val=\"").Append(Escape(bulletCharacters[level])).Append("\"/>");
                sb.Append("<w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"").Append(420 + level * 420)
                  .Append("\" w:hanging=\"420\"/></w:pPr>");
                sb.Append("<w:rPr><w:rFonts w:hint=\"eastAsia\"/></w:rPr>");
                sb.Append("</w:lvl>");
            }
            sb.Append("</w:abstractNum>");

            string[] formats = { "decimal", "lowerLetter", "lowerRoman", "decimal", "lowerLetter" };
            sb.Append("<w:abstractNum w:abstractNumId=\"1\"><w:multiLevelType w:val=\"hybridMultilevel\"/>");
            for (int level = 0; level < 5; level++)
            {
                sb.Append("<w:lvl w:ilvl=\"").Append(level).Append("\">");
                sb.Append("<w:start w:val=\"1\"/><w:numFmt w:val=\"").Append(formats[level]).Append("\"/>");
                sb.Append("<w:lvlText w:val=\"%").Append(level + 1).Append(".\"/>");
                sb.Append("<w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"").Append(420 + level * 420)
                  .Append("\" w:hanging=\"420\"/></w:pPr>");
                sb.Append("</w:lvl>");
            }
            sb.Append("</w:abstractNum>");

            sb.Append("<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num>");
            sb.Append("<w:num w:numId=\"2\"><w:abstractNumId w:val=\"1\"/></w:num>");
            sb.Append("</w:numbering>");
            return sb.ToString();
        }

        /// <summary>XML として安全な文字列にする。</summary>
        public static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length + 16);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default:
                        if (c == '\t' || c == '\n' || c == '\r') { sb.Append(' '); break; }
                        if (c < 0x20 || (c >= 0x7F && c <= 0x9F)) break;
                        if (c >= 0xFFFE) break;
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
