using System;

namespace IWorkConverter.Core
{
    public sealed class ConversionOptions
    {
        /// <summary>本文以外（テキストボックス・図形内の文字）も取り込む。</summary>
        public bool IncludeFloatingText = true;

        /// <summary>スタイル名から見出しを推定する。</summary>
        public bool DetectHeadings = true;

        /// <summary>空の段落を保持する。</summary>
        public bool KeepEmptyParagraphs = false;

        /// <summary>本文が空のスライドを出力しない。</summary>
        public bool SkipEmptySlides = true;

        /// <summary>表の 1 行目を見出し行として太字にする。</summary>
        public bool BoldFirstRow = true;

        /// <summary>出力先フォルダ（null なら元ファイルと同じ場所）。</summary>
        public string OutputFolder;

        /// <summary>同名ファイルがある場合に上書きする。</summary>
        public bool Overwrite = false;
    }
}
