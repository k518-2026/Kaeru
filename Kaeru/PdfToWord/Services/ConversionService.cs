using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using PdfToWord.Docx;
using PdfToWord.Models;
using PdfToWord.Pdf;

namespace PdfToWord.Services
{
    public sealed class ConversionOptions
    {
        public PdfReadOptions Read = new PdfReadOptions();
        public DocxWriteOptions Write = new DocxWriteOptions();
        public bool Overwrite;
        public bool SameFolderAsInput = true;
        public string OutputFolder;
    }

    public sealed class ConversionResult
    {
        public string InputPath;
        public string OutputPath;
        public bool Success;
        public string Message;
        public DocumentModel Model;
    }

    /// <summary>PDF を読み込み、Word 文書として書き出す。</summary>
    public sealed class ConversionService
    {
        public static bool IsSupported(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string extension = Path.GetExtension(path);
            return extension != null && extension.ToLowerInvariant() == ".pdf";
        }

        public ConversionResult Convert(string inputPath, ConversionOptions options,
            Action<string> log, Action<int, int> progress, CancellationToken token)
        {
            if (options == null) options = new ConversionOptions();
            if (log == null) log = delegate { };

            var result = new ConversionResult { InputPath = inputPath };

            if (!File.Exists(inputPath))
            {
                result.Message = "ファイルが見つかりません。";
                return result;
            }

            string outputDirectory = options.SameFolderAsInput
                ? Path.GetDirectoryName(Path.GetFullPath(inputPath))
                : options.OutputFolder;
            if (string.IsNullOrWhiteSpace(outputDirectory))
                outputDirectory = Path.GetDirectoryName(Path.GetFullPath(inputPath));
            Directory.CreateDirectory(outputDirectory);

            log("読み込み中: " + Path.GetFileName(inputPath));
            var reader = new PdfDocumentReader();
            DocumentModel model = reader.Read(inputPath, options.Read, progress, token);
            result.Model = model;

            foreach (string note in model.Notes) log("  ! " + note);
            log(string.Format("  {0} ページ / 段落 {1} / 見出し {2} / 表 {3} / 画像 {4}",
                model.PageCount,
                model.CountOf(BlockKind.Paragraph),
                model.CountOf(BlockKind.Heading),
                model.CountOf(BlockKind.Table),
                model.CountOf(BlockKind.Image)));

            token.ThrowIfCancellationRequested();

            string baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(inputPath));
            string outputPath = ResolvePath(outputDirectory, baseName, ".docx", options.Overwrite);

            var writer = new DocxWriter(options.Write);
            writer.Write(model, outputPath);

            result.OutputPath = outputPath;
            result.Success = true;
            result.Message = "変換しました";
            log("  → " + outputPath);
            return result;
        }

        /// <summary>変換結果をテキストで確認するためのプレビューを作る。</summary>
        public static string BuildPreview(DocumentModel model, int maxCharacters)
        {
            var sb = new StringBuilder();
            if (model == null) return "";
            if (!string.IsNullOrWhiteSpace(model.Title)) sb.AppendLine("《題名》 " + model.Title).AppendLine();

            foreach (DocBlock block in model.Blocks)
            {
                switch (block.Kind)
                {
                    case BlockKind.Heading:
                        sb.AppendLine();
                        sb.AppendLine(new string('#', Math.Max(1, Math.Min(6, block.HeadingLevel))) + " " + block.PlainText);
                        break;
                    case BlockKind.ListItem:
                        sb.AppendLine(new string('\u3000', block.ListLevel) + (block.Ordered ? "1. " : "・") + block.PlainText);
                        break;
                    case BlockKind.Table:
                        sb.AppendLine();
                        if (block.Table != null)
                        {
                            foreach (TableRow row in block.Table.Rows)
                            {
                                var cells = new List<string>();
                                foreach (TableCell cell in row.Cells) cells.Add(cell.PlainText.Trim());
                                sb.AppendLine("| " + string.Join(" | ", cells.ToArray()) + " |");
                            }
                        }
                        sb.AppendLine();
                        break;
                    case BlockKind.Image:
                        sb.AppendLine("［画像 " + (block.Image != null ? block.Image.PixelWidth + "×" + block.Image.PixelHeight : "") + "］");
                        break;
                    case BlockKind.PageBreak:
                        sb.AppendLine();
                        sb.AppendLine("──────── 改ページ ────────");
                        sb.AppendLine();
                        break;
                    default:
                        sb.AppendLine(block.PlainText);
                        break;
                }
                if (maxCharacters > 0 && sb.Length > maxCharacters)
                {
                    sb.AppendLine();
                    sb.AppendLine("…（以下省略）");
                    break;
                }
            }
            return sb.ToString();
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "output";
            var sb = new StringBuilder();
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in name)
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            string result = sb.ToString().Trim();
            return result.Length == 0 ? "output" : result;
        }

        public static string ResolvePath(string directory, string baseName, string extension, bool overwrite)
        {
            string path = Path.Combine(directory, baseName + extension);
            if (overwrite || !File.Exists(path)) return path;
            for (int i = 2; i < 1000; i++)
            {
                string candidate = Path.Combine(directory, baseName + " (" + i + ")" + extension);
                if (!File.Exists(candidate)) return candidate;
            }
            return path;
        }
    }
}
