using System;
using System.Collections.Generic;
using System.Text;

namespace PdfToWord.Pdf
{
    /// <summary>2 次元アフィン変換（行ベクトル規約）。</summary>
    public struct Matrix
    {
        public double A, B, C, D, E, F;

        public Matrix(double a, double b, double c, double d, double e, double f)
        {
            A = a; B = b; C = c; D = d; E = e; F = f;
        }

        public static Matrix Identity { get { return new Matrix(1, 0, 0, 1, 0, 0); } }

        /// <summary>this × other。</summary>
        public Matrix Multiply(Matrix other)
        {
            return new Matrix(
                A * other.A + B * other.C,
                A * other.B + B * other.D,
                C * other.A + D * other.C,
                C * other.B + D * other.D,
                E * other.A + F * other.C + other.E,
                E * other.B + F * other.D + other.F);
        }

        public double TransformX(double x, double y) { return A * x + C * y + E; }
        public double TransformY(double x, double y) { return B * x + D * y + F; }

        public double ScaleY { get { return Math.Sqrt(C * C + D * D); } }
        public double ScaleX { get { return Math.Sqrt(A * A + B * B); } }
    }

    /// <summary>配置済みの 1 文字。</summary>
    public sealed class Glyph
    {
        public string Text;
        public double X0;
        public double X1;
        public double Baseline;
        public double Size;
        public bool Bold;
        public bool Italic;
        public bool Monospace;
        public bool Superscript;
        public double Width { get { return Math.Max(0, X1 - X0); } }
    }

    /// <summary>ページ上に置かれた画像。</summary>
    public sealed class ImagePlacement
    {
        public PdfStreamObject Stream;
        public double X0, Y0, X1, Y1;
        public double Width { get { return Math.Abs(X1 - X0); } }
        public double Height { get { return Math.Abs(Y1 - Y0); } }
    }

    /// <summary>罫線（水平・垂直の線分）。</summary>
    public sealed class RuleLine
    {
        public double X0, Y0, X1, Y1;
        public bool IsHorizontal;
    }

    /// <summary>1 ページ分の解析結果。</summary>
    public sealed class PageContent
    {
        public List<Glyph> Glyphs = new List<Glyph>();
        public List<ImagePlacement> Images = new List<ImagePlacement>();
        public List<RuleLine> Rules = new List<RuleLine>();
        public double Width;
        public double Height;
    }

    /// <summary>コンテンツストリームを解釈して、文字・画像・罫線の位置を求める。</summary>
    public sealed class ContentRenderer
    {
        private sealed class GraphicsState
        {
            public Matrix Ctm;
            public GraphicsState Clone() { return new GraphicsState { Ctm = Ctm }; }
        }

        private readonly PdfFile _file;
        private readonly Dictionary<PdfDictionary, PdfFontInfo> _fontCache =
            new Dictionary<PdfDictionary, PdfFontInfo>();

        private PageContent _result;
        private GraphicsState _state;
        private List<GraphicsState> _stack;

        // テキスト状態
        private Matrix _textMatrix = Matrix.Identity;
        private Matrix _lineMatrix = Matrix.Identity;
        private PdfFontInfo _font;
        private double _fontSize = 12;
        private double _charSpacing;
        private double _wordSpacing;
        private double _horizontalScale = 1;
        private double _leading;
        private double _rise;
        private int _renderMode;

        // パス
        private readonly List<double[]> _pathSegments = new List<double[]>();
        private double _currentX, _currentY, _startX, _startY;
        private bool _hasCurrentPoint;

        public ContentRenderer(PdfFile file) { _file = file; }

        /// <summary>0 以上を入れると、ページの /Rotate の代わりにその角度を使う。</summary>
        public int RotationOverride = -1;

        public PageContent Render(PdfPageInfo page)
        {
            _result = new PageContent();
            int rotation = RotationOverride >= 0 ? RotationOverride : page.Rotate;
            double x0 = page.MediaBox[0], y0 = page.MediaBox[1], x1 = page.MediaBox[2], y1 = page.MediaBox[3];
            double pageWidth = x1 - x0, pageHeight = y1 - y0;

            Matrix baseMatrix;
            switch (rotation)
            {
                case 90:
                    baseMatrix = new Matrix(0, 1, 1, 0, -y0, -x0);
                    _result.Width = pageHeight; _result.Height = pageWidth;
                    break;
                case 180:
                    baseMatrix = new Matrix(-1, 0, 0, 1, x1, -y0);
                    _result.Width = pageWidth; _result.Height = pageHeight;
                    break;
                case 270:
                    baseMatrix = new Matrix(0, -1, -1, 0, y1, x1);
                    _result.Width = pageHeight; _result.Height = pageWidth;
                    break;
                default:
                    baseMatrix = new Matrix(1, 0, 0, -1, -x0, y1);
                    _result.Width = pageWidth; _result.Height = pageHeight;
                    break;
            }

            _state = new GraphicsState { Ctm = baseMatrix };
            _stack = new List<GraphicsState>();

            byte[] content = _file.GetPageContent(page);
            Execute(content, page.Resources, 0);
            return _result;
        }

        private void Execute(byte[] content, PdfDictionary resources, int depth)
        {
            if (content == null || content.Length == 0 || depth > 12) return;
            var parser = new PdfParser(content);
            var operands = new List<PdfObject>();

            while (true)
            {
                PdfObject item;
                try { item = parser.ParseObject(); }
                catch (Exception) { break; }
                if (item == null)
                {
                    if (parser.AtEnd) break;
                    continue;
                }
                var keyword = item as PdfKeyword;
                if (keyword == null)
                {
                    operands.Add(item);
                    if (operands.Count > 200) operands.RemoveRange(0, 100);
                    continue;
                }

                try { Operate(keyword.Value, operands, resources, depth, parser); }
                catch (Exception) { }
                operands.Clear();
            }
        }

        private static double Number(List<PdfObject> operands, int indexFromEnd)
        {
            int index = operands.Count - 1 - indexFromEnd;
            if (index < 0 || index >= operands.Count) return 0;
            var number = operands[index] as PdfNumber;
            return number != null ? number.Value : 0;
        }

        private void Operate(string op, List<PdfObject> operands, PdfDictionary resources, int depth, PdfParser parser)
        {
            switch (op)
            {
                case "q":
                    _stack.Add(_state.Clone());
                    break;
                case "Q":
                    if (_stack.Count > 0)
                    {
                        _state = _stack[_stack.Count - 1];
                        _stack.RemoveAt(_stack.Count - 1);
                    }
                    break;
                case "cm":
                    if (operands.Count >= 6)
                    {
                        var m = new Matrix(Number(operands, 5), Number(operands, 4), Number(operands, 3),
                            Number(operands, 2), Number(operands, 1), Number(operands, 0));
                        _state.Ctm = m.Multiply(_state.Ctm);
                    }
                    break;

                case "BT":
                    _textMatrix = Matrix.Identity;
                    _lineMatrix = Matrix.Identity;
                    break;
                case "ET":
                    break;
                case "Tf":
                    _fontSize = Number(operands, 0);
                    if (operands.Count >= 2)
                    {
                        var name = operands[operands.Count - 2] as PdfName;
                        _font = LookUpFont(resources, name != null ? name.Value : null);
                    }
                    break;
                case "Td":
                    _lineMatrix = new Matrix(1, 0, 0, 1, Number(operands, 1), Number(operands, 0)).Multiply(_lineMatrix);
                    _textMatrix = _lineMatrix;
                    break;
                case "TD":
                    _leading = -Number(operands, 0);
                    _lineMatrix = new Matrix(1, 0, 0, 1, Number(operands, 1), Number(operands, 0)).Multiply(_lineMatrix);
                    _textMatrix = _lineMatrix;
                    break;
                case "Tm":
                    if (operands.Count >= 6)
                    {
                        _lineMatrix = new Matrix(Number(operands, 5), Number(operands, 4), Number(operands, 3),
                            Number(operands, 2), Number(operands, 1), Number(operands, 0));
                        _textMatrix = _lineMatrix;
                    }
                    break;
                case "T*":
                    NextLine();
                    break;
                case "TL":
                    _leading = Number(operands, 0);
                    break;
                case "Tc":
                    _charSpacing = Number(operands, 0);
                    break;
                case "Tw":
                    _wordSpacing = Number(operands, 0);
                    break;
                case "Tz":
                    _horizontalScale = Number(operands, 0) / 100.0;
                    if (_horizontalScale == 0) _horizontalScale = 1;
                    break;
                case "Ts":
                    _rise = Number(operands, 0);
                    break;
                case "Tr":
                    _renderMode = (int)Number(operands, 0);
                    break;

                case "Tj":
                    ShowText(LastString(operands));
                    break;
                case "'":
                    NextLine();
                    ShowText(LastString(operands));
                    break;
                case "\"":
                    if (operands.Count >= 3)
                    {
                        _wordSpacing = Number(operands, 2);
                        _charSpacing = Number(operands, 1);
                    }
                    NextLine();
                    ShowText(LastString(operands));
                    break;
                case "TJ":
                    {
                        var array = operands.Count > 0 ? operands[operands.Count - 1] as PdfArray : null;
                        if (array == null) break;
                        for (int i = 0; i < array.Count; i++)
                        {
                            var text = array[i] as PdfStringObject;
                            if (text != null) { ShowText(text.Bytes); continue; }
                            var number = array[i] as PdfNumber;
                            if (number != null)
                            {
                                double shift = -number.Value / 1000.0 * _fontSize * _horizontalScale;
                                _textMatrix = new Matrix(1, 0, 0, 1, shift, 0).Multiply(_textMatrix);
                            }
                        }
                    }
                    break;

                case "Do":
                    {
                        var name = operands.Count > 0 ? operands[operands.Count - 1] as PdfName : null;
                        if (name != null) DoXObject(name.Value, resources, depth);
                    }
                    break;

                case "BI":
                    SkipInlineImage(parser);
                    break;

                case "m":
                    _currentX = Number(operands, 1); _currentY = Number(operands, 0);
                    _startX = _currentX; _startY = _currentY;
                    _hasCurrentPoint = true;
                    break;
                case "l":
                    if (_hasCurrentPoint)
                    {
                        double nx = Number(operands, 1), ny = Number(operands, 0);
                        _pathSegments.Add(new double[] { _currentX, _currentY, nx, ny });
                        _currentX = nx; _currentY = ny;
                    }
                    break;
                case "c":
                case "v":
                case "y":
                    if (operands.Count >= 2)
                    {
                        _currentX = Number(operands, 1);
                        _currentY = Number(operands, 0);
                    }
                    break;
                case "h":
                    if (_hasCurrentPoint)
                    {
                        _pathSegments.Add(new double[] { _currentX, _currentY, _startX, _startY });
                        _currentX = _startX; _currentY = _startY;
                    }
                    break;
                case "re":
                    if (operands.Count >= 4)
                    {
                        double x = Number(operands, 3), y = Number(operands, 2);
                        double w = Number(operands, 1), h = Number(operands, 0);
                        _pathSegments.Add(new double[] { x, y, x + w, y });
                        _pathSegments.Add(new double[] { x + w, y, x + w, y + h });
                        _pathSegments.Add(new double[] { x + w, y + h, x, y + h });
                        _pathSegments.Add(new double[] { x, y + h, x, y });
                        _currentX = x; _currentY = y;
                        _startX = x; _startY = y;
                        _hasCurrentPoint = true;
                    }
                    break;
                case "S":
                case "s":
                case "f":
                case "F":
                case "f*":
                case "B":
                case "B*":
                case "b":
                case "b*":
                    FlushPath();
                    break;
                case "n":
                    _pathSegments.Clear();
                    _hasCurrentPoint = false;
                    break;
            }
        }

        private static byte[] LastString(List<PdfObject> operands)
        {
            for (int i = operands.Count - 1; i >= 0; i--)
            {
                var text = operands[i] as PdfStringObject;
                if (text != null) return text.Bytes;
            }
            return null;
        }

        private void NextLine()
        {
            _lineMatrix = new Matrix(1, 0, 0, 1, 0, -_leading).Multiply(_lineMatrix);
            _textMatrix = _lineMatrix;
        }

        private PdfFontInfo LookUpFont(PdfDictionary resources, string name)
        {
            if (resources == null || string.IsNullOrEmpty(name)) return _font;
            var fonts = _file.GetDictionary(resources, "Font");
            if (fonts == null) return _font;
            var dictionary = _file.Resolve(fonts.Get(name)) as PdfDictionary;
            if (dictionary == null) return _font;
            PdfFontInfo info;
            if (!_fontCache.TryGetValue(dictionary, out info))
            {
                info = PdfFontInfo.Load(dictionary, _file);
                _fontCache[dictionary] = info;
            }
            return info;
        }

        private void ShowText(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;
            PdfFontInfo font = _font ?? PdfFontInfo.Load(null, _file);

            List<int> lengths;
            List<int> codes = font.SplitCodes(bytes, out lengths);

            for (int i = 0; i < codes.Count; i++)
            {
                int code = codes[i];
                double width = font.GetWidth(code) / 1000.0;
                string text = font.ToUnicode(code);

                Matrix combined = _textMatrix.Multiply(_state.Ctm);
                Matrix glyphMatrix = new Matrix(_fontSize * _horizontalScale, 0, 0, _fontSize, 0, _rise)
                    .Multiply(combined);
                double x0 = glyphMatrix.E;
                double baseline = glyphMatrix.F;
                double size = _fontSize * combined.ScaleY;
                if (size <= 0) size = Math.Abs(_fontSize);

                bool isSpaceCode = lengths[i] == 1 && code == 32;
                double advance = (width * _fontSize + _charSpacing + (isSpaceCode ? _wordSpacing : 0)) * _horizontalScale;
                _textMatrix = new Matrix(1, 0, 0, 1, advance, 0).Multiply(_textMatrix);

                Matrix after = _textMatrix.Multiply(_state.Ctm);
                double x1 = new Matrix(_fontSize * _horizontalScale, 0, 0, _fontSize, 0, _rise).Multiply(after).E;

                if (_renderMode == 7) continue;
                if (string.IsNullOrEmpty(text)) continue;
                if (text == "\r" || text == "\n") continue;

                var glyph = new Glyph
                {
                    Text = text,
                    X0 = Math.Min(x0, x1),
                    X1 = Math.Max(x0, x1),
                    Baseline = baseline,
                    Size = size,
                    Bold = font.IsBold,
                    Italic = font.IsItalic,
                    Monospace = font.IsFixedPitch
                };
                if (glyph.X1 - glyph.X0 < 0.01) glyph.X1 = glyph.X0 + size * 0.5;
                _result.Glyphs.Add(glyph);
            }
        }

        private void DoXObject(string name, PdfDictionary resources, int depth)
        {
            var xobjects = _file.GetDictionary(resources, "XObject");
            if (xobjects == null) return;
            var stream = _file.Resolve(xobjects.Get(name)) as PdfStreamObject;
            if (stream == null) return;

            string subtype = _file.GetName(stream.Dictionary, "Subtype");
            if (subtype == "Image")
            {
                var placement = new ImagePlacement { Stream = stream };
                double[] xs = new double[4], ys = new double[4];
                double[][] corners = new double[][]
                {
                    new double[] { 0, 0 }, new double[] { 1, 0 }, new double[] { 1, 1 }, new double[] { 0, 1 }
                };
                for (int i = 0; i < 4; i++)
                {
                    xs[i] = _state.Ctm.TransformX(corners[i][0], corners[i][1]);
                    ys[i] = _state.Ctm.TransformY(corners[i][0], corners[i][1]);
                }
                placement.X0 = Min(xs); placement.X1 = Max(xs);
                placement.Y0 = Min(ys); placement.Y1 = Max(ys);
                _result.Images.Add(placement);
                return;
            }
            if (subtype != "Form") return;

            var saved = _state.Clone();
            int savedStackDepth = _stack.Count;
            Matrix savedText = _textMatrix, savedLine = _lineMatrix;

            var matrixArray = _file.GetArray(stream.Dictionary, "Matrix");
            if (matrixArray != null && matrixArray.Count >= 6)
            {
                var values = new double[6];
                for (int i = 0; i < 6; i++)
                {
                    var number = _file.Resolve(matrixArray[i]) as PdfNumber;
                    values[i] = number != null ? number.Value : (i == 0 || i == 3 ? 1 : 0);
                }
                var formMatrix = new Matrix(values[0], values[1], values[2], values[3], values[4], values[5]);
                _state.Ctm = formMatrix.Multiply(_state.Ctm);
            }

            var formResources = _file.GetDictionary(stream.Dictionary, "Resources") ?? resources;
            byte[] content = PdfFilters.Decode(stream, _file);
            Execute(content, formResources, depth + 1);

            while (_stack.Count > savedStackDepth) _stack.RemoveAt(_stack.Count - 1);
            _state = saved;
            _textMatrix = savedText;
            _lineMatrix = savedLine;
        }

        private static double Min(double[] values)
        {
            double result = values[0];
            for (int i = 1; i < values.Length; i++) if (values[i] < result) result = values[i];
            return result;
        }

        private static double Max(double[] values)
        {
            double result = values[0];
            for (int i = 1; i < values.Length; i++) if (values[i] > result) result = values[i];
            return result;
        }

        private void SkipInlineImage(PdfParser parser)
        {
            byte[] data = parser.Data;
            int position = parser.Position;
            // ID までを読み飛ばし、その後 EI を探す
            int idIndex = PdfParser.IndexOf(data, new byte[] { (byte)'I', (byte)'D' }, position);
            if (idIndex < 0) { parser.Position = data.Length; return; }
            int search = idIndex + 3;
            while (search + 1 < data.Length)
            {
                if (data[search] == (byte)'E' && data[search + 1] == (byte)'I'
                    && (search == 0 || PdfParser.IsWhitespace(data[search - 1]))
                    && (search + 2 >= data.Length || !PdfParser.IsRegular(data[search + 2])))
                {
                    parser.Position = search + 2;
                    return;
                }
                search++;
            }
            parser.Position = data.Length;
        }

        private void FlushPath()
        {
            foreach (double[] segment in _pathSegments)
            {
                double ax = _state.Ctm.TransformX(segment[0], segment[1]);
                double ay = _state.Ctm.TransformY(segment[0], segment[1]);
                double bx = _state.Ctm.TransformX(segment[2], segment[3]);
                double by = _state.Ctm.TransformY(segment[2], segment[3]);

                double dx = Math.Abs(bx - ax);
                double dy = Math.Abs(by - ay);
                if (dx < 1.5 && dy < 1.5) continue;

                if (dy <= 2.5 && dx > 3)
                {
                    _result.Rules.Add(new RuleLine
                    {
                        X0 = Math.Min(ax, bx), X1 = Math.Max(ax, bx),
                        Y0 = (ay + by) / 2, Y1 = (ay + by) / 2,
                        IsHorizontal = true
                    });
                }
                else if (dx <= 2.5 && dy > 3)
                {
                    _result.Rules.Add(new RuleLine
                    {
                        X0 = (ax + bx) / 2, X1 = (ax + bx) / 2,
                        Y0 = Math.Min(ay, by), Y1 = Math.Max(ay, by),
                        IsHorizontal = false
                    });
                }
                if (_result.Rules.Count > 20000) break;
            }
            _pathSegments.Clear();
            _hasCurrentPoint = false;
        }
    }
}
