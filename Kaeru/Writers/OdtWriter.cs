using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Kaeru.Models;

namespace Kaeru.Writers
{
    /// <summary>
    /// 中間モデルを ODF テキスト文書 (.odt) として書き出す。
    /// 外部ライブラリを使わず、OpenDocument 1.2 のパッケージ（ZIP）を直接生成する。
    /// </summary>
    public class OdtWriter
    {
        private const string MimeType = "application/vnd.oasis.opendocument.text";

        public void Write(DocumentModel doc, string outputPath)
        {
            var images = new List<ImageRef>();
            string content = BuildContentXml(doc, images);

            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                // mimetype は非圧縮で最初に格納する必要がある
                var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
                using (var s = mime.Open())
                {
                    var bytes = Encoding.ASCII.GetBytes(MimeType);
                    s.Write(bytes, 0, bytes.Length);
                }

                AddText(zip, "content.xml", content);
                AddText(zip, "styles.xml", BuildStylesXml());
                AddText(zip, "meta.xml", BuildMetaXml(doc));

                foreach (var img in images)
                {
                    var entry = zip.CreateEntry("Pictures/" + img.FileName, CompressionLevel.Optimal);
                    using (var s = entry.Open())
                        s.Write(img.Data, 0, img.Data.Length);
                }

                AddText(zip, "META-INF/manifest.xml", BuildManifestXml(images));
            }
        }

        private static void AddText(ZipArchive zip, string path, string text)
        {
            var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
            using (var s = entry.Open())
            using (var w = new StreamWriter(s, new UTF8Encoding(false)))
                w.Write(text);
        }

        // ---------- content.xml ----------

        private string BuildContentXml(DocumentModel doc, List<ImageRef> usedImages)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<office:document-content " + Namespaces + " office:version=\"1.2\">");
            sb.AppendLine("  <office:automatic-styles>");
            sb.AppendLine("    <style:style style:name=\"T_b\" style:family=\"text\"><style:text-properties fo:font-weight=\"bold\" style:font-weight-asian=\"bold\"/></style:style>");
            sb.AppendLine("    <style:style style:name=\"T_i\" style:family=\"text\"><style:text-properties fo:font-style=\"italic\" style:font-style-asian=\"italic\"/></style:style>");
            sb.AppendLine("    <style:style style:name=\"T_bi\" style:family=\"text\"><style:text-properties fo:font-weight=\"bold\" fo:font-style=\"italic\" style:font-weight-asian=\"bold\" style:font-style-asian=\"italic\"/></style:style>");
            sb.AppendLine("    <style:style style:name=\"T_code\" style:family=\"text\"><style:text-properties style:font-name=\"Consolas\"/></style:style>");
            sb.AppendLine("    <style:style style:name=\"P_break\" style:family=\"paragraph\" style:parent-style-name=\"Standard\"><style:paragraph-properties fo:break-before=\"page\"/></style:style>");
            sb.AppendLine("    <style:style style:name=\"Gr1\" style:family=\"graphic\"><style:graphic-properties text:anchor-type=\"as-char\" style:vertical-pos=\"middle\" style:vertical-rel=\"text\"/></style:style>");
            sb.AppendLine(BulletListStyle("L_bullet"));
            sb.AppendLine(NumberListStyle("L_number"));
            sb.AppendLine("  </office:automatic-styles>");
            sb.AppendLine("  <office:body>");
            sb.AppendLine("    <office:text>");

            int imgIndex = 0;
            int i = 0;
            while (i < doc.Blocks.Count)
            {
                var b = doc.Blocks[i];

                switch (b.Kind)
                {
                    case BlockKind.Heading:
                        int lvl = Math.Min(6, Math.Max(1, b.Level));
                        sb.AppendLine("      <text:h text:style-name=\"Heading_20_" + lvl +
                                      "\" text:outline-level=\"" + lvl + "\">" + Inline(b.Runs) + "</text:h>");
                        i++;
                        break;

                    case BlockKind.ListItem:
                        i = WriteList(sb, doc, i);
                        break;

                    case BlockKind.Quote:
                        sb.AppendLine("      <text:p text:style-name=\"Quotations\">" + Inline(b.Runs) + "</text:p>");
                        i++;
                        break;

                    case BlockKind.Image:
                        imgIndex++;
                        usedImages.Add(b.Image);
                        WriteImage(sb, b.Image, imgIndex);
                        i++;
                        break;

                    case BlockKind.Table:
                        WriteTable(sb, b.Table, i);
                        i++;
                        break;

                    case BlockKind.PageBreak:
                        sb.AppendLine("      <text:p text:style-name=\"P_break\"/>");
                        i++;
                        break;

                    default:
                        foreach (var line in SplitLines(b.Runs))
                            sb.AppendLine("      <text:p text:style-name=\"Text_20_body\">" + line + "</text:p>");
                        i++;
                        break;
                }
            }

            sb.AppendLine("    </office:text>");
            sb.AppendLine("  </office:body>");
            sb.AppendLine("</office:document-content>");
            return sb.ToString();
        }

        private int WriteList(StringBuilder sb, DocumentModel doc, int start)
        {
            bool ordered = doc.Blocks[start].Ordered;
            string styleName = ordered ? "L_number" : "L_bullet";
            sb.AppendLine("      <text:list text:style-name=\"" + styleName + "\">");

            int i = start;
            while (i < doc.Blocks.Count &&
                   doc.Blocks[i].Kind == BlockKind.ListItem &&
                   doc.Blocks[i].Ordered == ordered)
            {
                sb.AppendLine("        <text:list-item><text:p text:style-name=\"List_20_Contents\">" +
                              Inline(doc.Blocks[i].Runs) + "</text:p></text:list-item>");
                i++;
            }

            sb.AppendLine("      </text:list>");
            return i;
        }

        private void WriteImage(StringBuilder sb, ImageRef img, int index)
        {
            string w = img.WidthCm.ToString("0.##", CultureInfo.InvariantCulture);
            string h = (img.HeightCm > 0.1 ? img.HeightCm : img.WidthCm * 0.66).ToString("0.##", CultureInfo.InvariantCulture);
            sb.AppendLine("      <text:p text:style-name=\"Standard\">" +
                          "<draw:frame draw:style-name=\"Gr1\" draw:name=\"Image" + index + "\" " +
                          "text:anchor-type=\"as-char\" svg:width=\"" + w + "cm\" svg:height=\"" + h + "cm\" draw:z-index=\"0\">" +
                          "<draw:image xlink:href=\"Pictures/" + Esc(img.FileName) + "\" xlink:type=\"simple\" " +
                          "xlink:show=\"embed\" xlink:actuate=\"onLoad\"/></draw:frame></text:p>");
        }

        private void WriteTable(StringBuilder sb, TableModel table, int index)
        {
            int cols = table.ColumnCount;
            if (cols == 0) return;

            sb.AppendLine("      <table:table table:name=\"Table" + index + "\">");
            sb.AppendLine("        <table:table-column table:number-columns-repeated=\"" + cols + "\"/>");

            foreach (var row in table.Rows)
            {
                sb.AppendLine("        <table:table-row>");
                for (int c = 0; c < cols; c++)
                {
                    string inner = c < row.Cells.Count ? Inline(row.Cells[c].Runs) : "";
                    sb.AppendLine("          <table:table-cell office:value-type=\"string\">" +
                                  "<text:p text:style-name=\"Table_20_Contents\">" + inner + "</text:p></table:table-cell>");
                }
                sb.AppendLine("        </table:table-row>");
            }

            sb.AppendLine("      </table:table>");
        }

        private IEnumerable<string> SplitLines(List<InlineRun> runs)
        {
            string joined = Inline(runs);
            var parts = joined.Split(new[] { "\n" }, StringSplitOptions.None);
            bool any = false;
            foreach (var p in parts)
            {
                any = true;
                yield return p;
            }
            if (!any) yield return "";
        }

        private string Inline(List<InlineRun> runs)
        {
            var sb = new StringBuilder();
            foreach (var r in runs)
            {
                string text = Esc(r.Text);
                if (text.Length == 0) continue;

                string style = null;
                if (r.Code) style = "T_code";
                else if (r.Bold && r.Italic) style = "T_bi";
                else if (r.Bold) style = "T_b";
                else if (r.Italic) style = "T_i";

                string inner = style == null ? text : "<text:span text:style-name=\"" + style + "\">" + text + "</text:span>";

                if (!string.IsNullOrEmpty(r.Link))
                    inner = "<text:a xlink:type=\"simple\" xlink:href=\"" + Esc(r.Link) + "\">" + inner + "</text:a>";

                sb.Append(inner);
            }
            return sb.ToString();
        }

        // ---------- 補助 XML ----------

        private static string BulletListStyle(string name)
        {
            var sb = new StringBuilder();
            sb.AppendLine("    <text:list-style style:name=\"" + name + "\">");
            for (int i = 1; i <= 3; i++)
            {
                double indent = 0.635 * i;
                sb.AppendLine("      <text:list-level-style-bullet text:level=\"" + i + "\" text:bullet-char=\"•\">" +
                              "<style:list-level-properties text:space-before=\"" +
                              indent.ToString("0.###", CultureInfo.InvariantCulture) +
                              "cm\" text:min-label-width=\"0.635cm\"/></text:list-level-style-bullet>");
            }
            sb.Append("    </text:list-style>");
            return sb.ToString();
        }

        private static string NumberListStyle(string name)
        {
            var sb = new StringBuilder();
            sb.AppendLine("    <text:list-style style:name=\"" + name + "\">");
            for (int i = 1; i <= 3; i++)
            {
                double indent = 0.635 * i;
                sb.AppendLine("      <text:list-level-style-number text:level=\"" + i + "\" style:num-suffix=\".\" style:num-format=\"1\">" +
                              "<style:list-level-properties text:space-before=\"" +
                              indent.ToString("0.###", CultureInfo.InvariantCulture) +
                              "cm\" text:min-label-width=\"0.635cm\"/></text:list-level-style-number>");
            }
            sb.Append("    </text:list-style>");
            return sb.ToString();
        }

        private static string BuildStylesXml()
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<office:document-styles " + Namespaces + " office:version=\"1.2\">");
            sb.AppendLine("  <office:styles>");
            sb.AppendLine("    <style:style style:name=\"Standard\" style:family=\"paragraph\" style:class=\"text\">");
            sb.AppendLine("      <style:paragraph-properties fo:margin-top=\"0cm\" fo:margin-bottom=\"0.25cm\"/>");
            sb.AppendLine("    </style:style>");
            sb.AppendLine("    <style:style style:name=\"Text_20_body\" style:display-name=\"Text body\" style:family=\"paragraph\" style:parent-style-name=\"Standard\" style:class=\"text\"/>");
            sb.AppendLine("    <style:style style:name=\"List_20_Contents\" style:display-name=\"List Contents\" style:family=\"paragraph\" style:parent-style-name=\"Standard\" style:class=\"list\"/>");
            sb.AppendLine("    <style:style style:name=\"Table_20_Contents\" style:display-name=\"Table Contents\" style:family=\"paragraph\" style:parent-style-name=\"Standard\" style:class=\"extra\"/>");
            sb.AppendLine("    <style:style style:name=\"Quotations\" style:family=\"paragraph\" style:parent-style-name=\"Standard\" style:class=\"html\">");
            sb.AppendLine("      <style:paragraph-properties fo:margin-left=\"1cm\" fo:margin-right=\"1cm\" fo:margin-top=\"0.25cm\" fo:margin-bottom=\"0.25cm\"/>");
            sb.AppendLine("    </style:style>");
            sb.AppendLine("    <style:style style:name=\"Heading\" style:family=\"paragraph\" style:parent-style-name=\"Standard\" style:class=\"text\">");
            sb.AppendLine("      <style:paragraph-properties fo:margin-top=\"0.42cm\" fo:margin-bottom=\"0.21cm\" fo:keep-with-next=\"always\"/>");
            sb.AppendLine("      <style:text-properties fo:font-weight=\"bold\" style:font-weight-asian=\"bold\"/>");
            sb.AppendLine("    </style:style>");

            double[] sizes = { 18, 16, 14, 12, 11, 11 };
            for (int i = 1; i <= 6; i++)
            {
                sb.AppendLine("    <style:style style:name=\"Heading_20_" + i + "\" style:display-name=\"Heading " + i +
                              "\" style:family=\"paragraph\" style:parent-style-name=\"Heading\" style:default-outline-level=\"" + i + "\">");
                sb.AppendLine("      <style:text-properties fo:font-size=\"" +
                              sizes[i - 1].ToString("0.#", CultureInfo.InvariantCulture) + "pt\" style:font-size-asian=\"" +
                              sizes[i - 1].ToString("0.#", CultureInfo.InvariantCulture) + "pt\"/>");
                sb.AppendLine("    </style:style>");
            }

            sb.AppendLine("  </office:styles>");
            sb.AppendLine("  <office:automatic-styles>");
            sb.AppendLine("    <style:page-layout style:name=\"pm1\">");
            sb.AppendLine("      <style:page-layout-properties fo:page-width=\"21cm\" fo:page-height=\"29.7cm\" style:print-orientation=\"portrait\" fo:margin-top=\"2cm\" fo:margin-bottom=\"2cm\" fo:margin-left=\"2cm\" fo:margin-right=\"2cm\"/>");
            sb.AppendLine("    </style:page-layout>");
            sb.AppendLine("  </office:automatic-styles>");
            sb.AppendLine("  <office:master-styles>");
            sb.AppendLine("    <style:master-page style:name=\"Standard\" style:page-layout-name=\"pm1\"/>");
            sb.AppendLine("  </office:master-styles>");
            sb.AppendLine("</office:document-styles>");
            return sb.ToString();
        }

        private static string BuildMetaXml(DocumentModel doc)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<office:document-meta " + Namespaces +
                          " xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:meta=\"urn:oasis:names:tc:opendocument:xmlns:meta:1.0\" office:version=\"1.2\">");
            sb.AppendLine("  <office:meta>");
            sb.AppendLine("    <meta:generator>Kaeru 1.0</meta:generator>");
            if (!string.IsNullOrWhiteSpace(doc.Title))
                sb.AppendLine("    <dc:title>" + Esc(doc.Title) + "</dc:title>");
            sb.AppendLine("    <meta:creation-date>" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + "</meta:creation-date>");
            sb.AppendLine("  </office:meta>");
            sb.AppendLine("</office:document-meta>");
            return sb.ToString();
        }

        private static string BuildManifestXml(List<ImageRef> images)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\">");
            sb.AppendLine("  <manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"" + MimeType + "\"/>");
            sb.AppendLine("  <manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>");
            sb.AppendLine("  <manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/>");
            sb.AppendLine("  <manifest:file-entry manifest:full-path=\"meta.xml\" manifest:media-type=\"text/xml\"/>");
            foreach (var img in images.GroupBy(x => x.FileName).Select(g => g.First()))
                sb.AppendLine("  <manifest:file-entry manifest:full-path=\"Pictures/" + Esc(img.FileName) +
                              "\" manifest:media-type=\"" + Esc(img.MediaType ?? "image/png") + "\"/>");
            sb.AppendLine("</manifest:manifest>");
            return sb.ToString();
        }

        private const string Namespaces =
            "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
            "xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" " +
            "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" " +
            "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" " +
            "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" " +
            "xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" " +
            "xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
            "xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\"";

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    case '\t': sb.Append("<text:tab/>"); break;
                    case '\n': sb.Append('\n'); break;
                    default:
                        if (c < 0x20) break;
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
