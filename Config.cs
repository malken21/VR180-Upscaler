namespace VR180_Upscaler
{
    /// <summary>
    /// アプリケーション全体の設定値を管理する静的クラス。
    /// </summary>
    public static class Config
    {
        /// <summary>デバッグモードの有効フラグ。</summary>
        public static readonly bool Debug = true;
        
        /// <summary>アプリケーションの実行ディレクトリ。</summary>
        public static readonly string BaseDir = System.AppDomain.CurrentDomain.BaseDirectory;
        /// <summary>AI モデルの格納ディレクトリ。</summary>
        public static readonly string ModelsDir = System.IO.Path.Combine(BaseDir, "models");
        /// <summary>一時フレームの出力先ディレクトリ。</summary>
        public static readonly string TempDir = System.IO.Path.Combine(BaseDir, "temp_frames");
        /// <summary>外部バイナリ（FFmpeg 等）の格納ディレクトリ。</summary>
        public static readonly string BinDir = System.IO.Path.Combine(BaseDir, "bin");
        
        /// <summary>使用する ONNX モデルのファイル名。</summary>
        public const string ModelName = "realesr-animevideov3.onnx";
        /// <summary>モデルファイルのダウンロード URL。</summary>
        public const string ModelUrl = "https://github.com/YoshitakaMo/Real-ESRGAN-ONNX/releases/download/v0.0.1/realesr-animevideov3.onnx";

        /// <summary>FFmpeg 配布元 URL (gyan.dev)。</summary>
        public const string FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        /// <summary>FFmpeg 実行ファイルのローカルパス。</summary>
        public static readonly string FfmpegPath = System.IO.Path.Combine(BinDir, "ffmpeg.exe");
        
        /// <summary>再構築時のデフォルトフレームレート。</summary>
        public const int DefaultFramerate = 30;
        /// <summary>x264 エンコードのプリセット (速度と圧縮率のバランス)。</summary>
        public const string EncodePreset = "slow";
        /// <summary>x264 の品質係数 (CRF: 低いほど高画質)。</summary>
        public const int EncodeCrf = 16;
        /// <summary>出力ピクセルフォーマット。</summary>
        public const string PixelFormat = "yuv420p";
        /// <summary>出力ビデオコーデック。</summary>
        public const string VideoCodec = "libx264";
        
        /// <summary>FFmpeg フレーム抽出時のフィルタ引数文字列。</summary>
        public const string FfmpegExtractVf = "scale={0}:{1}:flags=lanczos";
        /// <summary>FFmpeg フレーム抽出時の画質（1-31, 低いほど高画質）。</summary>
        public const string FfmpegExtractQscale = "2";
    }
}
