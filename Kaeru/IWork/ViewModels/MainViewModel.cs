using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using IWorkConverter.App.Models;
using IWorkConverter.Core;

namespace IWorkConverter.App.ViewModels
{
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        private static readonly string[] SupportedExtensions = { ".pages", ".numbers", ".key", ".keynote" };

        private bool _isBusy;
        private bool _isDragOver;
        private bool _openWhenDone = true;
        private string _outputFolder = string.Empty;
        private string _log = string.Empty;
        private string _statusLine = "ファイルを追加すると変換できます";
        private double _progress;

        public ObservableCollection<QueueItem> Items { get; } = new ObservableCollection<QueueItem>();
        public ConversionOptions Options { get; } = new ConversionOptions();

        public ICommand AddFilesCommand { get; }
        public ICommand AddFolderCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand RemoveCommand { get; }
        public ICommand ConvertCommand { get; }
        public ICommand ChooseOutputCommand { get; }
        public ICommand OpenOutputCommand { get; }

        public MainViewModel()
        {
            AddFilesCommand = new RelayCommand(_ => AddFiles(), _ => !IsBusy);
            AddFolderCommand = new RelayCommand(_ => AddFolder(), _ => !IsBusy);
            ClearCommand = new RelayCommand(_ => ClearAll(), _ => !IsBusy && Items.Count > 0);
            RemoveCommand = new RelayCommand(p => Remove(p as QueueItem), _ => !IsBusy);
            ConvertCommand = new RelayCommand(async _ => await ConvertAllAsync(), _ => !IsBusy && Items.Count > 0);
            ChooseOutputCommand = new RelayCommand(_ => ChooseOutput(), _ => !IsBusy);
            OpenOutputCommand = new RelayCommand(_ => OpenOutput());

            Items.CollectionChanged += (s, e) => { Raise("HasItems"); Raise("IsEmpty"); UpdateStatusLine(); };
        }

        // ---------- 状態 ----------
        public bool IsBusy
        {
            get { return _isBusy; }
            private set { _isBusy = value; Raise("IsBusy"); Raise("IsIdle"); }
        }

        public bool IsIdle { get { return !_isBusy; } }
        public bool HasItems { get { return Items.Count > 0; } }
        public bool IsEmpty { get { return Items.Count == 0; } }

        public bool IsDragOver
        {
            get { return _isDragOver; }
            set { _isDragOver = value; Raise("IsDragOver"); }
        }

        public double Progress
        {
            get { return _progress; }
            private set { _progress = value; Raise("Progress"); }
        }

        public string StatusLine
        {
            get { return _statusLine; }
            private set { _statusLine = value; Raise("StatusLine"); }
        }

        public string OutputFolder
        {
            get { return _outputFolder; }
            set
            {
                _outputFolder = value ?? string.Empty;
                Options.OutputFolder = string.IsNullOrWhiteSpace(_outputFolder) ? null : _outputFolder;
                Raise("OutputFolder");
            }
        }

        public string Log
        {
            get { return _log; }
            private set { _log = value; Raise("Log"); }
        }

        // ---------- オプション ----------
        public bool DetectHeadings
        {
            get { return Options.DetectHeadings; }
            set { Options.DetectHeadings = value; Raise("DetectHeadings"); }
        }

        public bool IncludeFloatingText
        {
            get { return Options.IncludeFloatingText; }
            set { Options.IncludeFloatingText = value; Raise("IncludeFloatingText"); }
        }

        public bool SkipEmptySlides
        {
            get { return Options.SkipEmptySlides; }
            set { Options.SkipEmptySlides = value; Raise("SkipEmptySlides"); }
        }

        public bool BoldFirstRow
        {
            get { return Options.BoldFirstRow; }
            set { Options.BoldFirstRow = value; Raise("BoldFirstRow"); }
        }

        public bool Overwrite
        {
            get { return Options.Overwrite; }
            set { Options.Overwrite = value; Raise("Overwrite"); }
        }

        public bool OpenWhenDone
        {
            get { return _openWhenDone; }
            set { _openWhenDone = value; Raise("OpenWhenDone"); }
        }

        // ---------- 一覧操作 ----------
        public void AddPaths(IEnumerable<string> paths)
        {
            if (paths == null) return;
            int added = 0;
            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                {
                    if (SupportedExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
                    {
                        if (AddOne(p)) added++;
                    }
                    else
                    {
                        foreach (var child in Directory.EnumerateFileSystemEntries(p, "*", SearchOption.AllDirectories))
                            if (SupportedExtensions.Contains(Path.GetExtension(child).ToLowerInvariant()))
                                if (AddOne(child)) added++;
                    }
                }
                else if (File.Exists(p))
                {
                    if (SupportedExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
                        if (AddOne(p)) added++;
                }
            }

            Append(added == 0
                ? "追加できるファイルがありませんでした（.pages / .numbers / .key）。"
                : added + " 件を追加しました。");
            UpdateStatusLine();
        }

        private bool AddOne(string path)
        {
            if (Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) return false;
            Items.Add(new QueueItem { Path = path });
            return true;
        }

        private void Remove(QueueItem item)
        {
            if (item != null) Items.Remove(item);
        }

        private void ClearAll()
        {
            Items.Clear();
            Log = string.Empty;
            Progress = 0;
            UpdateStatusLine();
        }

        private void UpdateStatusLine()
        {
            if (IsBusy) return;
            int done = Items.Count(i => i.State == ItemState.Done);
            int failed = Items.Count(i => i.State == ItemState.Failed);
            if (Items.Count == 0) StatusLine = "ファイルを追加すると変換できます";
            else if (done + failed == 0) StatusLine = Items.Count + " 件を変換できます";
            else StatusLine = "成功 " + done + " 件 / 失敗 " + failed + " 件";
        }

        // ---------- ダイアログ ----------
        private void AddFiles()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = "iWork ファイルを選択",
                Filter = "iWork ファイル (*.pages;*.numbers;*.key)|*.pages;*.numbers;*.key|すべてのファイル (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true) AddPaths(dlg.FileNames);
        }

        private void AddFolder()
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "フォルダを選択" };
            if (dlg.ShowDialog() == true) AddPaths(new[] { dlg.FolderName });
        }

        private void ChooseOutput()
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "出力先フォルダを選択" };
            if (dlg.ShowDialog() == true) OutputFolder = dlg.FolderName;
        }

        private void OpenOutput()
        {
            string folder = OutputFolder;
            if (string.IsNullOrWhiteSpace(folder))
            {
                var first = Items.FirstOrDefault(i => !string.IsNullOrEmpty(i.Output));
                folder = first != null ? Path.GetDirectoryName(first.Output) : null;
            }
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { Append("フォルダを開けませんでした: " + ex.Message); }
        }

        // ---------- 変換 ----------
        private async Task ConvertAllAsync()
        {
            IsBusy = true;
            Log = string.Empty;
            Progress = 0;

            int total = Items.Count, done = 0, ok = 0;
            foreach (var item in Items.ToList())
            {
                item.State = ItemState.Running;
                item.Detail = string.Empty;
                StatusLine = "変換中 " + (done + 1) + " / " + total + "　" + item.FileName;

                var captured = item;
                var result = await Task.Run(() => ConversionService.Convert(captured.Path, Options));

                if (result.Success)
                {
                    ok++;
                    item.State = ItemState.Done;
                    item.Output = result.OutputPath;
                    item.Detail = Path.GetFileName(result.OutputPath) +
                                  (result.Notes.Count > 0 ? "　·　" + string.Join(" / ", result.Notes) : string.Empty);
                    Append("[OK] " + item.FileName + " → " + Path.GetFileName(result.OutputPath));
                }
                else
                {
                    item.State = ItemState.Failed;
                    item.Detail = result.Message;
                    Append("[NG] " + item.FileName + " : " + result.Message);
                }

                foreach (var n in result.Notes) Append("     - " + n);

                done++;
                Progress = total == 0 ? 0 : (done * 100.0 / total);
            }

            IsBusy = false;
            UpdateStatusLine();
            Append("完了: 成功 " + ok + " 件 / 全 " + total + " 件");

            if (OpenWhenDone && ok > 0) OpenOutput();
        }

        private void Append(string line)
        {
            var sb = new StringBuilder(Log);
            if (sb.Length > 0) sb.Append(Environment.NewLine);
            sb.Append(line);
            Log = sb.ToString();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }
}
