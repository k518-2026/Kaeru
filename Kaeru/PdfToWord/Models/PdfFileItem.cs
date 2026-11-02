using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace PdfToWord.Models
{
    public enum ItemState
    {
        Waiting,
        Converting,
        Done,
        Failed
    }

    /// <summary>一覧に並ぶ 1 つの PDF。</summary>
    public sealed class PdfFileItem : INotifyPropertyChanged
    {
        private ItemState _state = ItemState.Waiting;
        private string _message = "未変換";
        private string _outputPath;
        private string _preview;

        public PdfFileItem(string path)
        {
            FullPath = path;
        }

        public string FullPath { get; private set; }

        public string FileName { get { return Path.GetFileName(FullPath); } }

        public string FolderName { get { return Path.GetDirectoryName(FullPath) ?? string.Empty; } }

        public ItemState State
        {
            get { return _state; }
            set
            {
                if (_state == value) return;
                _state = value;
                OnPropertyChanged();
                OnPropertyChanged("StateLabel");
            }
        }

        public string StateLabel
        {
            get
            {
                switch (State)
                {
                    case ItemState.Waiting: return "待機";
                    case ItemState.Converting: return "変換中";
                    case ItemState.Done: return "完了";
                    default: return "失敗";
                }
            }
        }

        public string Message
        {
            get { return _message; }
            set
            {
                if (_message == value) return;
                _message = value;
                OnPropertyChanged();
            }
        }

        /// <summary>できあがった .docx の場所。まだ変換していなければ null。</summary>
        public string OutputPath
        {
            get { return _outputPath; }
            set { _outputPath = value; OnPropertyChanged(); }
        }

        /// <summary>変換後に画面で見せる下読み用のテキスト。</summary>
        public string Preview
        {
            get { return _preview; }
            set { _preview = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
