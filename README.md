# VR180-Upscaler

VR180 SBS (Side-by-Side) 動画を AI を用いてアップスケールするためのアプリケーションです。直感的なドラッグ＆ドロップ UI を備えた Windows 向けデスクトップアプリケーションで、ONNX Runtime と DirectML を活用した高速な推論をサポートしています。

## 主要機能

- **柔軟なアップスケール**: 1x, 2x, 4x の倍率指定によるアップスケールに対応。
- **推論デバイスの選択**: CPU または複数台の GPU (DirectML) から使用するデバイスを個別に選択して推論を実行可能。
- **直感的な UI**: 動画ファイルをメインウィンドウにドラッグ＆ドロップするだけで処理を開始。
- **自動モデル管理**: 必要な AI モデルを起動時に自動的にダウンロード。
- **FFmpeg 自動セットアップ**: FFmpeg が未インストールの環境でも、初回起動時に自動的にダウンロード・配置。
- **メタデータ保持**: VR 動画として認識されるよう、空間メタデータの注入を統合。

## システム要件

- **OS**: Windows 10/11
- **Runtime**: .NET 8.0 Desktop Runtime
- **ハードウェア**: DirectX 12 対応の GPU を推奨 (GPU 推論使用時)

## 使用方法

### GUI モード

1. `VR180-Upscaler.exe` を実行。
2. 「Scale Factor」から出力倍率を選択。
3. 「Execution Provider」から使用するデバイスを選択。
4. 対象の VR180 SBS 動画ファイルをウィンドウにドラッグ＆ドロップ。

### CLI モード

コマンドラインから引数を指定して実行することで、GUI を介さずに処理が可能です。

```powershell
.\VR180-Upscaler.exe [input_path] [options]
```

**オプション:**

- `-i, --input <path>`   入力動画ファイルのパス
- `-s, --scale <value>`   アップスケール倍率 (デフォルト: 2.0)
- `-d, --device <id>`    実行デバイス ID (-1: CPU, 0以上: GPU) (デフォルト: -1)
- `-h, --help           ヘルプを表示`

**実行例:**

```powershell
# 基本的な実行 (2x, CPU)
.\VR180-Upscaler.exe "video.mp4"

# 倍率とデバイスを指定
.\VR180-Upscaler.exe -i "video.mp4" -s 1.5 -d 0
```

## 技術仕様

- **GUI**: WPF (.NET 8.0)
- **推論エンジン**: ONNX Runtime (DirectML)
- **動画処理**: FFmpeg (外部プロセス呼び出し)
- **AI モデル**: Real-ESRGAN ベース
