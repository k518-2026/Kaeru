using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using PdfToWord.Models;

namespace PdfToWord.Pdf
{
    /// <summary>読み取り時の設定。</summary>
    /// <summary>パスワードが要る、または合っていないときに投げる。</summary>
    public sealed class PdfPasswordException : Exception
    {
        public PdfPasswordException(string message) : base(message) { }
    }

    public sealed class PdfReadOptions
    {
        public bool DetectHeadings = true;
        public bool DetectLists = true;
        public bool DetectTables = true;
        public bool MergeLines = true;
        public bool RemoveHeaderFooter = true;
        public bool ExtractImages = true;
        public bool InsertPageBreaks;
        public int PageFrom;   // 1 始まり。0 なら先頭から
        public int PageTo;     // 0 なら最後まで
        public HashSet<int> PageNumbers;      // 指定があればそのページだけ（1 始まり）
        public bool DetectColumns = true;     // 段組みを判別する
        public bool KeepLineBreaks;           // PDF の 1 行をそのまま 1 段落にする
        public string Password = "";          // 開くのにパスワードが要る場合
    }

    /// <summary>PDF を読み、段落・見出し・表・画像からなる文書モデルを作る。</summary>
    public sealed class PdfDocumentReader
    {
        private sealed class PageData
        {
            public int Number;
            public double Width;
            public double Height;
            public List<TextLine> Lines = new List<TextLine>();
            public List<DetectedTable> Tables = new List<DetectedTable>();
            public List<ImagePlacement> Images = new List<ImagePlacement>();
            public int ColumnCount = 1;
            public double ColumnSplit = double.NaN;
            public LayoutAnalyzer.ColumnGutter Gutter;
            public LayoutAnalyzer.ColumnGutter Candidate;   // このページ単独で見つかった切れ目
            public List<Glyph> Glyphs = new List<Glyph>();
            public List<RuleLine> Rules = new List<RuleLine>();
        }

        public DocumentModel Read(string path, PdfReadOptions options, Action<int, int> progress, CancellationToken token)
        {
            if (options == null) options = new PdfReadOptions();
            var model = new DocumentModel();

            using (PdfFile file = PdfFile.Open(path, options.Password))
            {
                if (!string.IsNullOrEmpty(file.Warning)) model.Notes.Add(file.Warning);
                if (file.PasswordRequired)
                    throw new PdfPasswordException(string.IsNullOrEmpty(options.Password)
                        ? "この PDF はパスワードで保護されています。パスワードを入力してください。"
                        : "パスワードが違うようです。もう一度確かめてください。");

                model.Title = CleanTitle(file.GetDocumentInfo("Title"));
                model.Author = file.GetDocumentInfo("Author");
                model.PageCount = file.Pages.Count;

                int from = options.PageFrom > 0 ? options.PageFrom : 1;
                int to = options.PageTo > 0 ? Math.Min(options.PageTo, file.Pages.Count) : file.Pages.Count;
                if (from > to) { model.Notes.Add("指定されたページ範囲に該当するページがありません。"); return model; }

                var renderer = new ContentRenderer(file);
                var pages = new List<PageData>();
                var sizeCounts = new Dictionary<double, int>();
                var imageUsage = new Dictionary<PdfStreamObject, int>();

                for (int index = from; index <= to; index++)
                {
                    token.ThrowIfCancellationRequested();
                    if (options.PageNumbers != null && options.PageNumbers.Count > 0
                        && !options.PageNumbers.Contains(index)) continue;
                    PdfPageInfo pageInfo = file.Pages[index - 1];
                    PageContent content;
                    try
                    {
                        content = renderer.Render(pageInfo);
                        content = PreferBetterRotation(renderer, pageInfo, content);
                    }
                    catch (Exception exception)
                    {
                        model.Notes.Add(index + " ページの解析に失敗しました（" + exception.Message + "）。");
                        continue;
                    }

                    var page = new PageData
                    {
                        Number = index,
                        Width = content.Width,
                        Height = content.Height
                    };
                    page.Glyphs = content.Glyphs;
                    page.Rules = content.Rules;
                    page.Candidate = options.DetectColumns
                        ? LayoutAnalyzer.FindGutter(content.Glyphs, content.Rules, content.Width)
                        : null;

                    foreach (Glyph glyph in content.Glyphs)
                    {
                        double key = Math.Round(glyph.Size * 2, MidpointRounding.AwayFromZero) / 2;
                        int count;
                        sizeCounts.TryGetValue(key, out count);
                        sizeCounts[key] = count + 1;
                    }

                    if (options.ExtractImages)
                    {
                        foreach (ImagePlacement placement in content.Images)
                        {
                            if (placement.Width < 12 || placement.Height < 12) continue;
                            page.Images.Add(placement);
                            int count;
                            imageUsage.TryGetValue(placement.Stream, out count);
                            imageUsage[placement.Stream] = count + 1;
                        }
                    }

                    if (model.PageWidthPoints <= 0 && content.Width > 100 && content.Height > 100)
                    {
                        model.PageWidthPoints = content.Width;
                        model.PageHeightPoints = content.Height;
                    }
                    pages.Add(page);
                    if (progress != null) progress(index - from + 1, to - from + 1);
                }

                // 段組みは文書全体で共通していることが多いので、
                // はっきり見つかったページの値をすべてのページに当てはめる
                LayoutAnalyzer.ColumnGutter documentGutter = ChooseDocumentGutter(pages);

                foreach (PageData page in pages)
                {
                    token.ThrowIfCancellationRequested();
                    LayoutAnalyzer.ColumnGutter gutter = UsableGutter(page, documentGutter);
                    page.Lines = LayoutAnalyzer.MergeSuperscripts(
                        LayoutAnalyzer.BuildLines(page.Glyphs, gutter));
                    page.ColumnCount = gutter != null ? 2 : 1;
                    page.ColumnSplit = gutter != null ? gutter.Center : double.NaN;
                    page.Gutter = gutter;

                    foreach (TextLine line in page.Lines) line.PageNumber = page.Number;
                    LayoutAnalyzer.MarkMarginCandidates(page.Lines, page.Height);

                    if (options.DetectTables)
                    {
                        try { page.Tables = TableDetector.Detect(page.Rules, page.Lines); }
                        catch (Exception) { page.Tables = new List<DetectedTable>(); }
                    }
                }

                double bodySize = FindBodySize(sizeCounts);

                HashSet<string> repeated = new HashSet<string>(StringComparer.Ordinal);
                if (options.RemoveHeaderFooter)
                {
                    var linesPerPage = new List<List<TextLine>>();
                    foreach (PageData page in pages) linesPerPage.Add(page.Lines);
                    double height = pages.Count > 0 ? pages[0].Height : 0;
                    repeated = LayoutAnalyzer.FindRepeatedLines(linesPerPage, height);
                }

                var decorativeImages = new HashSet<PdfStreamObject>();
                if (pages.Count >= 3)
                {
                    foreach (var pair in imageUsage)
                    {
                        if (pair.Value >= Math.Max(3, pages.Count * 0.6)) decorativeImages.Add(pair.Key);
                    }
                }

                var builder = new BlockBuilder(model, options, bodySize);
                foreach (PageData page in pages)
                {
                    token.ThrowIfCancellationRequested();
                    BuildPage(file, model, builder, page, options, repeated, decorativeImages);
                }
                builder.Flush();

                if (model.Blocks.Count == 0)
                {
                    model.Notes.Add("文字を取り出せませんでした。スキャン画像だけの PDF の可能性があります（OCR には未対応です）。");
                }
            }
            return model;
        }

        private void BuildPage(PdfFile file, DocumentModel model, BlockBuilder builder, PageData page,
            PdfReadOptions options, HashSet<string> repeated, HashSet<PdfStreamObject> decorativeImages)
        {
            var tableLines = new HashSet<TextLine>();
            foreach (DetectedTable table in page.Tables)
            {
                foreach (TextLine line in table.UsedLines) tableLines.Add(line);
            }

            var items = new List<PageItem>();
            foreach (TextLine line in page.Lines)
            {
                if (line.IsEmpty) continue;
                if (tableLines.Contains(line)) continue;
                if (options.RemoveHeaderFooter && line.IsMarginCandidate)
                {
                    string key = LayoutAnalyzer.Normalize(line.Text);
                    if (repeated.Contains(key)) continue;
                    if (LayoutAnalyzer.LooksLikePageNumber(line.Text)) continue;
                }
                items.Add(new PageItem
                {
                    Line = line,
                    Top = line.Top,
                    Bottom = line.Bottom,
                    Left = line.Left,
                    Right = line.Right,
                    Column = (line.SpansColumns || line.IsMarginCandidate) ? -1 : line.Column
                });
            }

            foreach (DetectedTable table in page.Tables)
            {
                items.Add(new PageItem
                {
                    Table = table.Table,
                    Top = table.Top,
                    Bottom = table.Bottom,
                    Left = table.Left,
                    Right = table.Right,
                    Column = ColumnOfBox(page, table.Left, table.Right)
                });
            }

            foreach (ImagePlacement placement in page.Images)
            {
                if (decorativeImages.Contains(placement.Stream)) continue;
                items.Add(new PageItem
                {
                    Image = placement,
                    Top = placement.Y0,
                    Bottom = placement.Y1,
                    Left = placement.X0,
                    Right = placement.X1,
                    Column = ColumnOfBox(page, placement.X0, placement.X1)
                });
            }

            items = OrderForReading(items, page.ColumnCount > 1);

            // 段の左端と右端を求める
            var columnLeft = new Dictionary<int, double>();
            var columnRight = new Dictionary<int, double>();
            for (int column = 0; column < Math.Max(1, page.ColumnCount); column++)
            {
                var lefts = new List<double>();
                var rights = new List<double>();
                foreach (PageItem item in items)
                {
                    if (item.Line == null || item.Column != column) continue;
                    lefts.Add(item.Left);
                    rights.Add(item.Right);
                }
                lefts.Sort();
                rights.Sort();
                columnLeft[column] = lefts.Count > 0 ? lefts[Math.Min(lefts.Count - 1, lefts.Count / 20)] : 0;
                columnRight[column] = rights.Count > 0 ? rights[Math.Max(0, rights.Count - 1 - rights.Count / 20)] : page.Width;
            }

            if (options.InsertPageBreaks && model.Blocks.Count > 0)
            {
                builder.Flush();
                model.Blocks.Add(new DocBlock { Kind = BlockKind.PageBreak, SourcePage = page.Number });
            }

            foreach (PageItem item in items)
            {
                if (item.Table != null)
                {
                    builder.Flush();
                    model.Blocks.Add(new DocBlock
                    {
                        Kind = BlockKind.Table,
                        Table = item.Table,
                        SourcePage = page.Number
                    });
                    continue;
                }
                if (item.Image != null)
                {
                    string reason;
                    DocImage image = ImageExtractor.Extract(item.Image.Stream, file, out reason);
                    if (image == null)
                    {
                        if (!string.IsNullOrEmpty(reason) && model.Notes.Count < 12
                            && !model.Notes.Contains("画像を取り込めませんでした（" + reason + "）。"))
                            model.Notes.Add("画像を取り込めませんでした（" + reason + "）。");
                        continue;
                    }
                    image.WidthPoints = item.Image.Width;
                    image.HeightPoints = item.Image.Height;
                    builder.Flush();
                    model.Blocks.Add(new DocBlock
                    {
                        Kind = BlockKind.Image,
                        Image = image,
                        SourcePage = page.Number
                    });
                    continue;
                }

                double left = columnLeft.ContainsKey(item.Column) ? columnLeft[item.Column] : 0;
                double right = columnRight.ContainsKey(item.Column) ? columnRight[item.Column] : page.Width;
                builder.Add(item.Line, left, right, page.Number);
            }
        }

        /// <summary>
        /// /Rotate を適用すると行がばらばらになる PDF があるので、
        /// 回転なしのほうが行としてまとまる場合はそちらを採る。
        /// </summary>
        private static PageContent PreferBetterRotation(ContentRenderer renderer, PdfPageInfo pageInfo, PageContent content)
        {
            if (pageInfo.Rotate == 0 || content.Glyphs.Count < 20) return content;
            double rotated = LineQuality(content);
            if (rotated >= 3.0) return content;

            renderer.RotationOverride = 0;
            PageContent upright;
            try { upright = renderer.Render(pageInfo); }
            finally { renderer.RotationOverride = -1; }

            return LineQuality(upright) > rotated ? upright : content;
        }

        /// <summary>1 行あたりの平均文字数。大きいほど行としてまとまっている。</summary>
        private static double LineQuality(PageContent content)
        {
            List<TextLine> lines = LayoutAnalyzer.BuildLines(content.Glyphs);
            if (lines.Count == 0) return 0;
            return (double)content.Glyphs.Count / lines.Count;
        }

        /// <summary>
        /// ページごとに見つかった切れ目から、文書全体で使う値を決める。
        /// 図や参考文献のせいで一部のページで見つからなくても、そろえて扱える。
        /// </summary>
        private static LayoutAnalyzer.ColumnGutter ChooseDocumentGutter(List<PageData> pages)
        {
            var centers = new List<double>();
            var found = new List<LayoutAnalyzer.ColumnGutter>();
            foreach (PageData page in pages)
            {
                if (page.Candidate == null) continue;
                found.Add(page.Candidate);
                centers.Add(page.Candidate.Center);
            }
            if (found.Count == 0) return null;
            if (pages.Count > 2 && found.Count < Math.Max(1, pages.Count * 0.15)) return null;

            centers.Sort();
            double center = centers[centers.Count / 2];

            // 中央に近いものだけを平均して、帯の左右を決める
            double left = 0, right = 0;
            int used = 0;
            foreach (LayoutAnalyzer.ColumnGutter candidate in found)
            {
                if (Math.Abs(candidate.Center - center) > 12) continue;
                left += candidate.Left;
                right += candidate.Right;
                used++;
            }
            if (used == 0) return null;

            return new LayoutAnalyzer.ColumnGutter { Left = left / used, Right = right / used };
        }

        /// <summary>そのページで切れ目を使ってよいか（表のページでは使わない）。</summary>
        private static LayoutAnalyzer.ColumnGutter UsableGutter(PageData page, LayoutAnalyzer.ColumnGutter gutter)
        {
            if (gutter == null) return null;
            foreach (RuleLine rule in page.Rules)
            {
                if (rule.IsHorizontal) continue;
                if (rule.X0 < gutter.Left - 2 || rule.X0 > gutter.Right + 2) continue;
                if (Math.Abs(rule.Y1 - rule.Y0) > 12) return null;   // 帯の中を縦罫線が通る＝表
            }
            return gutter;
        }

        /// <summary>
        /// 図や表がどの段に属するかを、左右の段との重なりの大きさで決める。
        /// 両方の段にしっかり重なっていれば、段をまたぐ扱い（-1）にする。
        /// </summary>
        private static int ColumnOfBox(PageData page, double left, double right)
        {
            if (page.Gutter == null || page.ColumnCount <= 1) return 0;

            double leftWidth = Math.Max(1, page.Gutter.Left);
            double rightWidth = Math.Max(1, page.Width - page.Gutter.Right);
            double inLeft = Math.Max(0, Math.Min(right, page.Gutter.Left) - Math.Max(left, 0));
            double inRight = Math.Max(0, Math.Min(right, page.Width) - Math.Max(left, page.Gutter.Right));

            if (inLeft > leftWidth * 0.45 && inRight > rightWidth * 0.45) return -1;
            return inLeft >= inRight ? 0 : 1;
        }

        /// <summary>
        /// 読む順序に並べ替える。段をまたぐ項目（表題・図・全幅の表）を区切りとして
        /// ページを上から帯に分け、帯の中では左の段 → 右の段の順に読む。
        /// </summary>
        private static List<PageItem> OrderForReading(List<PageItem> items, bool twoColumns)
        {
            items.Sort(delegate (PageItem a, PageItem b)
            {
                int compare = a.Top.CompareTo(b.Top);
                if (compare != 0) return compare;
                return a.Left.CompareTo(b.Left);
            });

            if (!twoColumns) return items;

            var ordered = new List<PageItem>();
            var band = new List<PageItem>();

            foreach (PageItem item in items)
            {
                if (item.Column < 0)
                {
                    FlushBand(band, ordered);
                    ordered.Add(item);
                }
                else band.Add(item);
            }
            FlushBand(band, ordered);
            return ordered;
        }

        private static void FlushBand(List<PageItem> band, List<PageItem> ordered)
        {
            if (band.Count == 0) return;

            band.Sort(delegate (PageItem a, PageItem b)
            {
                int compare = a.Top.CompareTo(b.Top);
                if (compare != 0) return compare;
                return a.Left.CompareTo(b.Left);
            });

            // 右の段がどこで終わっているかを見て、その下にある左の段の行
            // （次の節の見出しなど）は右の段のあとに回す
            // 図や表は段の下まで伸びることがあるので、右の段の「文章」だけで終わりを見る
            double rightBottom = double.MinValue;
            foreach (PageItem item in band)
            {
                if (item.Column == 1 && item.Line != null) rightBottom = Math.Max(rightBottom, item.Bottom);
            }

            var leftPart = new List<PageItem>();
            var rightPart = new List<PageItem>();
            var tail = new List<PageItem>();

            foreach (PageItem item in band)
            {
                if (item.Column == 1) rightPart.Add(item);
                else if (rightBottom > double.MinValue && item.Top > rightBottom + SectionGap(item)) tail.Add(item);
                else leftPart.Add(item);
            }

            ordered.AddRange(leftPart);
            ordered.AddRange(rightPart);
            ordered.AddRange(tail);
            band.Clear();
        }

        /// <summary>
        /// 右の段より下にある左の段の行を「次の節」とみなすための空き。
        /// 段の長さの多少の違いで行が入れ替わらないよう、2 行分ほど余裕をとる。
        /// </summary>
        private static double SectionGap(PageItem item)
        {
            double size = item.Line != null ? item.Line.FontSize : 10.5;
            return Math.Max(24.0, size * 2.5);
        }

        private static int ColumnOf(PageData page, double centerX)
        {
            if (page.ColumnCount <= 1 || double.IsNaN(page.ColumnSplit)) return 0;
            return centerX < page.ColumnSplit ? 0 : 1;
        }

        /// <summary>作成ソフトが自動で入れた無意味な題名を取り除く。</summary>
        private static string CleanTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            string value = title.Trim();
            if (value.StartsWith("Microsoft Word - ", StringComparison.OrdinalIgnoreCase))
                value = value.Substring("Microsoft Word - ".Length).Trim();
            if (value.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".doc", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                || value.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(0, value.LastIndexOf('.'));
            string lower = value.ToLowerInvariant();
            if (lower.Length < 2) return null;
            if (lower == "untitled" || lower == "untitled document" || lower == "document"
                || lower == "no title" || lower == "無題" || lower == "microsoft word")
                return null;
            return value;
        }

        private static double FindBodySize(Dictionary<double, int> sizeCounts)
        {
            double best = 10.5;
            int bestCount = 0;
            foreach (var pair in sizeCounts)
            {
                if (pair.Key < 4 || pair.Key > 40) continue;
                if (pair.Value > bestCount) { bestCount = pair.Value; best = pair.Key; }
            }
            return best <= 0 ? 10.5 : best;
        }
    }

    /// <summary>行を段落・見出し・箇条書きに組み立てる。</summary>
    internal sealed class BlockBuilder
    {
        private readonly DocumentModel _model;
        private readonly PdfReadOptions _options;
        private readonly double _bodySize;

        private DocBlock _pending;
        private TextLine _previousLine;
        private double _previousColumnRight;
        private double _previousColumnLeft = double.NaN;
        private double _pendingContentLeft;

        public BlockBuilder(DocumentModel model, PdfReadOptions options, double bodySize)
        {
            _model = model;
            _options = options;
            _bodySize = bodySize <= 0 ? 10.5 : bodySize;
        }

        public void Flush()
        {
            if (_pending != null)
            {
                Trim(_pending);
                if (!string.IsNullOrWhiteSpace(_pending.PlainText)) _model.Blocks.Add(_pending);
                _pending = null;
            }
            _previousLine = null;
        }

        public void Add(TextLine line, double columnLeft, double columnRight, int pageNumber)
        {
            string text = line.Text.Trim();
            if (text.Length == 0) return;

            int markerLength = 0;
            bool ordered = false;
            bool isList = _options.DetectLists && LooksLikeListItem(text, out markerLength, out ordered);

            double gap = _previousLine != null ? line.Baseline - _previousLine.Baseline : double.MaxValue;
            bool sameFlow = _previousLine != null && _previousLine.PageNumber == pageNumber;
            double indent = line.Left - columnLeft;

            // 「2. 表」のように箇条書きと見分けがつきにくい行は、文字の大きさで判断する
            int headingLevel = 0;
            if (_options.DetectHeadings && (!isList || line.FontSize >= _bodySize * 1.12))
                headingLevel = DetectHeadingLevel(line, text, gap, sameFlow);

            if (headingLevel > 0)
            {
                Flush();
                var heading = new DocBlock { Kind = BlockKind.Heading, HeadingLevel = headingLevel, SourcePage = pageNumber };
                heading.Runs.AddRange(CloneRuns(line.Runs));
                Trim(heading);
                if (!string.IsNullOrWhiteSpace(heading.PlainText)) _model.Blocks.Add(heading);
                _previousLine = line;
                _previousColumnRight = columnRight;
                return;
            }

            if (isList && headingLevel == 0)
            {
                Flush();
                var item = new DocBlock
                {
                    Kind = BlockKind.ListItem,
                    Ordered = ordered,
                    SourcePage = pageNumber,
                    ListLevel = Math.Max(0, Math.Min(4, (int)Math.Round(indent / Math.Max(6.0, _bodySize * 1.6))))
                };
                item.Runs.AddRange(StripPrefix(CloneRuns(line.Runs), CountLeadingSpaces(line.Text) + markerLength));
                Trim(item);
                _pending = item;
                _previousLine = line;
                _previousColumnRight = columnRight;
                _previousColumnLeft = columnLeft;
                _pendingContentLeft = line.Left + _bodySize;
                return;
            }

            bool columnChanged = _previousLine != null && Math.Abs(columnLeft - _previousColumnLeft) > 1;

            bool canMerge = false;
            if (_options.MergeLines && !_options.KeepLineBreaks && _pending != null
                && (columnChanged || !sameFlow) && _pending.Kind == BlockKind.Paragraph)
            {
                // 段やページをまたいで続いている文章をつなぐ
                bool previousFilled = _previousLine.Right >= _previousColumnRight - Math.Max(_bodySize * 2.2, 12);
                bool notIndented = indent <= _bodySize * 0.7;
                bool sameSize = line.FontSize <= _previousLine.FontSize * 1.12
                    && line.FontSize >= _previousLine.FontSize * 0.88;
                canMerge = previousFilled && notIndented && sameSize && !EndsSentence(_previousLine.Text);
            }
            else if (_options.MergeLines && !_options.KeepLineBreaks && _pending != null && sameFlow)
            {
                bool closeEnough = gap > 0 && gap <= Math.Max(_bodySize, line.FontSize) * 2.3;
                bool previousWasFull = _previousLine.Right >= _previousColumnRight - Math.Max(_bodySize * 2.2, 12);
                if (_pending.Kind == BlockKind.Paragraph)
                {
                    bool notIndented = indent <= _bodySize * 0.7;
                    canMerge = closeEnough && previousWasFull && notIndented;
                }
                else if (_pending.Kind == BlockKind.ListItem)
                {
                    canMerge = closeEnough && line.Left >= _pendingContentLeft - _bodySize * 0.8;
                }
            }

            if (canMerge)
            {
                AppendJoined(_pending, line);
                _previousLine = line;
                _previousColumnRight = columnRight;
                _previousColumnLeft = columnLeft;
                return;
            }

            Flush();
            var paragraph = new DocBlock { Kind = BlockKind.Paragraph, SourcePage = pageNumber };
            paragraph.Runs.AddRange(CloneRuns(line.Runs));
            paragraph.Alignment = DetectAlignment(line, columnLeft, columnRight);
            _pending = paragraph;
            _previousLine = line;
            _previousColumnRight = columnRight;
            _previousColumnLeft = columnLeft;
            _pendingContentLeft = line.Left;
        }

        /// <summary>文が言い切りで終わっているか（続きがなさそうか）。</summary>
        private static bool EndsSentence(string text)
        {
            string value = text == null ? "" : text.TrimEnd();
            if (value.Length == 0) return true;
            char last = value[value.Length - 1];
            return last == '。' || last == '．' || last == '.' || last == '！' || last == '?'
                || last == '？' || last == '!' || last == '」' || last == '）';
        }

        private TextAlignment DetectAlignment(TextLine line, double columnLeft, double columnRight)
        {
            double leftGap = line.Left - columnLeft;
            double rightGap = columnRight - line.Right;
            double width = columnRight - columnLeft;
            if (width <= 0) return TextAlignment.Left;
            if (leftGap > _bodySize * 2.5 && rightGap > _bodySize * 2.5
                && Math.Abs(leftGap - rightGap) < Math.Max(_bodySize * 2.0, width * 0.08))
                return TextAlignment.Center;
            if (leftGap > width * 0.45 && rightGap < _bodySize * 1.5) return TextAlignment.Right;
            return TextAlignment.Left;
        }

        private int DetectHeadingLevel(TextLine line, string text, double gap, bool sameFlow)
        {
            if (text.Length > 120) return 0;
            double ratio = line.FontSize / _bodySize;

            if (ratio >= 1.75) return 1;
            if (ratio >= 1.45) return 2;
            if (ratio >= 1.27) return 3;
            if (ratio >= 1.13) return 4;

            bool spacedAbove = !sameFlow || gap > Math.Max(_bodySize, line.FontSize) * 1.55;
            bool endsLikeSentence = text.EndsWith("。", StringComparison.Ordinal)
                || text.EndsWith("．", StringComparison.Ordinal)
                || text.EndsWith(".", StringComparison.Ordinal)
                || text.EndsWith("、", StringComparison.Ordinal)
                || text.EndsWith(",", StringComparison.Ordinal);

            if (spacedAbove && !endsLikeSentence && text.Length <= 60)
            {
                if (IsNumberedHeading(text)) return Math.Min(5, 2 + CountSeparators(text));
                if (line.AllBold && ratio >= 1.0) return 4;
            }
            return 0;
        }

        private static int CountSeparators(string text)
        {
            int count = 0;
            for (int i = 0; i < text.Length && i < 12; i++)
            {
                if (text[i] == '.' || text[i] == '．') count++;
                if (text[i] == ' ' || text[i] == '\u3000') break;
            }
            return Math.Max(0, count - 1);
        }

        /// <summary>「第3章」「3.2 ○○」のような見出しか。</summary>
        private static bool IsNumberedHeading(string text)
        {
            if (text.Length < 2) return false;
            if (text.StartsWith("第", StringComparison.Ordinal))
            {
                for (int i = 1; i < text.Length && i < 8; i++)
                {
                    char c = text[i];
                    if (c == '章' || c == '節' || c == '項' || c == '部' || c == '編') return true;
                    if (!char.IsDigit(c) && "０１２３４５６７８９一二三四五六七八九十".IndexOf(c) < 0) break;
                }
            }
            int index = 0;
            bool sawDigit = false;
            while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '.' || text[index] == '．'))
            {
                if (char.IsDigit(text[index])) sawDigit = true;
                index++;
            }
            if (!sawDigit || index >= text.Length) return false;
            if (index < 2) return false;
            char next = text[index];
            if (next != ' ' && next != '\u3000') return false;
            string rest = text.Substring(index).Trim();
            return rest.Length > 0 && rest.Length <= 50;
        }

        /// <summary>箇条書きの行頭記号を判定する。</summary>
        public static bool LooksLikeListItem(string text, out int markerLength, out bool ordered)
        {
            markerLength = 0;
            ordered = false;
            if (text.Length < 2) return false;

            const string bullets = "\u2022\u30FB\u25CF\u25CB\u25A0\u25A1\u2023\u2043\u00B7\u2219\u25AA\u2013\u2014";
            char first = text[0];
            if (bullets.IndexOf(first) >= 0)
            {
                markerLength = 1;
                while (markerLength < text.Length && (text[markerLength] == ' ' || text[markerLength] == '\u3000'))
                    markerLength++;
                return true;
            }
            if ((first == '-' || first == '*' || first == '\u2022') && text.Length > 1 && text[1] == ' ')
            {
                markerLength = 2;
                return true;
            }
            if (first >= '\u2460' && first <= '\u2473')
            {
                ordered = true;
                markerLength = 1;
                while (markerLength < text.Length && (text[markerLength] == ' ' || text[markerLength] == '\u3000'))
                    markerLength++;
                return true;
            }

            int index = 0;
            bool parenthesis = false;
            if (first == '(' || first == '（') { parenthesis = true; index = 1; }
            int digitStart = index;
            while (index < text.Length && (char.IsDigit(text[index]) || (text[index] >= '０' && text[index] <= '９')))
                index++;
            if (index == digitStart || index - digitStart > 3) return false;
            if (index >= text.Length) return false;

            char terminator = text[index];
            bool valid = parenthesis
                ? (terminator == ')' || terminator == '）')
                : (terminator == '.' || terminator == '．' || terminator == ')' || terminator == '）'
                   || terminator == '、' || terminator == '・');
            if (!valid) return false;
            index++;
            if (index < text.Length && text[index] != ' ' && text[index] != '\u3000') return false;
            while (index < text.Length && (text[index] == ' ' || text[index] == '\u3000')) index++;
            if (index >= text.Length) return false;

            ordered = true;
            markerLength = index;
            return true;
        }

        private static int CountLeadingSpaces(string text)
        {
            int count = 0;
            while (count < text.Length && (text[count] == ' ' || text[count] == '\u3000')) count++;
            return count;
        }

        private static List<InlineRun> CloneRuns(List<InlineRun> runs)
        {
            var result = new List<InlineRun>();
            foreach (InlineRun run in runs) result.Add(run.Clone());
            return result;
        }

        private static List<InlineRun> StripPrefix(List<InlineRun> runs, int count)
        {
            int remaining = count;
            var result = new List<InlineRun>();
            foreach (InlineRun run in runs)
            {
                if (remaining <= 0) { result.Add(run); continue; }
                if (run.Text.Length <= remaining) { remaining -= run.Text.Length; continue; }
                run.Text = run.Text.Substring(remaining);
                remaining = 0;
                result.Add(run);
            }
            return result;
        }

        private void AppendJoined(DocBlock block, TextLine line)
        {
            string existing = block.PlainText;
            string addition = line.Text.Trim();
            if (addition.Length == 0) return;

            List<InlineRun> runs = CloneRuns(line.Runs);
            // 先頭の空白を落とす
            while (runs.Count > 0)
            {
                runs[0].Text = runs[0].Text.TrimStart();
                if (runs[0].Text.Length == 0) runs.RemoveAt(0); else break;
            }
            if (runs.Count == 0) return;

            char lastChar = existing.Length > 0 ? existing[existing.Length - 1] : '\0';
            char firstChar = addition[0];

            if (lastChar == '-' && existing.Length >= 2 && char.IsLetter(existing[existing.Length - 2])
                && char.IsLower(firstChar))
            {
                for (int i = block.Runs.Count - 1; i >= 0; i--)
                {
                    if (block.Runs[i].Text.EndsWith("-", StringComparison.Ordinal))
                    {
                        block.Runs[i].Text = block.Runs[i].Text.Substring(0, block.Runs[i].Text.Length - 1);
                        break;
                    }
                    if (block.Runs[i].Text.Length > 0) break;
                }
            }
            else if (LayoutAnalyzer.IsCjk(lastChar) || LayoutAnalyzer.IsCjk(firstChar))
            {
                // 日本語どうしは空白なしでつなぐ
            }
            else if (lastChar != ' ' && lastChar != '\0')
            {
                if (block.Runs.Count > 0) block.Runs[block.Runs.Count - 1].Text += " ";
                else block.Runs.Add(new InlineRun(" "));
            }

            foreach (InlineRun run in runs)
            {
                InlineRun last = block.Runs.Count > 0 ? block.Runs[block.Runs.Count - 1] : null;
                if (last != null && last.Bold == run.Bold && last.Italic == run.Italic
                    && last.Monospace == run.Monospace && last.Superscript == run.Superscript)
                    last.Text += run.Text;
                else
                    block.Runs.Add(run);
            }
        }

        private static void Trim(DocBlock block)
        {
            while (block.Runs.Count > 0)
            {
                block.Runs[0].Text = block.Runs[0].Text.TrimStart();
                if (block.Runs[0].Text.Length == 0) block.Runs.RemoveAt(0); else break;
            }
            while (block.Runs.Count > 0)
            {
                int last = block.Runs.Count - 1;
                block.Runs[last].Text = block.Runs[last].Text.TrimEnd();
                if (block.Runs[last].Text.Length == 0) block.Runs.RemoveAt(last); else break;
            }
        }
    }
}
