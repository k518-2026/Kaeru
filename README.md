# Kaeru － Word / PDF を LaTeX・Markdown・ODF に変換

Word 文書 (.docx) と PDF (.pdf) を読み込んで、**LaTeX (.tex)**、**Markdown (.md)**、
**ODF テキスト (.odt)** に変換する Windows デスクトップアプリです。
WPF (C# / .NET 8) の Visual Studio プロジェクト一式になっています。

## ビルドと実行

1. `Kaeru.sln` を Visual Studio 2022（17.8 以降）で開く
2. NuGet パッケージの復元が自動で走ります（`DocumentFormat.OpenXml` と `PdfPig`）
3. F5 で実行

必要なワークロード: **.NET デスクトップ開発**（.NET 8 SDK 同梱）

コマンドラインからは以下でも動きます。

```
dotnet build Kaeru.sln -c Release
dotnet run --project Kaeru\Kaeru.csproj
```

## 使い方

1. ウィンドウにファイルをドラッグ＆ドロップ（またはフォルダーごとドロップ）
2. 右側で出力形式を選ぶ（複数同時に出力できます）
3. ［変換する］を押す

出力先は既定で入力ファイルと同じ場所です。チェックを外すと任意のフォルダーを指定できます。
画像は `media/` フォルダーに書き出され、Markdown と LaTeX から相対パスで参照されます
（.odt には画像がファイル内に埋め込まれます）。

### LaTeX テンプレート

| 選択肢 | クラス | コンパイル |
|---|---|---|
| 日本語 ltjsarticle | `ltjsarticle` | `lualatex file.tex` |
| 日本語 jsarticle | `jsarticle` (uplatex) | `uplatex file.tex` → `dvipdfmx file.dvi` |
| 欧文 article | `article` | `pdflatex file.tex` |

## 変換される要素

| 要素 | .docx から | .pdf から | → LaTeX | → Markdown | → ODF |
|---|---|---|---|---|---|
| 見出し（6 段階） | スタイル名で判定 | 文字サイズで推定 | `\section` 系 | `#` 見出し | `text:h` |
| 段落 | ✓ | 行の座標から復元 | ✓ | ✓ | ✓ |
| 太字・斜体・等幅 | ✓ | － | `\textbf` 等 | `**` `*` `` ` `` | `text:span` |
| ハイパーリンク | ✓ | － | `\href` | `[text](url)` | `text:a` |
| 箇条書き・番号付き | 番号定義を参照 | 行頭記号で推定 | itemize / enumerate | `-` / `1.` | `text:list` |
| 表 | ✓ | － | `tabular` + booktabs | GFM テーブル | `table:table` |
| 画像 | ✓（原寸を保持） | － | `\includegraphics` | `![](media/…)` | パッケージに埋め込み |
| ページ区切り | ✓ | ページ境界 | `\clearpage` | HTML の改ページ | 改ページ段落 |

## 構造

```
Kaeru.sln
└─ Kaeru/
   ├─ MainWindow.xaml(.cs)          UI とジョブ制御
   ├─ Models/DocumentModel.cs       中間表現（ブロックとインライン）
   ├─ Services/
   │   ├─ DocxReader.cs             OpenXML から中間表現へ
   │   ├─ PdfReader.cs              PdfPig ＋ レイアウト推定で中間表現へ
   │   └─ ConversionService.cs      読み込みと書き出しの統括
   └─ Writers/
       ├─ MarkdownWriter.cs
       ├─ LatexWriter.cs
       └─ OdtWriter.cs              ODF パッケージ（ZIP）を直接生成
```

「読み込み → 中間表現 → 書き出し」の 3 段構成なので、
入力形式（.pptx など）や出力形式（reStructuredText など）を足すときは
`Services` か `Writers` にクラスを 1 つ追加するだけで済みます。

## PDF 変換について

PDF は文字とその座標しか持たないため、本アプリでは次の推定を行っています。

- 文字サイズの中央値を本文サイズとみなし、それより大きい行を見出しとする
- 行の間隔・右端の余り・字下げ・文末記号から段落の切れ目を判断する
- 3 ページ以上で同じ位置に繰り返し現れる短い行と、ページ番号らしい行を除去する
- 日本語（CJK）どうしの行連結では空白を入れず、英単語のハイフン折り返しは結合する

段組み（2 カラム）の PDF、スキャン画像だけの PDF（OCR なし）、PDF 内の表と画像には
対応していません。構造をきちんと保ちたい場合は、元の Word ファイルからの変換をおすすめします。

## 既知の制限

- Word の脚注・コメント・数式 (OMML)・ヘッダー／フッターは取り込みません
- 表のセル結合は解除された状態で出力されます
- LaTeX 出力の表は `tabular` 固定です（ページをまたぐ長い表は `longtable` への手直しが必要）
