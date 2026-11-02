using System;
using System.Text;
using IWorkConverter.Core.Model;

namespace IWorkConverter.Core.Ooxml
{
    /// <summary>DocModel を .docx として書き出す。</summary>
    public static class DocxWriter
    {
        public static void Write(string path, DocModel doc, ConversionOptions opt)
        {
            using (var pkg = new OoxmlPackage(path))
            {
                pkg.AddXml("[Content_Types].xml", ContentTypesXml);
                pkg.AddXml("_rels/.rels", RootRelsXml);
                pkg.AddXml("word/_rels/document.xml.rels", DocumentRelsXml);
                pkg.AddXml("word/styles.xml", StylesXml);
                pkg.AddXml("word/numbering.xml", NumberingXml);
                pkg.AddXml("docProps/core.xml", CoreXml(doc.Title));
                pkg.AddXml("docProps/app.xml", AppXml);
                pkg.AddXml("word/document.xml", BuildDocument(doc));
            }
        }

        private static string BuildDocument(DocModel doc)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");

            foreach (var p in doc.Paragraphs)
            {
                sb.Append("<w:p>");
                sb.Append("<w:pPr>");
                string style = StyleId(p.Kind);
                if (style != null) sb.Append("<w:pStyle w:val=\"" + style + "\"/>");
                if (p.Kind == ParaKind.Bullet || p.Kind == ParaKind.Numbered)
                {
                    int numId = p.Kind == ParaKind.Bullet ? 1 : 2;
                    int lvl = Math.Max(0, Math.Min(4, p.ListLevel));
                    sb.Append("<w:numPr><w:ilvl w:val=\"" + lvl + "\"/><w:numId w:val=\"" + numId + "\"/></w:numPr>");
                }
                sb.Append("</w:pPr>");

                if (!string.IsNullOrEmpty(p.Text))
                {
                    sb.Append("<w:r><w:t xml:space=\"preserve\">");
                    sb.Append(Xml.Esc(p.Text));
                    sb.Append("</w:t></w:r>");
                }
                sb.Append("</w:p>");
            }

            sb.Append("<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/>");
            sb.Append("<w:pgMar w:top=\"1418\" w:right=\"1134\" w:bottom=\"1418\" w:left=\"1134\" w:header=\"851\" w:footer=\"992\" w:gutter=\"0\"/>");
            sb.Append("</w:sectPr></w:body></w:document>");
            return sb.ToString();
        }

        private static string StyleId(ParaKind kind)
        {
            switch (kind)
            {
                case ParaKind.Title: return "Title";
                case ParaKind.Heading1: return "Heading1";
                case ParaKind.Heading2: return "Heading2";
                case ParaKind.Heading3: return "Heading3";
                case ParaKind.Caption: return "Caption";
                case ParaKind.Bullet:
                case ParaKind.Numbered: return "ListParagraph";
                default: return null;
            }
        }

        private static string CoreXml(string title)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" ");
            sb.Append("xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" ");
            sb.Append("xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">");
            sb.Append("<dc:title>" + Xml.Esc(title ?? string.Empty) + "</dc:title>");
            sb.Append("<dc:creator>iWorkConverter</dc:creator>");
            sb.Append("<cp:lastModifiedBy>iWorkConverter</cp:lastModifiedBy>");
            sb.Append("<dcterms:created xsi:type=\"dcterms:W3CDTF\">" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "</dcterms:created>");
            sb.Append("<dcterms:modified xsi:type=\"dcterms:W3CDTF\">" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "</dcterms:modified>");
            sb.Append("</cp:coreProperties>");
            return sb.ToString();
        }

        private const string AppXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties" xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes"><Application>iWorkConverter</Application></Properties>
""";

        private const string ContentTypesXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
<Default Extension="xml" ContentType="application/xml"/>
<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
<Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
<Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"/>
<Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
<Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>
</Types>
""";

        private const string RootRelsXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
<Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/>
</Relationships>
""";

        private const string DocumentRelsXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering" Target="numbering.xml"/>
</Relationships>
""";

        private const string StylesXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Yu Gothic" w:hAnsi="Yu Gothic" w:eastAsia="Yu Gothic" w:cs="Yu Gothic"/><w:sz w:val="21"/><w:szCs w:val="21"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after="120" w:line="288" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>
<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>
<w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:basedOn w:val="Normal"/><w:qFormat/><w:pPr><w:spacing w:before="240" w:after="240"/></w:pPr><w:rPr><w:b/><w:sz w:val="52"/><w:szCs w:val="52"/></w:rPr></w:style>
<w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:qFormat/><w:pPr><w:outlineLvl w:val="0"/><w:spacing w:before="320" w:after="160"/></w:pPr><w:rPr><w:b/><w:sz w:val="36"/><w:szCs w:val="36"/></w:rPr></w:style>
<w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:qFormat/><w:pPr><w:outlineLvl w:val="1"/><w:spacing w:before="280" w:after="140"/></w:pPr><w:rPr><w:b/><w:sz w:val="30"/><w:szCs w:val="30"/></w:rPr></w:style>
<w:style w:type="paragraph" w:styleId="Heading3"><w:name w:val="heading 3"/><w:basedOn w:val="Normal"/><w:qFormat/><w:pPr><w:outlineLvl w:val="2"/><w:spacing w:before="240" w:after="120"/></w:pPr><w:rPr><w:b/><w:sz w:val="26"/><w:szCs w:val="26"/></w:rPr></w:style>
<w:style w:type="paragraph" w:styleId="Caption"><w:name w:val="caption"/><w:basedOn w:val="Normal"/><w:qFormat/><w:rPr><w:i/><w:sz w:val="18"/><w:szCs w:val="18"/></w:rPr></w:style>
<w:style w:type="paragraph" w:styleId="ListParagraph"><w:name w:val="List Paragraph"/><w:basedOn w:val="Normal"/><w:qFormat/><w:pPr><w:ind w:left="425"/><w:spacing w:after="60"/></w:pPr></w:style>
</w:styles>
""";

        private const string NumberingXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
<w:abstractNum w:abstractNumId="0"><w:multiLevelType w:val="hybridMultilevel"/>
<w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="&#9679;"/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="425" w:hanging="425"/></w:pPr></w:lvl>
<w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="&#9675;"/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="850" w:hanging="425"/></w:pPr></w:lvl>
<w:lvl w:ilvl="2"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="&#9642;"/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="1275" w:hanging="425"/></w:pPr></w:lvl>
<w:lvl w:ilvl="3"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="&#9679;"/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="1700" w:hanging="425"/></w:pPr></w:lvl>
<w:lvl w:ilvl="4"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="&#9675;"/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="2125" w:hanging="425"/></w:pPr></w:lvl>
</w:abstractNum>
<w:abstractNum w:abstractNumId="1"><w:multiLevelType w:val="hybridMultilevel"/>
<w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="425" w:hanging="425"/></w:pPr></w:lvl>
<w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%2."/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="850" w:hanging="425"/></w:pPr></w:lvl>
<w:lvl w:ilvl="2"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%3."/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="1275" w:hanging="425"/></w:pPr></w:lvl>
<w:lvl w:ilvl="3"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%4."/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="1700" w:hanging="425"/></w:pPr></w:lvl>
<w:lvl w:ilvl="4"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%5."/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="2125" w:hanging="425"/></w:pPr></w:lvl>
</w:abstractNum>
<w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
<w:num w:numId="2"><w:abstractNumId w:val="1"/></w:num>
</w:numbering>
""";
    }
}
