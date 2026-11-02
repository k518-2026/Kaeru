using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace IWorkConverter.Core.Ooxml
{
    /// <summary>OOXML（docx/xlsx/pptx）を外部ライブラリなしで書き出すための ZIP ラッパー。</summary>
    internal sealed class OoxmlPackage : IDisposable
    {
        private readonly FileStream _fs;
        private readonly ZipArchive _zip;

        public OoxmlPackage(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            _zip = new ZipArchive(_fs, ZipArchiveMode.Create, false);
        }

        public void AddXml(string entryName, string xml)
        {
            var entry = _zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var s = entry.Open())
            using (var w = new StreamWriter(s, new UTF8Encoding(false)))
            {
                w.Write(xml);
            }
        }

        public void Dispose()
        {
            _zip.Dispose();
            _fs.Dispose();
        }
    }

    internal static class Xml
    {
        public static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                if (c == '&') sb.Append("&amp;");
                else if (c == '<') sb.Append("&lt;");
                else if (c == '>') sb.Append("&gt;");
                else if (c == '"') sb.Append("&quot;");
                else if (c == '\'') sb.Append("&apos;");
                else if (c == '\t' || c == '\n') sb.Append(c);
                else if (c < 0x20) { /* 無効な制御文字は除去 */ }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
