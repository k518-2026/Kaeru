using System;
using System.IO;
using IWorkConverter.Core.Extract;
using IWorkConverter.Core.Iwa;
using IWorkConverter.Core.Legacy;
using IWorkConverter.Core.Model;
using IWorkConverter.Core.Ooxml;
using IWorkConverter.Core.Package;

namespace IWorkConverter.Core
{
    public static class ConversionService
    {
        public static string TargetExtension(IWorkKind kind)
        {
            switch (kind)
            {
                case IWorkKind.Pages: return ".docx";
                case IWorkKind.Numbers: return ".xlsx";
                case IWorkKind.Keynote: return ".pptx";
                default: return null;
            }
        }

        public static IWorkKind PeekKind(string path)
        {
            string ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            if (ext == ".pages") return IWorkKind.Pages;
            if (ext == ".numbers") return IWorkKind.Numbers;
            if (ext == ".key" || ext == ".keynote") return IWorkKind.Keynote;
            return IWorkKind.Unknown;
        }

        public static ConversionResult Convert(string inputPath, ConversionOptions options)
        {
            var result = new ConversionResult { InputPath = inputPath };
            options = options ?? new ConversionOptions();

            try
            {
                using (var pkg = IWorkPackage.Open(inputPath))
                {
                    if (pkg.Kind == IWorkKind.Unknown)
                    {
                        result.Message = "Pages / Numbers / Keynote のファイルとして認識できませんでした。";
                        return result;
                    }

                    string ext = TargetExtension(pkg.Kind);
                    string outPath = BuildOutputPath(inputPath, ext, options);
                    result.OutputPath = outPath;

                    if (File.Exists(outPath) && !options.Overwrite)
                        outPath = MakeUnique(outPath);
                    result.OutputPath = outPath;

                    Action<string> note = result.Note;

                    if (pkg.IsLegacy)
                    {
                        result.Note("iWork '09 形式（index.xml）として読み込みました。");
                        string xml = pkg.ReadLegacyXml();
                        if (pkg.Kind == IWorkKind.Pages)
                            DocxWriter.Write(outPath, LegacyXmlExtractor.ExtractPages(xml, note), options);
                        else if (pkg.Kind == IWorkKind.Numbers)
                            XlsxWriter.Write(outPath, LegacyXmlExtractor.ExtractNumbers(xml, note), options);
                        else
                            PptxWriter.Write(outPath, LegacyXmlExtractor.ExtractKeynote(xml, options, note), options);
                    }
                    else
                    {
                        var archive = IwaArchive.Load(pkg, note);
                        if (archive.Objects.Count == 0)
                        {
                            result.Message = "ファイルの内部データを読み取れませんでした（暗号化されている可能性があります）。";
                            return result;
                        }

                        if (pkg.Kind == IWorkKind.Pages)
                        {
                            var doc = PagesExtractor.Extract(archive, options, note);
                            result.Note("段落数: " + doc.Paragraphs.Count);
                            DocxWriter.Write(outPath, doc, options);
                        }
                        else if (pkg.Kind == IWorkKind.Numbers)
                        {
                            var book = NumbersExtractor.Extract(archive, options, note);
                            result.Note("表の数: " + book.Tables.Count);
                            XlsxWriter.Write(outPath, book, options);
                        }
                        else
                        {
                            var deck = KeynoteExtractor.Extract(archive, options, note);
                            result.Note("スライド数: " + deck.Slides.Count);
                            PptxWriter.Write(outPath, deck, options);
                        }
                    }

                    result.Success = true;
                    result.Message = "変換しました。";
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = ex.Message;
                return result;
            }
        }

        private static string BuildOutputPath(string inputPath, string ext, ConversionOptions options)
        {
            string folder = string.IsNullOrEmpty(options.OutputFolder)
                ? (Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory())
                : options.OutputFolder;
            string name = Path.GetFileNameWithoutExtension(inputPath);
            if (string.IsNullOrEmpty(name)) name = "converted";
            return Path.Combine(folder, name + ext);
        }

        private static string MakeUnique(string path)
        {
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; i < 1000; i++)
            {
                string candidate = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(candidate)) return candidate;
            }
            return path;
        }
    }
}
