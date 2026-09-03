using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Kaeru.Services;
using Kaeru.Writers;
using Microsoft.Win32;

namespace Kaeru
{
    public class InputFile
    {
        public string FullPath { get; set; }
        public string FileName { get { return Path.GetFileName(FullPath); } }
    }

    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<InputFile> _files = new ObservableCollection<InputFile>();
        private string _lastOutputDir;
        private bool _busy;

        public MainWindow()
        {
            InitializeComponent();
            FileList.ItemsSource = _files;
            TxtOutputDir.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Kaeru");
            Log("ファイルを追加して［変換する］を押してください。");
        }

        // ---------- ファイルの追加・削除 ----------

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "変換するファイルを選択",
                Multiselect = true,
                Filter = "対応ファイル (*.docx;*.pdf)|*.docx;*.pdf|Word 文書 (*.docx)|*.docx|PDF (*.pdf)|*.pdf|すべてのファイル (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) == true) AddPaths(dlg.FileNames);
        }

        private void RemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in FileList.SelectedItems.Cast<InputFile>().ToList())
                _files.Remove(item);
            UpdateStatus();
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            _files.Clear();
            UpdateStatus();
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            AddPaths(paths);
            e.Handled = true;
        }

        private void AddPaths(IEnumerable<string> paths)
        {
            int added = 0, skipped = 0;

            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                {
                    var inFolder = Directory.EnumerateFiles(p, "*.*", SearchOption.TopDirectoryOnly)
                                            .Where(ConversionService.IsSupported);
                    foreach (var f in inFolder)
                    {
                        if (TryAdd(f)) added++;
                    }
                    continue;
                }

                if (!ConversionService.IsSupported(p)) { skipped++; continue; }
                if (TryAdd(p)) added++;
            }

            if (added > 0) Log(string.Format("{0} 件のファイルを追加しました。", added));
            if (skipped > 0) Log(string.Format("{0} 件は対応していない形式のため無視しました（.docx と .pdf のみ）。", skipped));
            UpdateStatus();
        }

        private bool TryAdd(string path)
        {
            if (_files.Any(f => string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase))) return false;
            _files.Add(new InputFile { FullPath = path });
            return true;
        }

        // ---------- 出力先 ----------

        private void SameFolder_Changed(object sender, RoutedEventArgs e)
        {
            bool same = ChkSameFolder.IsChecked == true;
            if (TxtOutputDir != null) TxtOutputDir.IsEnabled = !same;
            if (BtnBrowseOutput != null) BtnBrowseOutput.IsEnabled = !same;
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "出力先フォルダーを選択" };
            if (Directory.Exists(TxtOutputDir.Text)) dlg.InitialDirectory = TxtOutputDir.Text;
            if (dlg.ShowDialog(this) == true) TxtOutputDir.Text = dlg.FolderName;
        }

        private void OpenOutput_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastOutputDir) || !Directory.Exists(_lastOutputDir)) return;
            Process.Start(new ProcessStartInfo { FileName = _lastOutputDir, UseShellExecute = true });
        }

        // ---------- 変換 ----------

        private async void Convert_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            if (_files.Count == 0)
            {
                MessageBox.Show(this, "変換するファイルを追加してください。", "Kaeru",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var options = BuildOptions();
            if (options == null) return;

            bool sameFolder = ChkSameFolder.IsChecked == true;
            string fixedDir = TxtOutputDir.Text;
            if (!sameFolder && string.IsNullOrWhiteSpace(fixedDir))
            {
                MessageBox.Show(this, "出力先フォルダーを指定してください。", "Kaeru",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var targets = _files.Select(f => f.FullPath).ToList();
            SetBusy(true);
            Progress.Value = 0;
            Log("──────── 変換開始 ────────");

            int ok = 0, failed = 0;
            string lastDir = null;

            for (int i = 0; i < targets.Count; i++)
            {
                string input = targets[i];
                string outDir = sameFolder ? Path.GetDirectoryName(input) : fixedDir;

                try
                {
                    var service = new ConversionService();
                    var produced = await Task.Run(() =>
                        service.Convert(input, outDir, options, msg => Dispatcher.Invoke(() => Log(msg))));

                    if (produced.Count > 0) lastDir = outDir;
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Log("  エラー: " + Path.GetFileName(input) + " － " + ex.Message);
                }

                Progress.Value = (i + 1) * 100.0 / targets.Count;
            }

            _lastOutputDir = lastDir;
            BtnOpenOutput.IsEnabled = !string.IsNullOrEmpty(lastDir) && Directory.Exists(lastDir);
            Log(string.Format("──────── 完了: 成功 {0} 件 / 失敗 {1} 件 ────────", ok, failed));
            SetBusy(false);
            UpdateStatus();
        }

        private ConversionOptions BuildOptions()
        {
            var formats = OutputFormats.None;
            if (ChkMarkdown.IsChecked == true) formats |= OutputFormats.Markdown;
            if (ChkLatex.IsChecked == true) formats |= OutputFormats.Latex;
            if (ChkOdf.IsChecked == true) formats |= OutputFormats.Odf;

            if (formats == OutputFormats.None)
            {
                MessageBox.Show(this, "出力形式を 1 つ以上選んでください。", "Kaeru",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            LatexTemplate template;
            switch (CmbLatexTemplate.SelectedIndex)
            {
                case 1: template = LatexTemplate.JapaneseUpLatex; break;
                case 2: template = LatexTemplate.Article; break;
                default: template = LatexTemplate.JapaneseLuaLatex; break;
            }

            return new ConversionOptions
            {
                Formats = formats,
                LatexTemplate = template,
                ExtractImages = ChkImages.IsChecked == true,
                MarkdownFrontMatter = ChkFrontMatter.IsChecked == true,
                StripRunningHeads = ChkStripHeads.IsChecked == true,
                Overwrite = ChkOverwrite.IsChecked == true
            };
        }

        // ---------- 表示 ----------

        private void SetBusy(bool busy)
        {
            _busy = busy;
            BtnConvert.IsEnabled = !busy;
            BtnConvert.Content = busy ? "変換中..." : "変換する";
            Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
        }

        private void UpdateStatus()
        {
            TxtStatus.Text = string.Format("{0} 件のファイル", _files.Count);
        }

        private void Log(string message)
        {
            TxtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            TxtLog.ScrollToEnd();
        }
    }
}
