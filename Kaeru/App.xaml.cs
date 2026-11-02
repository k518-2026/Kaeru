using System;
using System.Windows;
using System.Windows.Threading;

namespace Kaeru
{
    public partial class App : Application
    {
        public App()
        {
            // 画面の読み込みや変換で落ちた場合も理由が分かるように受け止める（旧 PdfToWord・iWorkConverter と同じ）
            DispatcherUnhandledException += OnDispatcherUnhandledException;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(e.Exception.ToString(), "Kaeru：エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
