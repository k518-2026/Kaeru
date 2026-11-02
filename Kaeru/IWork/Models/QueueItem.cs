using System.ComponentModel;
using System.Windows.Media;
using IWorkConverter.Core;
using IWorkConverter.Core.Package;

namespace IWorkConverter.App.Models
{
    public enum ItemState
    {
        Waiting,
        Running,
        Done,
        Failed
    }

    public sealed class QueueItem : INotifyPropertyChanged
    {
        private ItemState _state = ItemState.Waiting;
        private string _detail = string.Empty;
        private string _output = string.Empty;

        public string Path { get; set; }
        public string FileName { get { return System.IO.Path.GetFileName(Path); } }
        public string Folder { get { return System.IO.Path.GetDirectoryName(Path); } }

        public IWorkKind Kind { get { return ConversionService.PeekKind(Path); } }

        /// <summary>種別バッジの文字（PAGES / NUMBERS / KEYNOTE）。</summary>
        public string BadgeText
        {
            get
            {
                switch (Kind)
                {
                    case IWorkKind.Pages: return "PAGES";
                    case IWorkKind.Numbers: return "NUMB";
                    case IWorkKind.Keynote: return "KEY";
                    default: return "?";
                }
            }
        }

        public Brush BadgeBackground { get { return Hex(BadgeBackHex); } }
        public Brush BadgeForeground { get { return Hex(BadgeForeHex); } }

        private string BadgeBackHex
        {
            get
            {
                switch (Kind)
                {
                    case IWorkKind.Pages: return "#E7EEFE";
                    case IWorkKind.Numbers: return "#E4F5EC";
                    case IWorkKind.Keynote: return "#FDEFE2";
                    default: return "#EEF0F4";
                }
            }
        }

        private string BadgeForeHex
        {
            get
            {
                switch (Kind)
                {
                    case IWorkKind.Pages: return "#1A4FD6";
                    case IWorkKind.Numbers: return "#0B7A45";
                    case IWorkKind.Keynote: return "#B4560A";
                    default: return "#7A8494";
                }
            }
        }

        /// <summary>「Pages → .docx」のような変換方向の表示。</summary>
        public string Route
        {
            get
            {
                switch (Kind)
                {
                    case IWorkKind.Pages: return "Pages → .docx";
                    case IWorkKind.Numbers: return "Numbers → .xlsx";
                    case IWorkKind.Keynote: return "Keynote → .pptx";
                    default: return "未対応の形式";
                }
            }
        }

        public ItemState State
        {
            get { return _state; }
            set
            {
                _state = value;
                Raise("State"); Raise("StatusText"); Raise("StatusBackground"); Raise("StatusForeground");
            }
        }

        public string StatusText
        {
            get
            {
                switch (_state)
                {
                    case ItemState.Running: return "変換中";
                    case ItemState.Done: return "完了";
                    case ItemState.Failed: return "失敗";
                    default: return "待機中";
                }
            }
        }

        public Brush StatusBackground
        {
            get
            {
                switch (_state)
                {
                    case ItemState.Running: return Hex("#EEEEFC");
                    case ItemState.Done: return Hex("#E4F5EC");
                    case ItemState.Failed: return Hex("#FDEAEA");
                    default: return Hex("#EEF0F4");
                }
            }
        }

        public Brush StatusForeground
        {
            get
            {
                switch (_state)
                {
                    case ItemState.Running: return Hex("#4F46E5");
                    case ItemState.Done: return Hex("#0B7A45");
                    case ItemState.Failed: return Hex("#C62B2B");
                    default: return Hex("#6B7482");
                }
            }
        }

        public string Detail
        {
            get { return _detail; }
            set { _detail = value; Raise("Detail"); Raise("HasDetail"); }
        }

        public bool HasDetail { get { return !string.IsNullOrWhiteSpace(_detail); } }

        public string Output
        {
            get { return _output; }
            set { _output = value; Raise("Output"); }
        }

        private static Brush Hex(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }
}
