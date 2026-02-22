using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace VR180_Upscaler
{
    /// <summary>
    /// アプリケーションのメインウィンドウ。ユーザーとの対話と処理の開始を担当する。
    /// </summary>
    public partial class MainWindow : Window
    {
        private UpscaleEngine _engine;

        public MainWindow()
        {
            InitializeComponent();
            _engine = new UpscaleEngine(LogMessage);
            PopulateDevices();
            LogMessage("[報告] アプリケーション v1.0.0 を初期化しました。");
        }

        /// <summary>
        /// 利用可能なデバイスを列挙し、コンボボックスに反映する。
        /// </summary>
        private void PopulateDevices()
        {
            var devices = _engine.GetModelManager().GetAvailableDevices();
            ProviderComboBox.Items.Clear();
            foreach (var device in devices)
            {
                ProviderComboBox.Items.Add(new ComboBoxItem 
                { 
                    Content = device.Name,
                    Tag = device.Id
                });
            }
            ProviderComboBox.SelectedIndex = 0;
        }

        /// <summary>
        /// ログメッセージを UI のテキストボックスに出力する。
        /// </summary>
        /// <param name="message">出力するメッセージ。</param>
        private void LogMessage(string message)
        {
            Dispatcher.InvokeAsync(() =>
            {
                LogTextBox.AppendText($"{DateTime.Now:HH:mm:ss} {message}\n");
                LogTextBox.ScrollToEnd();
            });
        }

        /// <summary>
        /// ファイルがドロップされた際のイベントハンドラー。
        /// </summary>
        private async void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    string file = files[0];
                    string multiplierStr = ((ComboBoxItem)MultiplierComboBox.SelectedItem).Content.ToString() ?? "2x";
                    double scale = 2.0;
                    if (double.TryParse(multiplierStr.Replace("x", ""), out double result))
                    {
                        scale = result;
                    }

                    var selectedItem = (ComboBoxItem)ProviderComboBox.SelectedItem;
                    int deviceId = selectedItem != null ? (int)selectedItem.Tag : -1;
                    string providerName = selectedItem?.Content.ToString() ?? "Unknown";

                    LogMessage($"[報告] 処理開始: {file} (倍率: {multiplierStr}, デバイス: {providerName})");
                    
                    // 処理中はドロップを無効化
                    AllowDrop = false;
                    try
                    {
                        await _engine.ProcessAsync(file, scale, deviceId);
                    }
                    finally
                    {
                        AllowDrop = true;
                    }
                }
            }
        }
    }
}
