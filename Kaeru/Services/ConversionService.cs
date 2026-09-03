using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kaeru.Models;
using Kaeru.Writers;

namespace Kaeru.Services
{
    [Flags]
    public enum OutputFormats
    {
        None = 0,
        Latex = 1,
        Markdown = 2,
        Odf = 4
    }

    public class ConversionOptions
    {
        public OutputFormats Formats { get; set; } = OutputFormats.Markdown;
        public LatexTemplate LatexTemplate { get; set; } = LatexTemplate.JapaneseLuaLatex;
        /// <summary>画像を書き出す（LaTeX / Markdown 用の media フォルダ）。</summary>
        public bool ExtractImages { get; set; } = true;
        public string ImageFolderName { get; set; } = "media";
        /// <summary>Markdown 先頭に YAML フロントマターを付ける。</summary>
        public bool MarkdownFrontMatter { get; set; } = true;
        /// <summary>PDF のヘッダー・フッター・ページ番号を除去する。</summary>
        public bool StripRunningHeads { get; set; } = true;
        /// <summary>同名ファイルがあるとき上書きする。false なら連番を付ける。</summary>
        public bool Overwrite { get; set; } = false;
    }

    public class ConversionService
    {
        public static bool IsSupported(string path)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return ext == ".docx" || ext == ".pdf";
        }

        /// <summary>1 ファイルを変換し、生成したファイルのパスを返す。</summary>
        public List<string> Convert(string inputPath, string outputDir, ConversionOptions options, Action<string> log)
        {
            if (log == null) log = _ => { };
            var produced = new List<string>();

            if (!File.Exists(inputPath))
                throw new FileNotFoundException("入力ファイルが見つかりません。", inputPath);

            Directory.CreateDirectory(outputDir);

            string ext = (Path.GetExtension(inputPath) ?? "").ToLowerInvariant();
            DocumentModel doc;

            log("読み込み中: " + Path.GetFileName(inputPath));
            if (ext == ".docx")
            {
                doc = new DocxReader().Read(inputPath);
            }
            else if (ext == ".pdf")
            {
                doc = new PdfReader { StripRunningHeads = options.StripRunningHeads }.Read(inputPath);
            }
            else
            {
                throw new NotSupportedException("対応していない形式です: " + ext);
            }

            log(string.Format("  ブロック数 {0} / 画像 {1} 件", doc.Blocks.Count, doc.Images.Count));

            string baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(inputPath));

            if (options.Formats.HasFlag(OutputFormats.Markdown))
            {
                var writer = new MarkdownWriter
                {
                    ImageFolder = options.ImageFolderName,
                    FrontMatter = options.MarkdownFrontMatter
                };
                string path = ResolvePath(outputDir, baseName, ".md", options.Overwrite);
                File.WriteAllText(path, writer.Write(doc), new UTF8Encoding(false));
                produced.Add(path);
                log("  出力: " + Path.GetFileName(path));
            }

            if (options.Formats.HasFlag(OutputFormats.Latex))
            {
                var writer = new LatexWriter
                {
                    Template = options.LatexTemplate,
                    ImageFolder = options.ImageFolderName
                };
                string path = ResolvePath(outputDir, baseName, ".tex", options.Overwrite);
                File.WriteAllText(path, writer.Write(doc), new UTF8Encoding(false));
                produced.Add(path);
                log("  出力: " + Path.GetFileName(path));
            }

            if (options.Formats.HasFlag(OutputFormats.Odf))
            {
                string path = ResolvePath(outputDir, baseName, ".odt", options.Overwrite);
                new OdtWriter().Write(doc, path);
                produced.Add(path);
                log("  出力: " + Path.GetFileName(path));
            }

            bool needsMediaFolder =
                options.Formats.HasFlag(OutputFormats.Markdown) || options.Formats.HasFlag(OutputFormats.Latex);

            if (options.ExtractImages && needsMediaFolder && doc.Images.Count > 0)
            {
                string mediaDir = Path.Combine(outputDir, options.ImageFolderName);
                Directory.CreateDirectory(mediaDir);
                foreach (var img in doc.Images)
                {
                    string p = Path.Combine(mediaDir, img.FileName);
                    File.WriteAllBytes(p, img.Data);
                }
                log(string.Format("  画像 {0} 件を {1}/ に保存", doc.Images.Count, options.ImageFolderName));
            }

            return produced;
        }

        private static string ResolvePath(string dir, string baseName, string extension, bool overwrite)
        {
            string path = Path.Combine(dir, baseName + extension);
            if (overwrite || !File.Exists(path)) return path;

            for (int i = 2; i < 1000; i++)
            {
                string candidate = Path.Combine(dir, baseName + "_" + i + extension);
                if (!File.Exists(candidate)) return candidate;
            }
            return path;
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in name ?? "output")
                sb.Append(invalid.Contains(c) ? '_' : c);
            string result = sb.ToString().Trim();
            return string.IsNullOrEmpty(result) ? "output" : result;
        }
    }
}
