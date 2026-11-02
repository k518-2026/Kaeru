using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PdfToWord.Pdf
{
    /// <summary>1 ページ分の情報（辞書と継承済み属性）。</summary>
    public sealed class PdfPageInfo
    {
        public PdfDictionary Dictionary;
        public PdfDictionary Resources;
        public double[] MediaBox = new double[] { 0, 0, 595.276, 841.89 };
        public int Rotate;

        public double Width { get { return Math.Abs(MediaBox[2] - MediaBox[0]); } }
        public double Height { get { return Math.Abs(MediaBox[3] - MediaBox[1]); } }
    }

    /// <summary>
    /// PDF ファイル全体を読み込む。相互参照表が壊れていても動くように、
    /// ファイル全体を走査して "n g obj" を集める方式をとっている。
    /// </summary>
    public sealed class PdfFile : IDisposable
    {
        private byte[] _data;
        private readonly Dictionary<int, PdfObject> _objects = new Dictionary<int, PdfObject>();
        private readonly Dictionary<int, int> _generations = new Dictionary<int, int>();
        private PdfDecryptor _decryptor;

        public PdfDictionary Trailer = new PdfDictionary();
        public List<PdfPageInfo> Pages = new List<PdfPageInfo>();
        public string Warning { get; private set; }

        /// <summary>パスワードが要る文書のために、利用者パスワードを保持する。</summary>
        public string Password = string.Empty;

        /// <summary>パスワードが合わずに読めなかったときに true。</summary>
        public bool PasswordRequired { get; private set; }

        public static PdfFile Open(string path)
        {
            return Open(path, null);
        }

        public static PdfFile Open(string path, string password)
        {
            var file = new PdfFile();
            file.Password = password ?? string.Empty;
            file.Load(File.ReadAllBytes(path));
            return file;
        }

        public static PdfFile OpenBytes(byte[] bytes)
        {
            return OpenBytes(bytes, null);
        }

        public static PdfFile OpenBytes(byte[] bytes, string password)
        {
            var file = new PdfFile();
            file.Password = password ?? string.Empty;
            file.Load(bytes);
            return file;
        }

        private void Load(byte[] data)
        {
            _data = data ?? new byte[0];
            if (PdfParser.IndexOf(_data, Encoding.ASCII.GetBytes("%PDF-"), 0) < 0)
            {
                int header = PdfParser.IndexOf(_data, Encoding.ASCII.GetBytes("%PDF"), 0);
                if (header < 0) throw new InvalidDataException("PDF ファイルではないようです。");
            }

            ScanObjects();
            CollectTrailers();
            SetUpDecryption();
            ExpandObjectStreams();
            BuildPageList();
        }

        /// <summary>ファイル全体から "番号 世代 obj" を探して読み込む。</summary>
        private void ScanObjects()
        {
            byte[] data = _data;
            for (int i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] != (byte)'o' || data[i + 1] != (byte)'b' || data[i + 2] != (byte)'j') continue;
                if (i + 3 < data.Length && PdfParser.IsRegular(data[i + 3])) continue;
                if (i > 0 && PdfParser.IsRegular(data[i - 1])) continue;

                int p = i - 1;
                while (p >= 0 && PdfParser.IsWhitespace(data[p])) p--;
                int genEnd = p + 1;
                while (p >= 0 && data[p] >= (byte)'0' && data[p] <= (byte)'9') p--;
                int genStart = p + 1;
                if (genStart >= genEnd) continue;
                while (p >= 0 && PdfParser.IsWhitespace(data[p])) p--;
                int numEnd = p + 1;
                while (p >= 0 && data[p] >= (byte)'0' && data[p] <= (byte)'9') p--;
                int numStart = p + 1;
                if (numStart >= numEnd) continue;
                if (numEnd == genEnd) continue;
                if (p >= 0 && PdfParser.IsRegular(data[p])) continue;

                int number, generation;
                if (!int.TryParse(Encoding.ASCII.GetString(data, numStart, numEnd - numStart), out number)) continue;
                if (!int.TryParse(Encoding.ASCII.GetString(data, genStart, genEnd - genStart), out generation)) continue;

                var parser = new PdfParser(data, i + 3);
                parser.Resolver = ResolveRawLength;
                PdfObject value;
                try
                {
                    value = parser.ParseObject();
                }
                catch (Exception)
                {
                    continue;
                }
                if (value == null || value is PdfKeyword) continue;

                int previousGeneration;
                if (_objects.ContainsKey(number) && _generations.TryGetValue(number, out previousGeneration)
                    && previousGeneration > generation)
                    continue;

                _objects[number] = value;
                _generations[number] = generation;
                i = Math.Max(i, parser.Position - 1);
            }
        }

        /// <summary>ストリーム長が間接参照のときに使う簡易リゾルバー。</summary>
        private PdfObject ResolveRawLength(PdfObject value)
        {
            var reference = value as PdfReference;
            if (reference == null) return value;
            PdfObject target;
            if (_objects.TryGetValue(reference.Number, out target)) return target;
            return null;
        }

        private void CollectTrailers()
        {
            byte[] keyword = Encoding.ASCII.GetBytes("trailer");
            int index = 0;
            while (true)
            {
                index = PdfParser.IndexOf(_data, keyword, index);
                if (index < 0) break;
                var parser = new PdfParser(_data, index + keyword.Length);
                parser.Resolver = ResolveRawLength;
                var dictionary = parser.ParseObject() as PdfDictionary;
                if (dictionary != null)
                {
                    foreach (var pair in dictionary.Items) Trailer.Set(pair.Key, pair.Value);
                }
                index += keyword.Length;
            }

            if (!Trailer.Has("Root") || !Trailer.Has("Encrypt"))
            {
                // 相互参照ストリーム内のトレーラー情報を拾う
                foreach (var pair in _objects)
                {
                    var stream = pair.Value as PdfStreamObject;
                    if (stream == null) continue;
                    var type = stream.Dictionary.Get("Type") as PdfName;
                    if (type == null || type.Value != "XRef") continue;
                    foreach (var entry in stream.Dictionary.Items)
                    {
                        if (entry.Key == "Root" || entry.Key == "Info" || entry.Key == "Encrypt" || entry.Key == "ID")
                        {
                            if (!Trailer.Has(entry.Key)) Trailer.Set(entry.Key, entry.Value);
                        }
                    }
                }
            }
        }

        private void SetUpDecryption()
        {
            PdfObject encryptObject = Trailer.Get("Encrypt");
            int encryptNumber = -1;
            var encryptReference = encryptObject as PdfReference;
            if (encryptReference != null) encryptNumber = encryptReference.Number;
            var encrypt = Resolve(encryptObject) as PdfDictionary;
            if (encrypt == null) return;

            byte[] documentId = new byte[0];
            var ids = Resolve(Trailer.Get("ID")) as PdfArray;
            if (ids != null && ids.Count > 0)
            {
                var first = Resolve(ids[0]) as PdfStringObject;
                if (first != null) documentId = first.Bytes;
            }

            _decryptor = PdfDecryptor.Create(encrypt, documentId, this, Password);
            if (!_decryptor.IsSupported)
            {
                string reason = _decryptor.UnsupportedReason ?? string.Empty;
                if (reason.Contains("パスワード")) PasswordRequired = true;
                Warning = "暗号化された PDF です（" + (_decryptor.UnsupportedReason ?? "非対応の方式") + "）。";
                _decryptor = null;
                return;
            }

            var numbers = new List<int>(_objects.Keys);
            foreach (int number in numbers)
            {
                if (number == encryptNumber) continue;
                int generation;
                if (!_generations.TryGetValue(number, out generation)) generation = 0;
                DecryptObject(_objects[number], number, generation, new HashSet<PdfObject>());
            }
        }

        private void DecryptObject(PdfObject value, int number, int generation, HashSet<PdfObject> visited)
        {
            if (value == null || _decryptor == null) return;
            if (!visited.Add(value)) return;

            var text = value as PdfStringObject;
            if (text != null)
            {
                text.Bytes = _decryptor.DecryptString(text.Bytes, number, generation);
                return;
            }
            var array = value as PdfArray;
            if (array != null)
            {
                for (int i = 0; i < array.Count; i++) DecryptObject(array[i], number, generation, visited);
                return;
            }
            var dictionary = value as PdfDictionary;
            if (dictionary != null)
            {
                foreach (var pair in dictionary.Items) DecryptObject(pair.Value, number, generation, visited);
                return;
            }
            var stream = value as PdfStreamObject;
            if (stream != null)
            {
                var type = stream.Dictionary.Get("Type") as PdfName;
                foreach (var pair in stream.Dictionary.Items) DecryptObject(pair.Value, number, generation, visited);
                if (type == null || type.Value != "XRef")
                    stream.Raw = _decryptor.DecryptStream(stream.Raw, number, generation);
            }
        }

        /// <summary>オブジェクトストリーム（/Type /ObjStm）の中身を展開する。</summary>
        private void ExpandObjectStreams()
        {
            var streams = new List<PdfStreamObject>();
            foreach (var pair in _objects)
            {
                var stream = pair.Value as PdfStreamObject;
                if (stream == null) continue;
                var type = stream.Dictionary.Get("Type") as PdfName;
                if (type != null && type.Value == "ObjStm") streams.Add(stream);
            }

            foreach (var stream in streams)
            {
                byte[] content;
                try { content = PdfFilters.Decode(stream, this); }
                catch (Exception) { continue; }
                if (content.Length == 0) continue;

                int count = GetInt(stream.Dictionary, "N", 0);
                int first = GetInt(stream.Dictionary, "First", 0);
                if (count <= 0 || first <= 0 || first > content.Length) continue;

                var header = new PdfParser(content, 0);
                var pairs = new List<int[]>();
                for (int i = 0; i < count; i++)
                {
                    var numberObject = header.ParseObject() as PdfNumber;
                    var offsetObject = header.ParseObject() as PdfNumber;
                    if (numberObject == null || offsetObject == null) break;
                    pairs.Add(new int[] { numberObject.AsInt, offsetObject.AsInt });
                }

                foreach (int[] entry in pairs)
                {
                    int number = entry[0];
                    int offset = first + entry[1];
                    if (offset < 0 || offset >= content.Length) continue;
                    if (_objects.ContainsKey(number)) continue;
                    var parser = new PdfParser(content, offset);
                    parser.Resolver = ResolveRawLength;
                    PdfObject value;
                    try { value = parser.ParseObject(); }
                    catch (Exception) { continue; }
                    if (value == null || value is PdfKeyword) continue;
                    _objects[number] = value;
                    _generations[number] = 0;
                }
            }
        }

        private void BuildPageList()
        {
            var catalog = Resolve(Trailer.Get("Root")) as PdfDictionary;
            if (catalog == null)
            {
                foreach (var pair in _objects)
                {
                    var dictionary = pair.Value as PdfDictionary;
                    if (dictionary == null) continue;
                    var type = dictionary.Get("Type") as PdfName;
                    if (type != null && type.Value == "Catalog") { catalog = dictionary; break; }
                }
            }

            PdfDictionary pagesRoot = catalog != null ? Resolve(catalog.Get("Pages")) as PdfDictionary : null;
            if (pagesRoot != null)
            {
                var inherited = new PdfPageInfo();
                inherited.Resources = null;
                WalkPageTree(pagesRoot, null, null, int.MinValue, new HashSet<PdfDictionary>(), 0);
            }

            if (Pages.Count == 0)
            {
                var numbers = new List<int>(_objects.Keys);
                numbers.Sort();
                foreach (int number in numbers)
                {
                    var dictionary = _objects[number] as PdfDictionary;
                    if (dictionary == null) continue;
                    var type = dictionary.Get("Type") as PdfName;
                    if (type == null || type.Value != "Page") continue;
                    Pages.Add(MakePage(dictionary, null, null, int.MinValue));
                }
            }
        }

        private void WalkPageTree(PdfDictionary node, PdfDictionary resources, double[] mediaBox, int rotate,
            HashSet<PdfDictionary> visited, int depth)
        {
            if (node == null || depth > 64 || !visited.Add(node)) return;

            var localResources = Resolve(node.Get("Resources")) as PdfDictionary;
            if (localResources != null) resources = localResources;
            double[] localBox = GetRectangle(node, "MediaBox");
            if (localBox != null) mediaBox = localBox;
            var rotateNumber = Resolve(node.Get("Rotate")) as PdfNumber;
            if (rotateNumber != null) rotate = rotateNumber.AsInt;

            var type = node.Get("Type") as PdfName;
            PdfObject kidsObject = Resolve(node.Get("Kids"));
            var kids = kidsObject as PdfArray;

            if (kids != null && (type == null || type.Value != "Page"))
            {
                for (int i = 0; i < kids.Count; i++)
                {
                    var child = Resolve(kids[i]) as PdfDictionary;
                    if (child != null) WalkPageTree(child, resources, mediaBox, rotate, visited, depth + 1);
                }
                return;
            }
            if (type != null && type.Value == "Pages") return;
            Pages.Add(MakePage(node, resources, mediaBox, rotate));
        }

        private PdfPageInfo MakePage(PdfDictionary dictionary, PdfDictionary resources, double[] mediaBox, int rotate)
        {
            var page = new PdfPageInfo();
            page.Dictionary = dictionary;
            var localResources = Resolve(dictionary.Get("Resources")) as PdfDictionary;
            page.Resources = localResources ?? resources;
            double[] box = GetRectangle(dictionary, "MediaBox") ?? mediaBox;
            if (box != null && box.Length == 4) page.MediaBox = box;
            var rotateNumber = Resolve(dictionary.Get("Rotate")) as PdfNumber;
            int actualRotate = rotateNumber != null ? rotateNumber.AsInt : (rotate == int.MinValue ? 0 : rotate);
            actualRotate = ((actualRotate % 360) + 360) % 360;
            page.Rotate = (actualRotate / 90) * 90;
            return page;
        }

        public double[] GetRectangle(PdfDictionary dictionary, string key)
        {
            if (dictionary == null) return null;
            var array = Resolve(dictionary.Get(key)) as PdfArray;
            if (array == null || array.Count < 4) return null;
            var values = new double[4];
            for (int i = 0; i < 4; i++)
            {
                var number = Resolve(array[i]) as PdfNumber;
                if (number == null) return null;
                values[i] = number.Value;
            }
            double x0 = Math.Min(values[0], values[2]);
            double y0 = Math.Min(values[1], values[3]);
            double x1 = Math.Max(values[0], values[2]);
            double y1 = Math.Max(values[1], values[3]);
            return new double[] { x0, y0, x1, y1 };
        }

        /// <summary>間接参照をたどって実体を得る。</summary>
        public PdfObject Resolve(PdfObject value)
        {
            int guard = 0;
            while (value is PdfReference && guard++ < 64)
            {
                var reference = (PdfReference)value;
                PdfObject target;
                if (!_objects.TryGetValue(reference.Number, out target)) return null;
                value = target;
            }
            if (value is PdfNull) return null;
            return value;
        }

        public PdfDictionary GetDictionary(PdfDictionary owner, string key)
        {
            if (owner == null) return null;
            PdfObject value = Resolve(owner.Get(key));
            var stream = value as PdfStreamObject;
            if (stream != null) return stream.Dictionary;
            return value as PdfDictionary;
        }

        public PdfArray GetArray(PdfDictionary owner, string key)
        {
            if (owner == null) return null;
            return Resolve(owner.Get(key)) as PdfArray;
        }

        public string GetName(PdfDictionary owner, string key)
        {
            if (owner == null) return null;
            var name = Resolve(owner.Get(key)) as PdfName;
            return name != null ? name.Value : null;
        }

        public int GetInt(PdfDictionary owner, string key, int fallback)
        {
            if (owner == null) return fallback;
            var number = Resolve(owner.Get(key)) as PdfNumber;
            return number != null ? number.AsInt : fallback;
        }

        public double GetDouble(PdfDictionary owner, string key, double fallback)
        {
            if (owner == null) return fallback;
            var number = Resolve(owner.Get(key)) as PdfNumber;
            return number != null ? number.Value : fallback;
        }

        /// <summary>ページのコンテンツストリームを 1 本につないで返す。</summary>
        public byte[] GetPageContent(PdfPageInfo page)
        {
            if (page == null || page.Dictionary == null) return new byte[0];
            PdfObject contents = Resolve(page.Dictionary.Get("Contents"));
            var output = new MemoryStream();

            var single = contents as PdfStreamObject;
            if (single != null)
            {
                byte[] decoded = PdfFilters.Decode(single, this);
                output.Write(decoded, 0, decoded.Length);
            }
            else
            {
                var array = contents as PdfArray;
                if (array != null)
                {
                    for (int i = 0; i < array.Count; i++)
                    {
                        var stream = Resolve(array[i]) as PdfStreamObject;
                        if (stream == null) continue;
                        byte[] decoded = PdfFilters.Decode(stream, this);
                        output.Write(decoded, 0, decoded.Length);
                        output.WriteByte((byte)'\n');
                    }
                }
            }
            return output.ToArray();
        }

        /// <summary>文書情報（/Info）からタイトルなどを取り出す。</summary>
        public string GetDocumentInfo(string key)
        {
            var info = Resolve(Trailer.Get("Info")) as PdfDictionary;
            if (info == null) return null;
            var text = Resolve(info.Get(key)) as PdfStringObject;
            if (text == null) return null;
            string value = text.AsText();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public void Dispose()
        {
            _objects.Clear();
            _generations.Clear();
            _data = null;
        }
    }
}
