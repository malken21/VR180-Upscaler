using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VR180_Upscaler
{
    /// <summary>
    /// AI モデルの管理、および実行デバイスの列挙を担当するクラス。
    /// </summary>
    public class ModelManager
    {
        private Action<string> _logCallback;

        public ModelManager(Action<string> logCallback)
        {
            _logCallback = logCallback;
        }

        /// <summary>
        /// 利用可能な推論デバイス（CPU および GPU）のリストを取得する。
        /// </summary>
        /// <returns>デバイス名と ID のタプルリスト。</returns>
        public List<(string Name, int Id)> GetAvailableDevices()
        {
            var devices = new List<(string Name, int Id)>();
            devices.Add(("CPU", -1));

            try
            {
                // ONNX Runtime の環境から利用可能なプロバイダーを列挙
                var providers = OrtEnv.Instance().GetAvailableProviders();
                if (providers.Contains("DmlExecutionProvider"))
                {
                    // DirectML が利用可能なら、便宜上 2 つのスロットを用意
                    // (実際のアダプター列挙はより複雑なため、現在は固定で 0, 1 を提示)
                    devices.Add(("GPU (DirectML) - Device 0", 0));
                    devices.Add(("GPU (DirectML) - Device 1", 1));
                }
            }
            catch (Exception ex)
            {
                _logCallback($"デバイス列挙中にエラーが発生しました: {ex.Message}");
            }

            return devices;
        }

        /// <summary>
        /// デバイス ID に対応する実行プロバイダーの表示名を取得する。
        /// </summary>
        /// <param name="deviceId">デバイス ID (-1 は CPU)。</param>
        /// <returns>プロバイダー名。</returns>
        public string GetExecutionProvider(int deviceId)
        {
            return deviceId < 0 ? "CPU" : $"GPU (Device {deviceId})";
        }

        /// <summary>
        /// 実行に必要なモデルファイルが存在することを確認し、必要に応じて ZIP をダウンロードして展開する。
        /// </summary>
        /// <param name="deviceId">ターゲットデバイス ID。</param>
        public async Task EnsureModelAsync(int deviceId)
        {
            string modelPath = System.IO.Path.Combine(Config.ModelsDir, Config.ModelName);
            if (File.Exists(modelPath))
            {
                _logCallback($"モデルを確認: {Config.ModelName}");
                return;
            }

            if (!Directory.Exists(Config.ModelsDir))
                Directory.CreateDirectory(Config.ModelsDir);

            _logCallback($"モデルが見つかりません。ダウンロードを開始: {Config.ModelName}");
            string zipPath = System.IO.Path.Combine(Config.ModelsDir, "model_temp.zip");

            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromMinutes(10);
                using var response = await client.GetAsync(Config.ModelZipUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                using (var stream = await response.Content.ReadAsStreamAsync())
                    await stream.CopyToAsync(fs);

                _logCallback("モデルアーカイブのダウンロード完了。展開中...");

                using var archive = ZipFile.OpenRead(zipPath);
                var entry = archive.GetEntry(Config.ModelZipInternalPath)
                    ?? throw new FileNotFoundException($"ZIP 内にモデルが見つかりません: {Config.ModelZipInternalPath}");
                entry.ExtractToFile(modelPath, overwrite: true);

                _logCallback($"モデルの配置に成功: {Config.ModelName}");
            }
            finally
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
            }
        }
    }

    /// <summary>
    /// FFmpeg バイナリの管理と配置を担当するクラス。
    /// </summary>
    public class FfmpegManager
    {
        private Action<string> _logCallback;

        public FfmpegManager(Action<string> logCallback)
        {
            _logCallback = logCallback;
            if (!Directory.Exists(Config.BinDir))
                Directory.CreateDirectory(Config.BinDir);
        }

        /// <summary>
        /// FFmpeg が実行パスに存在することを確認し、存在しない場合は自動的にダウンロード・展開する。
        /// </summary>
        public async Task EnsureFfmpegAsync()
        {
            if (File.Exists(Config.FfmpegPath))
                return;

            _logCallback("FFmpeg が見つかりません。ダウンロードを開始。");
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

                _logCallback("ダウンロード完了。展開を開始。");
                string extractPath = Path.Combine(Config.BinDir, "extract_temp");
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                
                ZipFile.ExtractToDirectory(zipPath, extractPath);

                // gyan.dev の ZIP 構成 (bin/ffmpeg.exe) を想定して検索
                string[] files = Directory.GetFiles(extractPath, "ffmpeg.exe", SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    File.Move(files[0], Config.FfmpegPath, true);
                    _logCallback("FFmpeg の配置に成功。");
                }
                else
                {
                    throw new FileNotFoundException("アーカイブ内に ffmpeg.exe が見つかりませんでした。");
                }

                Directory.Delete(extractPath, true);
                File.Delete(zipPath);
            }
            catch (Exception e)
            {
                _logCallback($"FFmpeg の準備に失敗しました: {e.Message}");
                throw;
            }
        }
    }

    /// <summary>
    /// VR180 ビデオのアップスケール処理を統括するエンジンクラス。
    /// </summary>
    public class UpscaleEngine
    {
        private Action<string> _logCallback;
        private ModelManager _modelManager;
        private FfmpegManager _ffmpegManager;
        
        /// <summary>
        /// 現在設定されている実行プロバイダー名。
        /// </summary>
        public string Provider { get; private set; }

        public UpscaleEngine(Action<string> logCallback)
        {
            _logCallback = logCallback;
            _modelManager = new ModelManager(logCallback);
            _ffmpegManager = new FfmpegManager(logCallback);
            Provider = "未初期化";
            _logCallback("推論エンジン準備完了。実行プロバイダーは動的に決定。");
        }

        public ModelManager GetModelManager() => _modelManager;

        /// <summary>
        /// 外部コマンドを非同期で実行する。
        /// </summary>
        /// <param name="fileName">実行ファイル名。</param>
        /// <param name="args">コマンド引数。</param>
        /// <param name="description">ログ用の処理説明。</param>
        private async Task RunCommandAsync(string fileName, string args, string description)
        {
            _logCallback($"{description} を開始...");
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
                    _logCallback($"{description} が完了。");
                }
                else
                {
                    string error = await process.StandardError.ReadToEndAsync();
                    _logCallback($"{description} でエラーが発生。");
                    if (!string.IsNullOrEmpty(error))
                        _logCallback($"{error}");
                    throw new Exception($"{description} 失敗");
                }
            }
            catch (Exception ex)
            {
                _logCallback($"プロセスの実行に失敗しました: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 動画から静止画フレームを抽出する。
        /// </summary>
        /// <param name="inputPath">入力動画パス。</param>
        /// <param name="targetW">抽出時の横幅。</param>
        /// <param name="targetH">抽出時の高さ。</param>
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

        /// <summary>
        /// 処理済みフレームを動画として再構築する。
        /// </summary>
        /// <param name="outputPath">出力動画パス。</param>
        private async Task ReassembleVideoAsync(string outputPath)
        {
            string inputFormat = Path.Combine(Config.TempDir, "upscaled_frame_%05d.jpg");
            string args = $"-y -framerate {Config.DefaultFramerate} -i \"{inputFormat}\" -c:v {Config.VideoCodec} -pix_fmt {Config.PixelFormat} -preset {Config.EncodePreset} -crf {Config.EncodeCrf} \"{outputPath}\"";
            
            await RunCommandAsync(Config.FfmpegPath, args, "動画の再構築");
        }

        /// <summary>
        /// FFmpeg を使用して入力動画の解像度を取得する。
        /// </summary>
        /// <param name="inputPath">対象ファイルパス。</param>
        /// <returns>横幅と高さのタプル。</returns>
        private async Task<(int w, int h)> GetVideoResolutionAsync(string inputPath)
        {
            _logCallback("入力動画の解像度を確認中...");
            // ffprobe がない環境を想定し、ffmpeg -i のエラー出力から解像度をパースする
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

            // "Video: ..., 1920x1080 ..." といった形式の文字列から数値を抽出
            var match = System.Text.RegularExpressions.Regex.Match(output, @"(\d{3,5})x(\d{3,5})");
            if (match.Success)
            {
                int w = int.Parse(match.Groups[1].Value);
                int h = int.Parse(match.Groups[2].Value);
                _logCallback($"入力解像度: {w}x{h}");
                return (w, h);
            }

            throw new Exception("入力動画の解像度を特定できませんでした。");
        }

        /// <summary>
        /// 一時ディレクトリ内の全フレームに対して ONNX 推論アップスケールを実行する。
        /// </summary>
        /// <param name="modelPath">ONNX モデルファイルのパス。</param>
        /// <param name="deviceId">出力デバイス ID。</param>
        private async Task UpscaleFramesAsync(string modelPath, int deviceId)
        {
            await Task.Run(() =>
            {
                // ONNX セッションオプションの構築
                var options = new SessionOptions();
                if (deviceId >= 0)
                {
                    options.AppendExecutionProvider_DML(deviceId);
                    _logCallback($"DirectML デバイス {deviceId} を使用します。");
                }
                else
                {
                    _logCallback("CPU で推論を実行します。");
                }
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

                using var session = new InferenceSession(modelPath, options);
                string inputName = session.InputMetadata.Keys.First();
                string outputName = session.OutputMetadata.Keys.First();

                string[] frames = Directory.GetFiles(Config.TempDir, "frame_*.jpg")
                    .OrderBy(f => f)
                    .ToArray();

                _logCallback($"ONNX 推論開始: {frames.Length} フレーム");

                for (int i = 0; i < frames.Length; i++)
                {
                    string framePath = frames[i];
                    string outputFrameName = $"upscaled_frame_{(i + 1):D5}.jpg";
                    string outputFramePath = Path.Combine(Config.TempDir, outputFrameName);

                    // 画像の読み込みと NCHW float32 テンソル変換 (LockBits による高速化)
                    using var bmpSrc = System.Drawing.Image.FromFile(framePath);
                    using var bmpResized = new System.Drawing.Bitmap(bmpSrc);
                    int h = bmpResized.Height;
                    int w = bmpResized.Width;
                    var inputData = new float[1 * 3 * h * w];

                    // Format24bppRgb に変換して LockBits でメモリを直接参照
                    using var bmp24 = bmpResized.Clone(
                        new System.Drawing.Rectangle(0, 0, w, h),
                        System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    var bmpData = bmp24.LockBits(
                        new System.Drawing.Rectangle(0, 0, w, h),
                        System.Drawing.Imaging.ImageLockMode.ReadOnly,
                        System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    try
                    {
                        int stride = bmpData.Stride;
                        byte[] rowBuf = new byte[stride];
                        int planeSize = h * w;
                        for (int y = 0; y < h; y++)
                        {
                            // 1行分を配列にコピー
                            Marshal.Copy(bmpData.Scan0 + y * stride, rowBuf, 0, stride);
                            int rowBase = y * w;
                            for (int x = 0; x < w; x++)
                            {
                                // BGRレイアウト
                                int bmpOff = x * 3;
                                inputData[0 * planeSize + rowBase + x] = rowBuf[bmpOff + 2] / 255f; // R
                                inputData[1 * planeSize + rowBase + x] = rowBuf[bmpOff + 1] / 255f; // G
                                inputData[2 * planeSize + rowBase + x] = rowBuf[bmpOff + 0] / 255f; // B
                            }
                        }
                    }
                    finally
                    {
                        bmp24.UnlockBits(bmpData);
                    }

                    // 推論実行
                    var inputTensor = new DenseTensor<float>(inputData, new[] { 1, 3, h, w });
                    var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) };
                    using var results = session.Run(inputs);
                    var output = results.First().AsTensor<float>();

                    // 出力テンソルを画像に変換 (LockBits による高速化)
                    int outH = output.Dimensions[2];
                    int outW = output.Dimensions[3];
                    using var outBmp = new System.Drawing.Bitmap(outW, outH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    var outBmpData = outBmp.LockBits(
                        new System.Drawing.Rectangle(0, 0, outW, outH),
                        System.Drawing.Imaging.ImageLockMode.WriteOnly,
                        System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    try
                    {
                        int outStride = outBmpData.Stride;
                        byte[] outRowBuf = new byte[outStride];
                        int outPlane = outH * outW;
                        for (int y = 0; y < outH; y++)
                        {
                            for (int x = 0; x < outW; x++)
                            {
                                // BGRレイアウトで書き込む
                                int bmpOff = x * 3;
                                outRowBuf[bmpOff + 2] = (byte)Math.Clamp(output[0, 0, y, x] * 255f, 0f, 255f); // R
                                outRowBuf[bmpOff + 1] = (byte)Math.Clamp(output[0, 1, y, x] * 255f, 0f, 255f); // G
                                outRowBuf[bmpOff + 0] = (byte)Math.Clamp(output[0, 2, y, x] * 255f, 0f, 255f); // B
                            }
                            Marshal.Copy(outRowBuf, 0, outBmpData.Scan0 + y * outStride, outStride);
                        }
                    }
                    finally
                    {
                        outBmp.UnlockBits(outBmpData);
                    }
                    outBmp.Save(outputFramePath, System.Drawing.Imaging.ImageFormat.Jpeg);

                    if ((i + 1) % 10 == 0 || i == frames.Length - 1)
                        _logCallback($"ONNX 推論: {i + 1}/{frames.Length} フレーム完了");
                }

                _logCallback("全フレームの ONNX 推論完了。");
            });
        }

        /// <summary>
        /// アップスケール処理のメインパイプラインを実行する。
        /// </summary>
        /// <param name="inputPath">入力動画パス。</param>
        /// <param name="scale">倍率。</param>
        /// <param name="deviceId">実行デバイス ID。</param>
        public async Task ProcessAsync(string inputPath, double scale, int deviceId)
        {
            try
            {
                Provider = _modelManager.GetExecutionProvider(deviceId);
                _logCallback($"実行プロバイダーを設定: {Provider}");

                // 依存ファイルの準備
                await _ffmpegManager.EnsureFfmpegAsync();
                await _modelManager.EnsureModelAsync(deviceId);

                var (srcW, srcH) = await GetVideoResolutionAsync(inputPath);
                int targetW = (int)(srcW * scale);
                int targetH = (int)(srcH * scale);
                
                // 偶数解像度の補正（エンコード互換性のため）
                targetW = (targetW / 2) * 2;
                targetH = (targetH / 2) * 2;

                _logCallback($"ターゲット解像度: {targetW}x{targetH} ({scale}x 倍)");
                
                string outputDir = Path.GetDirectoryName(inputPath) ?? string.Empty;
                string outputName = $"upscaled_{scale}x_{Path.GetFileName(inputPath)}";
                string outputPath = Path.Combine(outputDir, outputName);

                // 各ステップの実行
                await ExtractFramesAsync(inputPath, srcW, srcH);

                string modelPath = System.IO.Path.Combine(Config.ModelsDir, Config.ModelName);
                await UpscaleFramesAsync(modelPath, deviceId);

                await ReassembleVideoAsync(outputPath);
                InjectMetadata(outputPath);

                // 一時ファイルの削除
                if (Directory.Exists(Config.TempDir))
                    Directory.Delete(Config.TempDir, true);
                
                _logCallback($"全ての処理が完了しました: {outputPath}");
            }
            catch (Exception e)
            {
                _logCallback($"処理が異常終了しました: {e.Message}");
            }
        }

        /// <summary>
        /// 動画ファイルに VR180 空間メタデータを注入する。
        /// Spherical Video V2 仕様に基づき st3d / sv3d ボックスを MP4 に直接書き込む。
        /// </summary>
        /// <param name="filePath">対象ファイルパス。</param>
        private void InjectMetadata(string filePath)
        {
            SphericalMetadataInjector.Inject(filePath, _logCallback);
        }
    }
}
