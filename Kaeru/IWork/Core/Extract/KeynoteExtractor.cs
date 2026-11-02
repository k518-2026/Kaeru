using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using IWorkConverter.Core.Iwa;
using IWorkConverter.Core.Model;

namespace IWorkConverter.Core.Extract
{
    public static class KeynoteExtractor
    {
        public static DeckModel Extract(IwaArchive archive, ConversionOptions opt, Action<string> note)
        {
            var deck = new DeckModel();
            var storages = TextStorageFinder.Find(archive);

            // スライドは Slide-*.iwa という個別コンポーネントに分かれている
            var slideComponents = archive.Components
                .Where(c => c.StartsWith("Slide", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();

            if (slideComponents.Count == 0)
            {
                // 単一コンポーネントに全部入っている場合のフォールバック
                if (note != null) note("スライド単位の構造を検出できなかったため、テキスト単位でスライドを作りました。");
                foreach (var st in storages.OrderBy(s => s.Order))
                {
                    var slide = BuildSlide(new List<TextStorage> { st });
                    if (!opt.SkipEmptySlides || !IsEmpty(slide)) deck.Slides.Add(slide);
                }
                return deck;
            }

            var ordered = OrderSlides(archive, slideComponents);

            foreach (var comp in ordered)
            {
                var inSlide = storages.Where(s => string.Equals(s.Component, comp, StringComparison.OrdinalIgnoreCase))
                                      .OrderBy(s => s.Order)
                                      .ToList();
                var slide = BuildSlide(inSlide);
                if (opt.SkipEmptySlides && IsEmpty(slide)) continue;
                deck.Slides.Add(slide);
            }

            if (deck.Slides.Count == 0 && storages.Count > 0)
            {
                if (note != null) note("スライド本文を割り当てられなかったため、検出したテキストをまとめて出力しました。");
                deck.Slides.Add(BuildSlide(storages));
            }

            return deck;
        }

        private static bool IsEmpty(SlideModel s)
        {
            return string.IsNullOrWhiteSpace(s.Title) && s.Bullets.Count == 0;
        }

        private static SlideModel BuildSlide(List<TextStorage> storages)
        {
            var slide = new SlideModel();
            if (storages == null || storages.Count == 0) return slide;

            TextStorage title = null;
            foreach (var st in storages)
            {
                string t = st.Text.Trim();
                if (t.Length == 0) continue;
                if (t.IndexOf('\n') < 0 && t.Length <= 120) { title = st; break; }
            }
            if (title == null) title = storages.FirstOrDefault(s => s.Text.Trim().Length > 0);

            if (title != null)
            {
                var lines = SplitLines(title.Text);
                if (lines.Count > 0) slide.Title = lines[0];
                for (int i = 1; i < lines.Count; i++) slide.Bullets.Add(lines[i]);
            }

            foreach (var st in storages)
            {
                if (ReferenceEquals(st, title)) continue;
                foreach (var line in SplitLines(st.Text)) slide.Bullets.Add(line);
            }

            return slide;
        }

        private static List<string> SplitLines(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;
            foreach (var raw in text.Split('\n'))
            {
                string s = raw.Trim();
                if (s.Length == 0) continue;
                s = s.TrimStart('•', '◦', '▪', '‣', '・', '-', '–', '*', '\t', ' ');
                if (s.Length > 0) result.Add(s);
            }
            return result;
        }

        /// <summary>
        /// スライドの並び順を推定する。ドキュメント側にスライドの参照リストがあればそれを使い、
        /// 見つからなければコンポーネント名の数値でソートする。
        /// </summary>
        private static List<string> OrderSlides(IwaArchive archive, List<string> slideComponents)
        {
            var master = new HashSet<string>(slideComponents.Where(c => c.StartsWith("MasterSlide", StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
            var slides = slideComponents.Where(c => !master.Contains(c)).ToList();

            var componentOf = new Dictionary<ulong, string>();
            foreach (var o in archive.Objects)
                if (!componentOf.ContainsKey(o.Id)) componentOf[o.Id] = o.Component;

            List<string> best = null;
            foreach (var obj in archive.Objects)
            {
                foreach (var kv in obj.Message.Fields)
                {
                    if (kv.Value.Count < 2) continue;
                    var seq = new List<string>();
                    foreach (var f in kv.Value)
                    {
                        if (f.WireType != 2) { seq.Clear(); break; }
                        var m = f.AsMessage();
                        if (m == null) { seq.Clear(); break; }
                        ulong id = m.ULong(1) ?? 0UL;
                        string comp;
                        if (id == 0UL || !componentOf.TryGetValue(id, out comp)) { seq.Clear(); break; }
                        if (master.Contains(comp)) { seq.Clear(); break; }
                        if (!seq.Contains(comp)) seq.Add(comp);
                    }
                    if (seq.Count >= 2 && seq.All(s => slides.Contains(s)))
                    {
                        if (best == null || seq.Count > best.Count) best = seq;
                    }
                }
            }

            if (best != null && best.Count >= slides.Count) return best;
            if (best != null && best.Count > 0)
            {
                var rest = slides.Where(s => !best.Contains(s)).OrderBy(NumericKey).ToList();
                best.AddRange(rest);
                return best;
            }

            return slides.OrderBy(NumericKey).ToList();
        }

        private static readonly Regex NumRegex = new Regex(@"(\d+)", RegexOptions.Compiled);

        private static long NumericKey(string component)
        {
            var m = NumRegex.Match(component);
            long v;
            if (m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return long.MaxValue;
        }
    }
}
