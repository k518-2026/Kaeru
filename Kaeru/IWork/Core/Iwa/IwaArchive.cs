using System;
using System.Collections.Generic;
using IWorkConverter.Core.Package;

namespace IWorkConverter.Core.Iwa
{
    /// <summary>.iwa 内の 1 オブジェクト（アーカイブ）。</summary>
    public sealed class IwaObject
    {
        public ulong Id;
        public uint Type;          // Apple 内部のメッセージ型 ID（意味は非公開）
        public ProtoMessage Message;
        public string Component;   // 由来する .iwa の名前
        public int Order;          // 読み込み順
    }

    /// <summary>パッケージ内のすべての .iwa を読み込んだ結果。</summary>
    public sealed class IwaArchive
    {
        public readonly List<IwaObject> Objects = new List<IwaObject>();
        public readonly Dictionary<ulong, IwaObject> ById = new Dictionary<ulong, IwaObject>();
        public readonly List<string> Components = new List<string>();

        public static IwaArchive Load(IWorkPackage pkg, Action<string> log)
        {
            var archive = new IwaArchive();
            int order = 0;

            foreach (var entry in pkg.IwaEntries())
            {
                byte[] raw;
                try { raw = pkg.Read(entry); }
                catch (Exception ex) { if (log != null) log("読み込み失敗: " + entry + " (" + ex.Message + ")"); continue; }

                byte[] plain;
                try { plain = AppleSnappy.Decompress(raw); }
                catch (Exception ex) { if (log != null) log("展開失敗: " + entry + " (" + ex.Message + ")"); continue; }
                if (plain == null || plain.Length == 0) continue;

                string component = System.IO.Path.GetFileName(entry);
                archive.Components.Add(component);

                int pos = 0;
                int guard = 0;
                while (pos < plain.Length && guard++ < 2000000)
                {
                    ulong infoLen;
                    int p = pos;
                    if (!Varints.TryRead(plain, ref p, plain.Length, out infoLen)) break;
                    if (infoLen == 0 || (ulong)(plain.Length - p) < infoLen) break;

                    var info = ProtoMessage.Parse(plain, p, (int)infoLen);
                    p += (int)infoLen;

                    ulong id = info.ULong(1) ?? 0UL;
                    bool any = false;

                    foreach (var mi in info.Messages(2))
                    {
                        ulong type = mi.ULong(1) ?? 0UL;
                        ulong len = mi.ULong(3) ?? 0UL;
                        if ((ulong)(plain.Length - p) < len) { p = plain.Length; break; }

                        var body = ProtoMessage.Parse(plain, p, (int)len);
                        p += (int)len;

                        var obj = new IwaObject
                        {
                            Id = id,
                            Type = (uint)type,
                            Message = body,
                            Component = component,
                            Order = order++
                        };
                        archive.Objects.Add(obj);
                        if (!archive.ById.ContainsKey(id)) archive.ById[id] = obj;
                        any = true;
                    }

                    if (!any && p <= pos) break;
                    pos = p;
                }
            }

            return archive;
        }
    }
}
