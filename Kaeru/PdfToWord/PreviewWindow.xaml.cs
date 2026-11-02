using System.Windows;

namespace PdfToWord
{
    public partial class PreviewWindow : Window
    {
        public PreviewWindow(string fileName, string text)
        {
            InitializeComponent();
            TitleText.Text = fileName;
            Body.Text = string.IsNullOrEmpty(text) ? "文字を読み取れませんでした。" : text;
        }

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(Body.Text); }
            catch { /* ほかのアプリが使用中のときは何もしない */ }
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
