using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Kaeru.Models;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Kaeru.Services
{
    /// <summary>Word 文書 (.docx) を中間モデルに読み込む。</summary>
    public class DocxReader
    {
        private static readonly Regex HeadingRx =
            new Regex(@"(?:heading|見出し|標題)\s*([1-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private const double EmuPerCm = 360000.0;

        private MainDocumentPart _main;
        private DocumentModel _model;
        private int _imageCounter;

        public DocumentModel Read(string path)
        {
            _model = new DocumentModel
            {
                SourcePath = path,
                Title = Path.GetFileNameWithoutExtension(path)
            };
            _imageCounter = 0;

            using (var doc = WordprocessingDocument.Open(path, false))
            {
                _main = doc.MainDocumentPart;
                if (_main == null || _main.Document == null || _main.Document.Body == null)
                    throw new InvalidDataException("Word 文書の本文を読み取れませんでした。");

                try
                {
                    var t = doc.PackageProperties.Title;
                    if (!string.IsNullOrWhiteSpace(t)) _model.Title = t;
                }
                catch { /* コアプロパティが無い場合は既定値のまま */ }

                ReadContainer(_main.Document.Body);
            }

            Cleanup(_model);
            return _model;
        }

        private void ReadContainer(OpenXmlElement container)
        {
            foreach (var el in container.ChildElements)
            {
                if (el is W.Paragraph p)
                {
                    ReadParagraph(p);
                }
                else if (el is W.Table t)
                {
                    var table = ReadTable(t);
                    if (table.Rows.Count > 0)
                        _model.Blocks.Add(new Block { Kind = BlockKind.Table, Table = table });
                }
                else if (el is W.SdtBlock || el is W.SdtContentBlock)
                {
                    ReadContainer(el);
                }
            }
        }

        // ---------- 段落 ----------

        private void ReadParagraph(W.Paragraph p)
        {
            var images = new List<ImageRef>();
            var runs = ReadInlines(p, images);

            var pr = p.ParagraphProperties;
            string styleId = pr?.ParagraphStyleId?.Val?.Value ?? "";
            string styleName = ResolveStyleName(styleId);
            int headingLevel = DetectHeadingLevel(styleId, styleName);

            if (runs.Count > 0 && !string.IsNullOrWhiteSpace(string.Concat(runs.Select(r => r.Text))))
            {
                if (headingLevel > 0)
                {
                    _model.Blocks.Add(new Block { Kind = BlockKind.Heading, Level = headingLevel, Runs = runs });
                }
                else if (pr?.NumberingProperties != null)
                {
                    int ilvl = pr.NumberingProperties.NumberingLevelReference?.Val?.Value ?? 0;
                    int numId = pr.NumberingProperties.NumberingId?.Val?.Value ?? 0;
                    _model.Blocks.Add(new Block
                    {
                        Kind = BlockKind.ListItem,
                        Level = Math.Max(0, ilvl),
                        Ordered = IsOrderedList(numId, ilvl),
                        Runs = runs
                    });
                }
                else if (IsQuoteStyle(styleId, styleName))
                {
                    _model.Blocks.Add(new Block { Kind = BlockKind.Quote, Runs = runs });
                }
                else
                {
                    _model.Blocks.Add(new Block { Kind = BlockKind.Paragraph, Runs = runs });
                }
            }

            foreach (var img in images)
                _model.Blocks.Add(new Block { Kind = BlockKind.Image, Image = img });

            bool pageBreak = p.Descendants<W.Break>()
                .Any(b => b.Type != null && string.Equals(b.Type.ToString(), "page", StringComparison.OrdinalIgnoreCase));
            if (pageBreak)
                _model.Blocks.Add(new Block { Kind = BlockKind.PageBreak });
        }

        private List<InlineRun> ReadInlines(OpenXmlElement container, List<ImageRef> images, string link = null)
        {
            var result = new List<InlineRun>();

            foreach (var child in container.ChildElements)
            {
                if (child is W.Run run)
                {
                    AppendRun(run, result, images, link);
                }
                else if (child is W.Hyperlink hl)
                {
                    string url = ResolveHyperlink(hl);
                    result.AddRange(ReadInlines(hl, images, url ?? link));
                }
                else if (child is W.SimpleField fld)
                {
                    result.AddRange(ReadInlines(fld, images, link));
                }
                else if (child is W.SdtRun || child is W.SdtContentRun)
                {
                    result.AddRange(ReadInlines(child, images, link));
                }
            }

            return MergeRuns(result);
        }

        private void AppendRun(W.Run run, List<InlineRun> result, List<ImageRef> images, string link)
        {
            var rp = run.RunProperties;
            bool bold = IsOn(rp?.Bold);
            bool italic = IsOn(rp?.Italic);
            bool code = IsMonospace(rp);

            var sb = new StringBuilder();
            foreach (var c in run.ChildElements)
            {
                if (c is W.Text tx) sb.Append(tx.Text);
                else if (c is W.TabChar) sb.Append('\t');
                else if (c is W.Break) sb.Append('\n');
                else if (c is W.NoBreakHyphen) sb.Append('-');
                else if (c is W.Drawing drawing) ExtractImage(drawing, images);
                else if (c is W.Picture pict) ExtractLegacyImage(pict, images);
            }

            if (sb.Length > 0)
            {
                result.Add(new InlineRun
                {
                    Text = sb.ToString(),
                    Bold = bold,
                    Italic = italic,
                    Code = code,
                    Link = link
                });
            }
        }

        private static List<InlineRun> MergeRuns(List<InlineRun> runs)
        {
            var merged = new List<InlineRun>();
            foreach (var r in runs)
            {
                var last = merged.Count > 0 ? merged[merged.Count - 1] : null;
                if (last != null && last.Bold == r.Bold && last.Italic == r.Italic &&
                    last.Code == r.Code && last.Link == r.Link)
                {
                    last.Text += r.Text;
                }
                else
                {
                    merged.Add(r.Clone());
                }
            }
            return merged;
        }

        // ---------- 表 ----------

        private TableModel ReadTable(W.Table t)
        {
            var model = new TableModel();
            foreach (var tr in t.Elements<W.TableRow>())
            {
                var row = new TableRowModel();
                foreach (var tc in tr.Elements<W.TableCell>())
                {
                    var cell = new TableCellModel();
                    var parts = new List<InlineRun>();
                    foreach (var cp in tc.Elements<W.Paragraph>())
                    {
                        if (parts.Count > 0) parts.Add(new InlineRun { Text = " " });
                        parts.AddRange(ReadInlines(cp, new List<ImageRef>()));
                    }
                    cell.Runs = MergeRuns(parts);
                    row.Cells.Add(cell);
                }
                if (row.Cells.Count > 0) model.Rows.Add(row);
            }
            return model;
        }

        // ---------- 画像 ----------

        private void ExtractImage(W.Drawing drawing, List<ImageRef> images)
        {
            var blip = drawing.Descendants<A.Blip>().FirstOrDefault();
            string rid = blip?.Embed?.Value;
            if (string.IsNullOrEmpty(rid)) return;

            double wCm = 0, hCm = 0;
            var extent = drawing.Descendants<DW.Extent>().FirstOrDefault();
            if (extent != null)
            {
                if (extent.Cx != null) wCm = extent.Cx.Value / EmuPerCm;
                if (extent.Cy != null) hCm = extent.Cy.Value / EmuPerCm;
            }

            var img = LoadImagePart(rid, wCm, hCm);
            if (img != null) images.Add(img);
        }

        private void ExtractLegacyImage(W.Picture pict, List<ImageRef> images)
        {
            var imageData = pict.Descendants<DocumentFormat.OpenXml.Vml.ImageData>().FirstOrDefault();
            string rid = imageData?.RelationshipId?.Value;
            if (string.IsNullOrEmpty(rid)) return;

            var img = LoadImagePart(rid, 0, 0);
            if (img != null) images.Add(img);
        }

        private ImageRef LoadImagePart(string rid, double wCm, double hCm)
        {
            try
            {
                var part = _main.GetPartById(rid) as ImagePart;
                if (part == null) return null;

                byte[] data;
                using (var s = part.GetStream(FileMode.Open, FileAccess.Read))
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    data = ms.ToArray();
                }
                if (data.Length == 0) return null;

                _imageCounter++;
                string ext = ExtensionFor(part.ContentType);
                var img = new ImageRef
                {
                    FileName = string.Format("image{0:D3}{1}", _imageCounter, ext),
                    Data = data,
                    MediaType = part.ContentType,
                    WidthCm = wCm > 0.1 ? Math.Min(wCm, 16.0) : 12.0,
                    HeightCm = hCm > 0.1 ? hCm : 0.0
                };
                if (img.HeightCm <= 0.1) img.HeightCm = img.WidthCm * 0.66;
                if (wCm > 16.0 && hCm > 0.1) img.HeightCm = hCm * (16.0 / wCm);

                _model.Images.Add(img);
                return img;
            }
            catch
            {
                return null;
            }
        }

        private static string ExtensionFor(string contentType)
        {
            switch ((contentType ?? "").ToLowerInvariant())
            {
                case "image/jpeg": return ".jpg";
                case "image/gif": return ".gif";
                case "image/bmp": return ".bmp";
                case "image/tiff": return ".tif";
                case "image/x-emf": return ".emf";
                case "image/x-wmf": return ".wmf";
                default: return ".png";
            }
        }

        // ---------- スタイル判定 ----------

        private string ResolveStyleName(string styleId)
        {
            if (string.IsNullOrEmpty(styleId)) return "";
            var styles = _main.StyleDefinitionsPart?.Styles;
            if (styles == null) return styleId;
            var s = styles.Elements<W.Style>().FirstOrDefault(x => x.StyleId != null && x.StyleId.Value == styleId);
            return s?.StyleName?.Val?.Value ?? styleId;
        }

        private static int DetectHeadingLevel(string styleId, string styleName)
        {
            foreach (var candidate in new[] { styleId ?? "", styleName ?? "" })
            {
                if (candidate.Length == 0) continue;
                var m = HeadingRx.Match(candidate);
                if (m.Success) return Math.Min(6, int.Parse(m.Groups[1].Value));
                if (candidate.Equals("Title", StringComparison.OrdinalIgnoreCase) || candidate == "表題")
                    return 1;
                if (candidate.Equals("Subtitle", StringComparison.OrdinalIgnoreCase) || candidate == "副題")
                    return 2;
            }
            return 0;
        }

        private static bool IsQuoteStyle(string styleId, string styleName)
        {
            foreach (var c in new[] { styleId ?? "", styleName ?? "" })
            {
                if (c.IndexOf("Quote", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (c.Contains("引用")) return true;
            }
            return false;
        }

        private static bool IsOn(W.OnOffType v)
        {
            if (v == null) return false;
            if (v.Val == null) return true;
            return v.Val.Value;
        }

        private static bool IsMonospace(W.RunProperties rp)
        {
            string f = rp?.RunFonts?.Ascii?.Value;
            if (string.IsNullOrEmpty(f)) return false;
            f = f.ToLowerInvariant();
            return f.Contains("mono") || f.Contains("consolas") || f.Contains("courier") || f.Contains("ゴシック等幅");
        }

        private bool IsOrderedList(int numId, int ilvl)
        {
            try
            {
                var numbering = _main.NumberingDefinitionsPart?.Numbering;
                if (numbering == null || numId == 0) return false;

                var instance = numbering.Elements<W.NumberingInstance>()
                    .FirstOrDefault(n => n.NumberID != null && n.NumberID.Value == numId);
                int? absId = instance?.AbstractNumId?.Val?.Value;
                if (absId == null) return false;

                var abs = numbering.Elements<W.AbstractNum>()
                    .FirstOrDefault(a => a.AbstractNumberId != null && a.AbstractNumberId.Value == absId.Value);
                var lvl = abs?.Elements<W.Level>()
                    .FirstOrDefault(l => l.LevelIndex != null && l.LevelIndex.Value == ilvl)
                          ?? abs?.Elements<W.Level>().FirstOrDefault();

                string fmt = lvl?.NumberingFormat?.Val?.ToString() ?? "bullet";
                return !fmt.Equals("bullet", StringComparison.OrdinalIgnoreCase)
                       && !fmt.Equals("none", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private string ResolveHyperlink(W.Hyperlink hl)
        {
            try
            {
                string rid = hl.Id?.Value;
                if (string.IsNullOrEmpty(rid)) return null;
                var rel = _main.HyperlinkRelationships.FirstOrDefault(r => r.Id == rid);
                return rel?.Uri?.ToString();
            }
            catch
            {
                return null;
            }
        }

        // ---------- 後処理 ----------

        private static void Cleanup(DocumentModel model)
        {
            var blocks = new List<Block>();
            foreach (var b in model.Blocks)
            {
                if (b.IsEmpty) continue;
                foreach (var r in b.Runs)
                    r.Text = r.Text.Replace("\u00a0", " ").Replace("\r", "");
                blocks.Add(b);
            }
            // 連続するページ区切りをまとめる
            var final = new List<Block>();
            foreach (var b in blocks)
            {
                if (b.Kind == BlockKind.PageBreak && final.Count > 0 &&
                    final[final.Count - 1].Kind == BlockKind.PageBreak) continue;
                final.Add(b);
            }
            model.Blocks = final;
        }
    }
}
