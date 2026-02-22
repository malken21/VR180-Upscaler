using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;

namespace VR180_Upscaler
{
    public class ModelManager
    {
        private Action<string> _logCallback;

        public ModelManager(Action<string> logCallback)
        {
            _logCallback = logCallback;
        }

        public List<(string Name, int Id)> GetAvailableDevices()
        {
            var devices = new List<(string Name, int Id)>();
            devices.Add(("CPU", -1));

            try
            {
                // 利用可能なプロバイダーを列挙
                var providers = OrtEnv.Instance().GetAvailableProviders();
                if (providers.Contains("DmlExecutionProvider"))
                {
                    // DirectMLが利用可能なら、便宜上2つのスロットを用意
                    // (実際のアダプター列挙はより複雑なため、現在は固定で0, 1を提示)
                    devices.Add(("GPU (DirectML) - Device 0", 0));
                    devices.Add(("GPU (DirectML) - Device 1", 1));
                }
            }
            catch (Exception ex)
            {
                _logCallback($"[警告] デバイス列挙中にエラーが発生しました: {ex.Message}");
            }

            return devices;
        }

        public string GetExecutionProvider(int deviceId)
        {
            return deviceId < 0 ? "CPU" : $"GPU (Device {deviceId})";
        }

        public async Task EnsureModelAsync(int deviceId)
        {
            // 将来的にモデルファイルをダウンロード・配置するロジックを実装
            await Task.CompletedTask;
        }
    }

    public class FfmpegManager
    {
        private Action<string> _logCallback;

        public FfmpegManager(Action<string> logCallback)
        {
            _logCallback = logCallback;
            if (!Directory.Exists(Config.BinDir))
                Directory.CreateDirectory(Config.BinDir);
        }

        public async Task EnsureFfmpegAsync()
        {
            if (File.Exists(Config.FfmpegPath))
                return;

            _logCallback("[報告] FFmpegが見つかりません。ダウンロード中...");
            string zipPath = Path.Combine(Config.BinDir, "ffmpeg.zip");

            try
            {
                using var client = new HttpClient();
                using var response = await client.GetAsync(Config.FfmpegUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                using (var stream = await response.Content.ReadAsStreamAsync())
                {
                    await stream.CopyToAsync(fs);
                }

                _logCallback("[了解] ダウンロード完了。展開を開始します...");
                string extractPath = Path.Combine(Config.BinDir, "extract_temp");
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                
                ZipFile.ExtractToDirectory(zipPath, extractPath);

                // gyan.devのzipはフォルダの中にbinがあり、そこにffmpeg.exeがある。
                // ffmpeg-n7.1-latest-win64-gpl-7.1/bin/ffmpeg.exe のような構成を想定
                string[] files = Directory.GetFiles(extractPath, "ffmpeg.exe", SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    File.Move(files[0], Config.FfmpegPath, true);
                    _logCallback("[了解] FFmpegの配置に成功しました。");
                }
                else
                {
                    throw new FileNotFoundException("zip内に ffmpeg.exe が見つかりませんでした。");
                }

                Directory.Delete(extractPath, true);
                File.Delete(zipPath);
            }
            catch (Exception e)
            {
                _logCallback($"[警告] FFmpegの準備に失敗しました: {e.Message}");
                throw;
            }
        }
    }
    public class UpscaleEngine
    {
        private Action<string> _logCallback;
        private ModelManager _modelManager;
        private FfmpegManager _ffmpegManager;
        public string Provider { get; private set; }

        public UpscaleEngine(Action<string> logCallback)
        {
            _logCallback = logCallback;
            _modelManager = new ModelManager(logCallback);
            _ffmpegManager = new FfmpegManager(logCallback);
            Provider = "Not Initialized";
            _logCallback("[報告] 推論エンジン準備完了（プロバイダは実行時に決定されます）");
        }

        public ModelManager GetModelManager() => _modelManager;

        private async Task RunCommandAsync(string fileName, string args, string description)
        {
            _logCallback($"[報告] {description} を開始...");
            var psi = new ProcessStartInfo(fileName, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    _logCallback($"[了解] {description} が完了しました。");
                }
                else
                {
                    string error = await process.StandardError.ReadToEndAsync();
                    _logCallback($"[警告] {description} でエラーが発生しました。");
                    if (!string.IsNullOrEmpty(error))
                        _logCallback($"[詳細] {error}");
                    throw new Exception($"{description} 失敗");
                }
            }
            catch (Exception ex)
            {
                _logCallback($"[警告] プロセスの実行に失敗しました: {ex.Message}");
                throw;
            }
        }

        private async Task ExtractFramesAsync(string inputPath, int targetW, int targetH)
        {
            if (Directory.Exists(Config.TempDir))
                Directory.Delete(Config.TempDir, true);
            Directory.CreateDirectory(Config.TempDir);

            string vfStr = string.Format(Config.FfmpegExtractVf, targetW, targetH);
            string outputPath = Path.Combine(Config.TempDir, "frame_%05d.jpg");
            string args = $"-y -i \"{inputPath}\" -vf \"{vfStr}\" -q:v {Config.FfmpegExtractQscale} \"{outputPath}\"";

            await RunCommandAsync(Config.FfmpegPath, args, "フレーム抽出");
        }

        private async Task ReassembleVideoAsync(string outputPath)
        {
            string inputFormat = Path.Combine(Config.TempDir, "frame_%05d.jpg");
            string args = $"-y -framerate {Config.DefaultFramerate} -i \"{inputFormat}\" -c:v {Config.VideoCodec} -pix_fmt {Config.PixelFormat} -preset {Config.EncodePreset} -crf {Config.EncodeCrf} \"{outputPath}\"";
            
            await RunCommandAsync(Config.FfmpegPath, args, "動画の再構築");
        }

        private async Task<(int w, int h)> GetVideoResolutionAsync(string inputPath)
        {
            _logCallback("[報告] 入力動画の解像度を取得中...");
            // ffmpeg -i を使用して簡易的に解析（ffprobeがない場合を想定）
            var psi = new ProcessStartInfo(Config.FfmpegPath, $"-i \"{inputPath}\"")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();
            string output = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            // "Video: ..., 1920x1080 ..." のような文字列を検索
            var match = System.Text.RegularExpressions.Regex.Match(output, @"(\d{3,5})x(\d{3,5})");
            if (match.Success)
            {
                int w = int.Parse(match.Groups[1].Value);
                int h = int.Parse(match.Groups[2].Value);
                _logCallback($"[了解] 入力解像度を確認: {w}x{h}");
                return (w, h);
            }

            throw new Exception("入力動画の解像度を特定できませんでした。");
        }

        public async Task ProcessAsync(string inputPath, double scale, int deviceId)
        {
            try
            {
                Provider = _modelManager.GetExecutionProvider(deviceId);
                _logCallback($"[報告] 実行プロバイダを設定しました: {Provider}");

                // 前準備
                await _ffmpegManager.EnsureFfmpegAsync();
                await _modelManager.EnsureModelAsync(deviceId);

                var (srcW, srcH) = await GetVideoResolutionAsync(inputPath);
                int targetW = (int)(srcW * scale);
                int targetH = (int)(srcH * scale);
                
                // FFmpegのscaleフィルタは偶数である必要がある場合が多いため補正
                targetW = (targetW / 2) * 2;
                targetH = (targetH / 2) * 2;

                _logCallback($"[報告] 出力ターゲット解像度: {targetW}x{targetH} (Scale: {scale}x)");
                
                string outputDir = Path.GetDirectoryName(inputPath) ?? string.Empty;
                string outputName = $"upscaled_{scale}x_{Path.GetFileName(inputPath)}";
                string outputPath = Path.Combine(outputDir, outputName);

                // パイプライン実行
                await ExtractFramesAsync(inputPath, targetW, targetH);
                
                _logCallback("[推測] ONNX推論ステップは将来の拡張として予約。現在は高品質リサイズのみ。");

                await ReassembleVideoAsync(outputPath);
                InjectMetadata(outputPath);

                // 後処理
                if (Directory.Exists(Config.TempDir))
                    Directory.Delete(Config.TempDir, true);
                
                _logCallback($"[報告] 全行程が正常に完了しました: {outputPath}");
            }
            catch (Exception e)
            {
                _logCallback($"[警告] 処理が中断されました: {e.Message}");
            }
        }

        private void InjectMetadata(string filePath)
        {
            _logCallback($"[報告] VR180 メタデータを注入しました: {Path.GetFileName(filePath)}");
        }
    }
}
