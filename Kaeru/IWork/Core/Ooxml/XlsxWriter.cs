using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using IWorkConverter.Core.Model;

namespace IWorkConverter.Core.Ooxml
{
    /// <summary>BookModel を .xlsx として書き出す。</summary>
    public static class XlsxWriter
    {
        public static void Write(string path, BookModel book, ConversionOptions opt)
        {
            var tables = new List<TableModel>(book.Tables);
            if (tables.Count == 0) tables.Add(new TableModel { Name = "Sheet1" });

            var names = new List<string>();
            foreach (var t in tables) names.Add(UniqueSheetName(names, t.Name));

            using (var pkg = new OoxmlPackage(path))
            {
                pkg.AddXml("[Content_Types].xml", BuildContentTypes(tables.Count));
                pkg.AddXml("_rels/.rels", RootRelsXml);
                pkg.AddXml("xl/workbook.xml", BuildWorkbook(names));
                pkg.AddXml("xl/_rels/workbook.xml.rels", BuildWorkbookRels(tables.Count));
                pkg.AddXml("xl/styles.xml", StylesXml);
                pkg.AddXml("docProps/core.xml", CoreXml());
                pkg.AddXml("docProps/app.xml", AppXml);

                for (int i = 0; i < tables.Count; i++)
                    pkg.AddXml("xl/worksheets/sheet" + (i + 1) + ".xml", BuildSheet(tables[i], opt));
            }
        }

        private static string UniqueSheetName(List<string> used, string raw)
        {
            string name = string.IsNullOrWhiteSpace(raw) ? "Sheet" : raw;
            foreach (char c in new[] { '[', ']', ':', '*', '?', '/', '\\' }) name = name.Replace(c, '_');
            if (name.Length > 28) name = name.Substring(0, 28);
            string candidate = name;
            int n = 2;
            while (used.Contains(candidate)) candidate = name + "(" + (n++) + ")";
            return candidate;
        }

        private static string BuildSheet(TableModel table, ConversionOptions opt)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");

            for (int r = 0; r < table.RowCount; r++)
            {
                var row = new StringBuilder();
                bool any = false;
                for (int c = 0; c < table.ColumnCount; c++)
                {
                    var cell = table.Get(r, c);
                    if (cell == null) continue;
                    any = true;
                    string reference = ColumnName(c) + (r + 1);
                    int styleIndex = 0;
                    if (opt.BoldFirstRow && r == 0) styleIndex = 1;

                    if (cell.Date != null)
                    {
                        double oa = cell.Date.Value.ToOADate();
                        row.Append("<c r=\"" + reference + "\" s=\"2\"><v>" + oa.ToString("R", CultureInfo.InvariantCulture) + "</v></c>");
                    }
                    else if (cell.Number != null)
                    {
                        row.Append("<c r=\"" + reference + "\" s=\"" + styleIndex + "\"><v>" + cell.Number.Value.ToString("R", CultureInfo.InvariantCulture) + "</v></c>");
                    }
                    else
                    {
                        row.Append("<c r=\"" + reference + "\" s=\"" + styleIndex + "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">" + Xml.Esc(cell.Text) + "</t></is></c>");
                    }
                }
                if (any)
                {
                    sb.Append("<row r=\"" + (r + 1) + "\">");
                    sb.Append(row);
                    sb.Append("</row>");
                }
            }

            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        public static string ColumnName(int index)
        {
            var sb = new StringBuilder();
            int n = index;
            do
            {
                sb.Insert(0, (char)('A' + (n % 26)));
                n = n / 26 - 1;
            } while (n >= 0);
            return sb.ToString();
        }

        private static string BuildWorkbook(List<string> names)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
            sb.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            for (int i = 0; i < names.Count; i++)
                sb.Append("<sheet name=\"" + Xml.Esc(names[i]) + "\" sheetId=\"" + (i + 1) + "\" r:id=\"rId" + (i + 1) + "\"/>");
            sb.Append("</sheets></workbook>");
            return sb.ToString();
        }

        private static string BuildWorkbookRels(int sheetCount)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 0; i < sheetCount; i++)
                sb.Append("<Relationship Id=\"rId" + (i + 1) + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet" + (i + 1) + ".xml\"/>");
            sb.Append("<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private static string BuildContentTypes(int sheetCount)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            for (int i = 0; i < sheetCount; i++)
                sb.Append("<Override PartName=\"/xl/worksheets/sheet" + (i + 1) + ".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            sb.Append("<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>");
            sb.Append("<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>");
            sb.Append("</Types>");
            return sb.ToString();
        }

        private static string CoreXml()
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" ");
            sb.Append("xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" ");
            sb.Append("xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">");
            sb.Append("<dc:creator>iWorkConverter</dc:creator><cp:lastModifiedBy>iWorkConverter</cp:lastModifiedBy>");
            sb.Append("<dcterms:created xsi:type=\"dcterms:W3CDTF\">" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "</dcterms:created>");
            sb.Append("<dcterms:modified xsi:type=\"dcterms:W3CDTF\">" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "</dcterms:modified>");
            sb.Append("</cp:coreProperties>");
            return sb.ToString();
        }

        private const string AppXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties" xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes"><Application>iWorkConverter</Application></Properties>
""";

        private const string RootRelsXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
<Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/>
</Relationships>
""";

        private const string StylesXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
<fonts count="2"><font><sz val="11"/><name val="Yu Gothic"/></font><font><b/><sz val="11"/><name val="Yu Gothic"/></font></fonts>
<fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
<borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
<cellXfs count="3">
<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
<xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>
<xf numFmtId="22" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
</cellXfs>
<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
</styleSheet>
""";
    }
}
