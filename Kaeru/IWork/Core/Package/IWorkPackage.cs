using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace IWorkConverter.Core.Package
{
    public enum IWorkKind
    {
        Unknown = 0,
        Pages = 1,
        Numbers = 2,
        Keynote = 3
    }

    /// <summary>
    /// .pages / .numbers / .key を開く。ZIP 単一ファイル形式とフォルダ（バンドル）形式の両方に対応。
    /// </summary>
    public sealed class IWorkPackage : IDisposable
    {
        private ZipArchive _zip;
        private string _rootDir;
        private readonly List<string> _entries = new List<string>();

        public string SourcePath { get; private set; }
        public IWorkKind Kind { get; private set; }
        public bool IsLegacy { get; private set; }     // iWork '09（index.xml）形式
        public IReadOnlyList<string> Entries { get { return _entries; } }

        private IWorkPackage() { }

        public static IWorkPackage Open(string path)
        {
            var pkg = new IWorkPackage();
            pkg.SourcePath = path;

            if (Directory.Exists(path))
            {
                pkg._rootDir = path;
                int cut = path.Length + (path.EndsWith(Path.DirectorySeparatorChar.ToString()) ? 0 : 1);
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    pkg._entries.Add(f.Substring(cut).Replace('\\', '/'));
            }
            else
            {
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                pkg._zip = new ZipArchive(fs, ZipArchiveMode.Read, false);
                foreach (var e in pkg._zip.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    pkg._entries.Add(e.FullName.Replace('\\', '/'));
                }
            }

            pkg.Kind = DetectKind(path, pkg._entries);
            pkg.IsLegacy = pkg.FindLegacyIndex() != null;
            return pkg;
        }

        private static IWorkKind DetectKind(string path, List<string> entries)
        {
            string ext = Path.GetExtension(path);
            if (!string.IsNullOrEmpty(ext))
            {
                ext = ext.ToLowerInvariant();
                if (ext == ".pages") return IWorkKind.Pages;
                if (ext == ".numbers") return IWorkKind.Numbers;
                if (ext == ".key" || ext == ".keynote") return IWorkKind.Keynote;
            }

            bool hasSlides = entries.Any(e => e.IndexOf("Slide", StringComparison.OrdinalIgnoreCase) >= 0 && e.EndsWith(".iwa", StringComparison.OrdinalIgnoreCase));
            bool hasCalc = entries.Any(e => e.IndexOf("CalculationEngine", StringComparison.OrdinalIgnoreCase) >= 0);
            bool hasTables = entries.Any(e => e.IndexOf("/Tables/", StringComparison.OrdinalIgnoreCase) >= 0);

            if (hasSlides) return IWorkKind.Keynote;
            if (hasCalc || hasTables) return IWorkKind.Numbers;
            return IWorkKind.Unknown;
        }

        public bool Exists(string entry)
        {
            return _entries.Any(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase));
        }

        public byte[] Read(string entry)
        {
            if (_rootDir != null)
            {
                string full = Path.Combine(_rootDir, entry.Replace('/', Path.DirectorySeparatorChar));
                return File.ReadAllBytes(full);
            }
            var e = _zip.Entries.FirstOrDefault(x => string.Equals(x.FullName.Replace('\\', '/'), entry, StringComparison.OrdinalIgnoreCase));
            if (e == null) throw new FileNotFoundException("パッケージ内に見つかりません: " + entry);
            using (var s = e.Open())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        /// <summary>Index 配下の .iwa をすべて列挙（Document.iwa を先頭に）。</summary>
        public List<string> IwaEntries()
        {
            var list = _entries.Where(e => e.EndsWith(".iwa", StringComparison.OrdinalIgnoreCase)).ToList();
            list.Sort(delegate (string a, string b)
            {
                int ra = Rank(a), rb = Rank(b);
                if (ra != rb) return ra.CompareTo(rb);
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        private static int Rank(string name)
        {
            string n = Path.GetFileName(name).ToLowerInvariant();
            if (n.StartsWith("document")) return 0;
            if (n.StartsWith("metadata")) return 1;
            return 5;
        }

        /// <summary>iWork '09 の index.xml（.gz も可）を探す。</summary>
        public string FindLegacyIndex()
        {
            foreach (var candidate in new[] { "index.xml", "index.xml.gz", "Index.xml", "index.apxl", "index.apxl.gz" })
            {
                var hit = _entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e), candidate, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            return null;
        }

        public string ReadLegacyXml()
        {
            string entry = FindLegacyIndex();
            if (entry == null) return null;
            byte[] raw = Read(entry);
            if (entry.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ||
                (raw.Length > 2 && raw[0] == 0x1F && raw[1] == 0x8B))
            {
                using (var ms = new MemoryStream(raw))
                using (var gz = new GZipStream(ms, CompressionMode.Decompress))
                using (var outMs = new MemoryStream())
                {
                    gz.CopyTo(outMs);
                    raw = outMs.ToArray();
                }
            }
            return System.Text.Encoding.UTF8.GetString(raw);
        }

        public void Dispose()
        {
            if (_zip != null) { _zip.Dispose(); _zip = null; }
        }
    }
}
