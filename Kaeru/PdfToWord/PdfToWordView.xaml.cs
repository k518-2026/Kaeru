using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PdfToWord.Models;
using PdfToWord.Pdf;
using PdfToWord.Services;

namespace PdfToWord
{
    public partial class PdfToWordView : UserControl
    {
        private readonly ObservableCollection<PdfFileItem> _files = new ObservableCollection<PdfFileItem>();
        private CancellationTokenSource _cancellation;
        private bool _busy;

        /// <summary>ダイアログの親にする Kaeru のウィンドウ。</summary>
        private Window HostWindow { get { return Window.GetWindow(this); } }

        public PdfToWordView()
        {
            InitializeComponent();
            FileList.ItemsSource = _files;
            _files.CollectionChanged += delegate { UpdateEmptyHint(); UpdateStatus(); };
            FillFontLists();
            TxtOutputFolder.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "変換した Word");
            OnSameFolderChanged(this, null);   // 保存先の欄の使用可否をそろえる
            UpdateEmptyHint();
            UpdateStatus();
        }

        // ------------------------------------------------------------ 一覧の操作

        private void OnAddFiles(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog();
            dialog.Title = "変換する PDF を選ぶ";
            dialog.Filter = "PDF ファイル (*.pdf)|*.pdf|すべてのファイル (*.*)|*.*";
            dialog.Multiselect = true;
            if (dialog.ShowDialog(HostWindow) == true) AddPaths(dialog.FileNames);
        }

        private void OnRemoveSelected(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var selected = FileList.SelectedItems.Cast<PdfFileItem>().ToList();
            foreach (PdfFileItem item in selected) _files.Remove(item);
        }

        private void OnClearFiles(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _files.Clear();
        }

        private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ShowPreview(FileList.SelectedItem as PdfFileItem);
        }

        private void OnPreview(object sender, RoutedEventArgs e)
        {
            PdfFileItem item = FileList.SelectedItem as PdfFileItem;
            if (item == null && _files.Count > 0) item = _files[0];
            ShowPreview(item);
        }

        /// <summary>変換前でも中身を読み、どう変換されるかを見せる。</summary>
        private void ShowPreview(PdfFileItem item)
        {
            if (item == null)
            {
                MessageBox.Show(HostWindow, "先に一覧から PDF を選んでください。", "中身を見る",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!string.IsNullOrEmpty(item.Preview))
            {
                new PreviewWindow(item.FileName, item.Preview) { Owner = HostWindow }.ShowDialog();
                return;
            }

            Cursor previous = Cursor;
            Cursor = Cursors.Wait;
            try
            {
                PdfReadOptions read = BuildReadOptions();
                read.PageTo = read.PageTo > 0 ? read.PageTo : 5;   // 下読みは先頭 5 ページまで
                var model = new PdfDocumentReader().Read(item.FullPath, read, null, CancellationToken.None);
                string preview = ConversionService.BuildPreview(model, 20000);
                new PreviewWindow(item.FileName, preview) { Owner = HostWindow }.ShowDialog();
            }
            catch (Exception exception)
            {
                MessageBox.Show(HostWindow, exception.Message, "読み取れませんでした",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                Cursor = previous;
            }
        }

        // ------------------------------------------------------------ 落として追加

        private void OnDragOver(object sender, DragEventArgs e)
        {
            bool ok = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (_busy || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            AddPaths((string[])e.Data.GetData(DataFormats.FileDrop));
        }

        private void AddPaths(IEnumerable<string> paths)
        {
            if (paths == null) return;
            int added = 0, skipped = 0;

            foreach (string path in paths)
            {
                if (Directory.Exists(path))
                {
                    string[] found;
                    try { found = Directory.GetFiles(path, "*.pdf", SearchOption.AllDirectories); }
                    catch { continue; }
                    foreach (string file in found) { if (AddOne(file)) added++; else skipped++; }
                }
                else if (File.Exists(path))
                {
                    if (ConversionService.IsSupported(path)) { if (AddOne(path)) added++; else skipped++; }
                    else skipped++;
                }
            }

            if (added > 0) Log(added + " 件を追加しました。");
            if (skipped > 0) Log(skipped + " 件は PDF ではない、または追加済みのため飛ばしました。");
        }

        private bool AddOne(string path)
        {
            foreach (PdfFileItem existing in _files)
            {
                if (string.Equals(existing.FullPath, path, StringComparison.OrdinalIgnoreCase)) return false;
            }
            _files.Add(new PdfFileItem(path));
            return true;
        }

        // ------------------------------------------------------------ 保存先

        private void OnSameFolderChanged(object sender, RoutedEventArgs e)
        {
            // 画面の読み込み中は下の項目がまだ作られていないので、そのときは何もしない
            if (TxtOutputFolder == null || BtnBrowse == null || ChkSameFolder == null) return;

            bool custom = ChkSameFolder.IsChecked != true;
            TxtOutputFolder.IsEnabled = custom;
            BtnBrowse.IsEnabled = custom;
        }

        private void OnBrowseFolder(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog();
            dialog.Title = "保存先のフォルダーを選ぶ";
            if (Directory.Exists(TxtOutputFolder.Text)) dialog.InitialDirectory = TxtOutputFolder.Text;
            if (dialog.ShowDialog(HostWindow) == true) TxtOutputFolder.Text = dialog.FolderName;
        }

        // ------------------------------------------------------------ 変換

        private async void OnConvert(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (_files.Count == 0)
            {
                MessageBox.Show(HostWindow, "変換する PDF を追加してください。", "PDF → Word 変換",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string outputFolder = null;
            if (ChkSameFolder.IsChecked != true)
            {
                outputFolder = TxtOutputFolder.Text != null ? TxtOutputFolder.Text.Trim() : string.Empty;
                if (outputFolder.Length == 0)
                {
                    MessageBox.Show(HostWindow, "保存先のフォルダーを指定してください。", "PDF → Word 変換",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                try { Directory.CreateDirectory(outputFolder); }
                catch (Exception exception)
                {
                    MessageBox.Show(HostWindow, "保存先を作れませんでした。\n" + exception.Message, "PDF → Word 変換",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            ConversionOptions options = BuildOptions(outputFolder);
            List<PdfFileItem> targets = _files.ToList();

            SetBusy(true);
            _cancellation = new CancellationTokenSource();
            CancellationToken token = _cancellation.Token;
            Progress.Value = 0;

            int done = 0, failed = 0;
            string lastFolder = null;

            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    if (token.IsCancellationRequested) break;

                    PdfFileItem item = targets[i];
                    item.State = ItemState.Converting;
                    item.Message = "読み取っています…";
                    FileList.ScrollIntoView(item);

                    int fileIndex = i;
                    var service = new ConversionService();
                    Action<int, int> progress = delegate (int page, int total)
                    {
                        double ratio = total > 0 ? (double)page / total : 0;
                        double overall = (fileIndex + ratio) / targets.Count * 100.0;
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            Progress.Value = overall;
                            StatusText.Text = string.Format("{0}（{1}/{2} ページ）　{3}/{4} 件目",
                                item.FileName, page, total, fileIndex + 1, targets.Count);
                        }));
                    };

                    Action<string> log = delegate (string message)
                    {
                        Dispatcher.BeginInvoke(new Action(delegate { Log("　" + message); }));
                    };

                    ConversionResult result = await Task.Run(delegate
                    {
                        return service.Convert(item.FullPath, options, log, progress, token);
                    }, token);

                    if (result.Success)
                    {
                        done++;
                        item.State = ItemState.Done;
                        item.OutputPath = result.OutputPath;
                        item.Message = result.Message;
                        item.Preview = ConversionService.BuildPreview(result.Model, 20000);
                        lastFolder = Path.GetDirectoryName(result.OutputPath);
                        Log(item.FileName + " → " + result.OutputPath);
                        if (result.Model != null)
                        {
                            foreach (string note in result.Model.Notes) Log("　　" + note);
                        }
                    }
                    else
                    {
                        failed++;
                        item.State = ItemState.Failed;
                        item.Message = result.Message;
                        Log(item.FileName + "：" + result.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log("中止しました。");
            }
            finally
            {
                foreach (PdfFileItem item in targets)
                {
                    if (item.State == ItemState.Converting)
                    {
                        item.State = ItemState.Waiting;
                        item.Message = "中止しました";
                    }
                }

                Progress.Value = token.IsCancellationRequested ? Progress.Value : 100;
                SetBusy(false);
                _cancellation.Dispose();
                _cancellation = null;

                StatusText.Text = string.Format("{0} 件を変換しました。{1}", done,
                    failed > 0 ? failed + " 件は失敗しています。" : string.Empty);

                if (done > 0 && ChkOpenFolder.IsChecked == true && lastFolder != null && Directory.Exists(lastFolder))
                {
                    try { Process.Start(new ProcessStartInfo(lastFolder) { UseShellExecute = true }); }
                    catch { /* 開けなくても処理は済んでいるので黙って進む */ }
                }
            }
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            if (_cancellation != null)
            {
                _cancellation.Cancel();
                StatusText.Text = "中止しています…";
            }
        }

        // ------------------------------------------------------------ 設定の組み立て

        private PdfReadOptions BuildReadOptions()
        {
            var read = new PdfReadOptions();
            read.DetectHeadings = ChkHeadings.IsChecked == true;
            read.DetectTables = ChkTables.IsChecked == true;
            read.DetectColumns = ChkColumns.IsChecked == true;
            read.ExtractImages = ChkImages.IsChecked == true;
            read.RemoveHeaderFooter = ChkRunningHeads.IsChecked == true;
            read.InsertPageBreaks = ChkPageBreaks.IsChecked == true;
            read.KeepLineBreaks = ModeKeepLines.IsChecked == true;
            read.MergeLines = ModeKeepLines.IsChecked != true;
            read.Password = TxtPassword.Password;
            read.PageNumbers = ParsePageRange(TxtPageRange.Text);
            return read;
        }

        private ConversionOptions BuildOptions(string outputFolder)
        {
            var options = new ConversionOptions();
            options.Read = BuildReadOptions();
            options.Write.JapaneseFont = TextOf(CmbJapaneseFont, "游明朝");
            options.Write.LatinFont = TextOf(CmbLatinFont, "Times New Roman");
            options.Overwrite = ChkAvoidOverwrite.IsChecked != true;
            options.SameFolderAsInput = ChkSameFolder.IsChecked == true;
            options.OutputFolder = outputFolder;
            return options;
        }

        /// <summary>「1-3,7」のような書き方をページ番号の集合にする。空欄なら null（＝すべて）。</summary>
        private static HashSet<int> ParsePageRange(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var pages = new HashSet<int>();

            foreach (string part in text.Replace('、', ',').Replace('，', ',').Split(','))
            {
                string piece = part.Trim().Replace('－', '-').Replace('ー', '-').Replace('〜', '-').Replace('~', '-');
                if (piece.Length == 0) continue;

                int dash = piece.IndexOf('-');
                if (dash > 0)
                {
                    int first, last;
                    if (int.TryParse(piece.Substring(0, dash).Trim(), out first)
                        && int.TryParse(piece.Substring(dash + 1).Trim(), out last))
                    {
                        if (first > last) { int swap = first; first = last; last = swap; }
                        for (int page = Math.Max(1, first); page <= last && page - first < 10000; page++) pages.Add(page);
                    }
                }
                else
                {
                    int single;
                    if (int.TryParse(piece, out single) && single > 0) pages.Add(single);
                }
            }

            return pages.Count > 0 ? pages : null;
        }

        private static string TextOf(ComboBox box, string fallback)
        {
            string value = box.Text != null ? box.Text.Trim() : string.Empty;
            return value.Length > 0 ? value : fallback;
        }

        // ------------------------------------------------------------ 画面まわり

        private void FillFontLists()
        {
            var japanese = new List<string> { "游明朝", "游ゴシック", "MS 明朝", "MS ゴシック", "メイリオ", "BIZ UDP明朝 Medium", "Yu Mincho" };
            var latin = new List<string> { "Times New Roman", "Arial", "Calibri", "Georgia", "Segoe UI", "Cambria" };

            var installed = new List<string>();
            try
            {
                foreach (var family in Fonts.SystemFontFamilies)
                {
                    string name = family.Source;
                    if (!string.IsNullOrEmpty(name)) installed.Add(name);
                }
                installed.Sort(StringComparer.CurrentCulture);
            }
            catch { /* 取得できない環境でも既定の候補で動く */ }

            foreach (string name in installed)
            {
                if (!japanese.Contains(name)) japanese.Add(name);
                if (!latin.Contains(name)) latin.Add(name);
            }

            CmbJapaneseFont.ItemsSource = japanese;
            CmbLatinFont.ItemsSource = latin;
            CmbJapaneseFont.Text = "游明朝";
            CmbLatinFont.Text = "Times New Roman";
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            BtnConvert.IsEnabled = !busy;
            BtnCancel.IsEnabled = busy;
            BtnAdd.IsEnabled = !busy;
            BtnRemove.IsEnabled = !busy;
            BtnClear.IsEnabled = !busy;
            BtnPreview.IsEnabled = !busy;
        }

        private void UpdateEmptyHint()
        {
            EmptyHint.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateStatus()
        {
            if (_busy) return;
            StatusText.Text = _files.Count == 0
                ? "PDF を追加してください。"
                : _files.Count + " 件を変換します。";
        }

        private void Log(string message)
        {
            LogBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            LogBox.ScrollToEnd();
        }
    }
}
