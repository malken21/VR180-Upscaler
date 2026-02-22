using System.Windows;

namespace VR180_Upscaler
{
    public partial class App : Application
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (e.Args.Length > 0)
            {
                // 親プロセスのコンソールにアタッチを試みる
                AttachConsole(ATTACH_PARENT_PROCESS);
                await RunCliAsync(e.Args);
            }
            else
            {
                var mainWindow = new MainWindow();
                mainWindow.Show();
            }
        }

        private async System.Threading.Tasks.Task RunCliAsync(string[] args)
        {
            // [報告] CLI モードでの実行を開始。
            string inputPath = "";
            double scale = 2.0;
            int deviceId = -1;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-i":
                    case "--input":
                        if (i + 1 < args.Length) inputPath = args[++i];
                        break;
                    case "-s":
                    case "--scale":
                        if (i + 1 < args.Length && double.TryParse(args[++i], out double s)) scale = s;
                        break;
                    case "-d":
                    case "--device":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out int d)) deviceId = d;
                        break;
                    case "-h":
                    case "--help":
                        ShowUsage();
                        Shutdown();
                        return;
                    default:
                        if (string.IsNullOrEmpty(inputPath) && !args[i].StartsWith("-"))
                        {
                            inputPath = args[i];
                        }
                        break;
                }
            }

            if (string.IsNullOrEmpty(inputPath))
            {
                // [警告] 入力パスが指定されていません。
                ShowUsage();
                Shutdown();
                return;
            }

            if (!System.IO.File.Exists(inputPath))
            {
                // [警告] 指定されたファイルが存在しません: inputPath
                System.Console.WriteLine($"[警告] ファイルが見つかりません: {inputPath}");
                Shutdown();
                return;
            }

            var engine = new UpscaleEngine(msg => 
            {
                System.Console.WriteLine($"{System.DateTime.Now:HH:mm:ss} {msg}");
            });

            try
            {
                await engine.ProcessAsync(inputPath, scale, deviceId);
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine($"[警告] 致命的なエラーが発生しました: {ex.Message}");
            }
            finally
            {
                Shutdown();
            }
        }

        private void ShowUsage()
        {
            System.Console.WriteLine("Usage: VR180-Upscaler.exe [input_path] [options]");
            System.Console.WriteLine("Options:");
            System.Console.WriteLine("  -i, --input <path>   Input video file path");
            System.Console.WriteLine("  -s, --scale <value>   Upscale scale (default: 2.0)");
            System.Console.WriteLine("  -d, --device <id>    Execution device ID (-1: CPU, 0+: GPU) (default: -1)");
            System.Console.WriteLine("  -h, --help           Show this help message");
        }
    }
}
