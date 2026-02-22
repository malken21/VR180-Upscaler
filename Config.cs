namespace VR180_Upscaler
{
    public static class Config
    {
        public static readonly bool Debug = true;
        
        public static readonly string BaseDir = System.AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string ModelsDir = System.IO.Path.Combine(BaseDir, "models");
        public static readonly string TempDir = System.IO.Path.Combine(BaseDir, "temp_frames");
        public static readonly string BinDir = System.IO.Path.Combine(BaseDir, "bin");
        
        public const string ModelName = "realesr-animevideov3.onnx";
        public const string ModelUrl = "https://github.com/YoshitakaMo/Real-ESRGAN-ONNX/releases/download/v0.0.1/realesr-animevideov3.onnx";

        public const string FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        public static readonly string FfmpegPath = System.IO.Path.Combine(BinDir, "ffmpeg.exe");
        
        
        public const int DefaultFramerate = 30;
        public const string EncodePreset = "slow";
        public const int EncodeCrf = 16;
        public const string PixelFormat = "yuv420p";
        public const string VideoCodec = "libx264";
        
        public const string FfmpegExtractVf = "scale={0}:{1}:flags=lanczos";
        public const string FfmpegExtractQscale = "2";
    }
}
