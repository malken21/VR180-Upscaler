using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VR180_Upscaler
{
    /// <summary>
    /// Spherical Video V2 仕様に基づき、MP4 ファイルのビデオトラックに
    /// st3d / sv3d メタデータボックスを直接注入するクラス。
    /// 外部ツールへの依存なし。
    /// </summary>
    public static class SphericalMetadataInjector
    {
        // ボックス名（4バイトFourCC）
        private static readonly byte[] TAG_MOOV = Encoding.ASCII.GetBytes("moov");
        private static readonly byte[] TAG_TRAK = Encoding.ASCII.GetBytes("trak");
        private static readonly byte[] TAG_MDIA = Encoding.ASCII.GetBytes("mdia");
        private static readonly byte[] TAG_MINF = Encoding.ASCII.GetBytes("minf");
        private static readonly byte[] TAG_STBL = Encoding.ASCII.GetBytes("stbl");
        private static readonly byte[] TAG_STSD = Encoding.ASCII.GetBytes("stsd");
        private static readonly byte[] TAG_STCO = Encoding.ASCII.GetBytes("stco");
        private static readonly byte[] TAG_CO64 = Encoding.ASCII.GetBytes("co64");
        private static readonly byte[] TAG_HDLR = Encoding.ASCII.GetBytes("hdlr");
        private static readonly byte[] TAG_VIDE = Encoding.ASCII.GetBytes("vide");

        // ビデオサンプルエントリのFourCC（追加が必要になったら拡張）
        private static readonly HashSet<string> VIDEO_SAMPLE_ENTRIES = new HashSet<string>
        {
            "avc1", "avc2", "avc3", "avc4", "hvc1", "hev1", "mp4v", "vp09", "av01"
        };

        /// <summary>
        /// 指定された MP4 ファイルに VR180 球面メタデータを注入する。
        /// </summary>
        /// <param name="filePath">対象 MP4 ファイルパス。</param>
        /// <param name="log">ログ出力コールバック。</param>
        public static void Inject(string filePath, Action<string> log)
        {
            log("VR180 空間メタデータの注入を開始...");

            byte[] data;
            try
            {
                data = File.ReadAllBytes(filePath);
            }
            catch (Exception ex)
            {
                log($"ファイルの読み込みに失敗: {ex.Message}");
                return;
            }

            // st3d + sv3d の合成バイナリを生成
            byte[] st3dBox = BuildSt3dBox();
            byte[] sv3dBox = BuildSv3dBox();
            byte[] injection = Concat(st3dBox, sv3dBox);

            // moov ボックスを探索してビデオサンプルエントリを検出・書き換え
            long moovPos = FindBox(data, 0, data.Length, TAG_MOOV);
            if (moovPos < 0)
            {
                log("エラー: moov ボックスが見つかりません。");
                return;
            }

            long moovSize = ReadUInt32BE(data, moovPos);
            long videoSampleEntryPos = FindVideoSampleEntry(data, moovPos, moovPos + moovSize, out string? entryType);
            if (videoSampleEntryPos < 0)
            {
                log("エラー: ビデオサンプルエントリが見つかりません。");
                return;
            }

            log($"ビデオサンプルエントリ '{entryType}' を検出 (オフセット: {videoSampleEntryPos})。");

            // 既存のメタデータを確認（二重注入防止）
            long existingSt3d = FindBoxInRange(data, videoSampleEntryPos + 8, videoSampleEntryPos + ReadUInt32BE(data, videoSampleEntryPos), Encoding.ASCII.GetBytes("st3d"));
            if (existingSt3d >= 0)
            {
                log("VR180 メタデータは既に注入済みです。スキップします。");
                return;
            }

            // 注入先のオフセット（ビデオサンプルエントリの末尾）
            long insertOffset = videoSampleEntryPos + ReadUInt32BE(data, videoSampleEntryPos);
            int injectLen = injection.Length;

            // データを挿入
            byte[] newData = new byte[data.Length + injectLen];
            Array.Copy(data, 0, newData, 0, (int)insertOffset);
            Array.Copy(injection, 0, newData, (int)insertOffset, injectLen);
            Array.Copy(data, (int)insertOffset, newData, (int)insertOffset + injectLen, data.Length - (int)insertOffset);

            // 祖先ボックスのサイズを更新（ビデオサンプルエントリ → stsd → stbl → minf → mdia → trak → moov）
            UpdateAncestorSizes(newData, videoSampleEntryPos, injectLen);

            // stco / co64: insertOffset より後のチャンクオフセットを補正
            FixChunkOffsets(newData, moovPos, injectLen, (uint)insertOffset);

            // 上書き保存
            try
            {
                File.WriteAllBytes(filePath, newData);
                log($"VR180 メタデータを注入しました: {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                log($"ファイルの書き込みに失敗: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────
        // ボックス構築
        // ─────────────────────────────────────────────

        /// <summary>st3d FullBox を構築する（stereo_mode = 2: Left-Right）。</summary>
        private static byte[] BuildSt3dBox()
        {
            // size(4) + "st3d"(4) + version(1) + flags(3) + stereo_mode(1) = 13 bytes
            using var ms = new MemoryStream();
            using var bw = new BigEndianBinaryWriter(ms);
            bw.WriteUInt32(13);                        // size
            bw.WriteAscii("st3d");                     // box type
            bw.WriteByte(0);                           // version
            bw.WriteUInt24(0);                         // flags
            bw.WriteByte(2);                           // stereo_mode: Left-Right
            return ms.ToArray();
        }

        /// <summary>sv3d ボックスを構築する（svhd + proj(prhd + equi)）。</summary>
        private static byte[] BuildSv3dBox()
        {
            byte[] svhd = BuildSvhdBox();
            byte[] proj = BuildProjBox();
            byte[] content = Concat(svhd, proj);

            using var ms = new MemoryStream();
            using var bw = new BigEndianBinaryWriter(ms);
            bw.WriteUInt32((uint)(8 + content.Length)); // size
            bw.WriteAscii("sv3d");                      // box type
            bw.WriteBytes(content);
            return ms.ToArray();
        }

        /// <summary>svhd FullBox を構築する。</summary>
        private static byte[] BuildSvhdBox()
        {
            byte[] source = Encoding.UTF8.GetBytes("VR180-Upscaler\0");
            // size(4) + "svhd"(4) + version(1) + flags(3) + source = 12 + source.Length
            using var ms = new MemoryStream();
            using var bw = new BigEndianBinaryWriter(ms);
            bw.WriteUInt32((uint)(12 + source.Length));
            bw.WriteAscii("svhd");
            bw.WriteByte(0);
            bw.WriteUInt24(0);
            bw.WriteBytes(source);
            return ms.ToArray();
        }

        /// <summary>proj ボックスを構築する（prhd + equi）。</summary>
        private static byte[] BuildProjBox()
        {
            byte[] prhd = BuildPrhdBox();
            byte[] equi = BuildEquiBox();
            byte[] content = Concat(prhd, equi);

            using var ms = new MemoryStream();
            using var bw = new BigEndianBinaryWriter(ms);
            bw.WriteUInt32((uint)(8 + content.Length));
            bw.WriteAscii("proj");
            bw.WriteBytes(content);
            return ms.ToArray();
        }

        /// <summary>prhd FullBox を構築する（pose 全て 0）。</summary>
        private static byte[] BuildPrhdBox()
        {
            // size(4) + "prhd"(4) + version(1) + flags(3) + yaw(4) + pitch(4) + roll(4) = 24 bytes
            using var ms = new MemoryStream();
            using var bw = new BigEndianBinaryWriter(ms);
            bw.WriteUInt32(24);
            bw.WriteAscii("prhd");
            bw.WriteByte(0);
            bw.WriteUInt24(0);
            bw.WriteInt32(0);   // pose_yaw_degrees (16.16 fixed point)
            bw.WriteInt32(0);   // pose_pitch_degrees
            bw.WriteInt32(0);   // pose_roll_degrees
            return ms.ToArray();
        }

        /// <summary>
        /// equi FullBox を構築する。
        /// VR180 は左半分のみ有効なので projection_bounds_right = 0x80000000（＝50%）。
        /// </summary>
        private static byte[] BuildEquiBox()
        {
            // size(4) + "equi"(4) + version(1) + flags(3) + top(4) + bottom(4) + left(4) + right(4) = 28 bytes
            using var ms = new MemoryStream();
            using var bw = new BigEndianBinaryWriter(ms);
            bw.WriteUInt32(28);
            bw.WriteAscii("equi");
            bw.WriteByte(0);
            bw.WriteUInt24(0);
            bw.WriteUInt32(0x00000000);  // bounds_top
            bw.WriteUInt32(0x00000000);  // bounds_bottom
            bw.WriteUInt32(0x00000000);  // bounds_left
            bw.WriteUInt32(0x80000000);  // bounds_right = 0.5 (右半分クロップ)
            return ms.ToArray();
        }

        // ─────────────────────────────────────────────
        // MP4 ボックス探索・操作
        // ─────────────────────────────────────────────

        /// <summary>
        /// 指定範囲の先頭レベルで対象タグのボックスを探す。
        /// </summary>
        private static long FindBox(byte[] data, long start, long end, byte[] tag)
        {
            long pos = start;
            while (pos + 8 <= end)
            {
                long size = ReadUInt32BE(data, pos);
                if (size < 8 || pos + size > end) break;

                if (MatchTag(data, pos + 4, tag))
                    return pos;

                pos += size;
            }
            return -1;
        }

        /// <summary>指定範囲内でネストせずに特定タグを探す。</summary>
        private static long FindBoxInRange(byte[] data, long start, long end, byte[] tag)
            => FindBox(data, start, end, tag);

        /// <summary>
        /// moov 配下を再帰的に探索し、ビデオトラックのサンプルエントリ位置を返す。
        /// </summary>
        private static long FindVideoSampleEntry(byte[] data, long containerStart, long containerEnd, out string? foundType)
        {
            foundType = null;
            // trak を全て試す
            long pos = containerStart + 8; // moov ヘッダをスキップ
            while (pos + 8 <= containerEnd)
            {
                long size = ReadUInt32BE(data, pos);
                if (size < 8 || pos + size > containerEnd) break;

                if (MatchTag(data, pos + 4, TAG_TRAK))
                {
                    // hdlr を確認してビデオトラックか判定
                    long trakEnd = pos + size;
                    if (IsVideoTrack(data, pos + 8, trakEnd))
                    {
                        long result = FindStsdEntry(data, pos + 8, trakEnd, out foundType);
                        if (result >= 0) return result;
                    }
                }
                pos += size;
            }
            return -1;
        }

        /// <summary>trak 内の hdlr を確認し、'vide' ハンドラを持つか判定する。</summary>
        private static bool IsVideoTrack(byte[] data, long start, long end)
        {
            long mdiaPos = FindBox(data, start, end, TAG_MDIA);
            if (mdiaPos < 0) return false;
            long mdiaEnd = mdiaPos + ReadUInt32BE(data, mdiaPos);
            long hdlrPos = FindBox(data, mdiaPos + 8, mdiaEnd, TAG_HDLR);
            if (hdlrPos < 0) return false;

            // hdlr: size(4) + "hdlr"(4) + version(1) + flags(3) + pre_defined(4) + handler_type(4)
            long handlerTypeOffset = hdlrPos + 16;
            if (handlerTypeOffset + 4 > end) return false;
            return MatchTag(data, handlerTypeOffset, TAG_VIDE);
        }

        /// <summary>stsd 内の最初のビデオサンプルエントリを探す。</summary>
        private static long FindStsdEntry(byte[] data, long start, long end, out string? foundType)
        {
            foundType = null;
            long mdiaPos = FindBox(data, start, end, TAG_MDIA);
            if (mdiaPos < 0) return -1;
            long mdiaEnd = mdiaPos + ReadUInt32BE(data, mdiaPos);

            long minfPos = FindBox(data, mdiaPos + 8, mdiaEnd, TAG_MINF);
            if (minfPos < 0) return -1;
            long minfEnd = minfPos + ReadUInt32BE(data, minfPos);

            long stblPos = FindBox(data, minfPos + 8, minfEnd, TAG_STBL);
            if (stblPos < 0) return -1;
            long stblEnd = stblPos + ReadUInt32BE(data, stblPos);

            long stsdPos = FindBox(data, stblPos + 8, stblEnd, TAG_STSD);
            if (stsdPos < 0) return -1;
            long stsdEnd = stsdPos + ReadUInt32BE(data, stsdPos);

            // stsd は FullBox: size(4) + "stsd"(4) + version(1) + flags(3) + entry_count(4)
            long entryStart = stsdPos + 16;
            if (entryStart + 8 > stsdEnd) return -1;

            // 最初のエントリを取得
            long entrySize = ReadUInt32BE(data, entryStart);
            if (entrySize < 8 || entryStart + entrySize > stsdEnd) return -1;

            string boxType = Encoding.ASCII.GetString(data, (int)entryStart + 4, 4);
            if (VIDEO_SAMPLE_ENTRIES.Contains(boxType))
            {
                foundType = boxType;
                return entryStart;
            }
            return -1;
        }

        /// <summary>
        /// 注入位置を祖先として持つコンテナボックスのサイズを全て更新する。
        /// </summary>
        private static void UpdateAncestorSizes(byte[] data, long videoSampleEntryPos, int delta)
        {
            // ビデオサンプルエントリ自身のサイズを更新
            PatchUInt32BE(data, videoSampleEntryPos, (uint)(ReadUInt32BE(data, videoSampleEntryPos) + delta));

            // stsd → stbl → minf → mdia → trak → moov を再探索してサイズ更新
            long moovPos = FindBox(data, 0, data.Length, TAG_MOOV);
            if (moovPos < 0) return;

            // 包含関係にある全コンテナを収集して更新
            PatchContainerSizes(data, moovPos, videoSampleEntryPos, delta);
        }

        /// <summary>
        /// containerStart から再帰的に探索し、targetPos を内包するボックスのサイズを更新する。
        /// </summary>
        private static bool PatchContainerSizes(byte[] data, long containerPos, long targetPos, int delta)
        {
            long size = ReadUInt32BE(data, containerPos);
            long end = containerPos + size;

            if (targetPos < containerPos || targetPos >= end) return false;

            // このコンテナは target を内包している → サイズ更新
            PatchUInt32BE(data, containerPos, (uint)(size + delta));

            // 子ボックスを探索して再帰
            long childPos = containerPos + 8;
            // FullBox の場合は version + flags 分スキップ (stsd のみここでは特別扱い不要。親コンテナのみ対象)
            while (childPos + 8 <= end)
            {
                long childSize = ReadUInt32BE(data, childPos);
                if (childSize < 8) break;
                // stsd は FullBox なので children は +16 から始まるが、
                // ここでは子コンテナとして再帰探索するだけなので問題ない
                if (targetPos >= childPos && targetPos < childPos + childSize)
                {
                    PatchContainerSizes(data, childPos, targetPos, delta);
                    return true;
                }
                childPos += childSize;
            }
            return true;
        }

        /// <summary>
        /// stco / co64 ボックスのチャンクオフセットテーブルを補正する。
        /// insertOffset より後のオフセット値に delta を加算する。
        /// </summary>
        private static void FixChunkOffsets(byte[] data, long moovPos, int delta, uint insertOffset)
        {
            long moovEnd = moovPos + ReadUInt32BE(data, moovPos);
            FixChunkOffsetsInContainer(data, moovPos + 8, moovEnd, delta, insertOffset);
        }

        private static void FixChunkOffsetsInContainer(byte[] data, long start, long end, int delta, uint insertOffset)
        {
            long pos = start;
            while (pos + 8 <= end)
            {
                long size = ReadUInt32BE(data, pos);
                if (size < 8 || pos + size > end) break;

                string tag = Encoding.ASCII.GetString(data, (int)pos + 4, 4);

                if (tag == "stco")
                {
                    // stco: size(4)+"stco"(4)+version(1)+flags(3)+entry_count(4) = 16 bytes header
                    uint entryCount = ReadUInt32BE(data, pos + 12);
                    for (uint i = 0; i < entryCount; i++)
                    {
                        long off = pos + 16 + i * 4;
                        uint chunkOffset = ReadUInt32BE(data, off);
                        if (chunkOffset >= insertOffset)
                            PatchUInt32BE(data, off, (uint)(chunkOffset + delta));
                    }
                }
                else if (tag == "co64")
                {
                    uint entryCount = ReadUInt32BE(data, pos + 12);
                    for (uint i = 0; i < entryCount; i++)
                    {
                        long off = pos + 16 + i * 8;
                        ulong chunkOffset = ReadUInt64BE(data, off);
                        if (chunkOffset >= insertOffset)
                            PatchUInt64BE(data, off, chunkOffset + (ulong)delta);
                    }
                }
                else if (IsContainerBox(tag))
                {
                    long innerStart = pos + 8;
                    if (tag == "stsd") innerStart += 8; // stsd は FullBox (version+flags+entry_count)
                    FixChunkOffsetsInContainer(data, innerStart, pos + size, delta, insertOffset);
                }

                pos += size;
            }
        }

        private static readonly HashSet<string> CONTAINER_BOXES = new HashSet<string>
        {
            "moov", "trak", "mdia", "minf", "stbl", "stsd", "dinf", "udta", "meta", "ilst", "edts"
        };

        private static bool IsContainerBox(string tag) => CONTAINER_BOXES.Contains(tag);

        // ─────────────────────────────────────────────
        // バイナリユーティリティ
        // ─────────────────────────────────────────────

        private static bool MatchTag(byte[] data, long offset, byte[] tag)
        {
            if (offset + 4 > data.Length) return false;
            for (int i = 0; i < 4; i++)
                if (data[offset + i] != tag[i]) return false;
            return true;
        }

        private static uint ReadUInt32BE(byte[] data, long offset)
        {
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16)
                 | ((uint)data[offset + 2] << 8) | data[offset + 3];
        }

        private static ulong ReadUInt64BE(byte[] data, long offset)
        {
            ulong hi = ReadUInt32BE(data, offset);
            ulong lo = ReadUInt32BE(data, offset + 4);
            return (hi << 32) | lo;
        }

        private static void PatchUInt32BE(byte[] data, long offset, uint value)
        {
            data[offset]     = (byte)(value >> 24);
            data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)(value);
        }

        private static void PatchUInt64BE(byte[] data, long offset, ulong value)
        {
            PatchUInt32BE(data, offset,     (uint)(value >> 32));
            PatchUInt32BE(data, offset + 4, (uint)(value & 0xFFFFFFFF));
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            byte[] result = new byte[a.Length + b.Length];
            Array.Copy(a, 0, result, 0, a.Length);
            Array.Copy(b, 0, result, a.Length, b.Length);
            return result;
        }
    }

    /// <summary>ビッグエンディアン書き込みヘルパー。</summary>
    internal sealed class BigEndianBinaryWriter : IDisposable
    {
        private readonly Stream _stream;

        public BigEndianBinaryWriter(Stream stream) => _stream = stream;

        public void WriteUInt32(uint value)
        {
            _stream.WriteByte((byte)(value >> 24));
            _stream.WriteByte((byte)(value >> 16));
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        public void WriteUInt24(uint value)
        {
            _stream.WriteByte((byte)(value >> 16));
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        public void WriteInt32(int value) => WriteUInt32((uint)value);

        public void WriteByte(byte value) => _stream.WriteByte(value);

        public void WriteAscii(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            _stream.Write(b, 0, b.Length);
        }

        public void WriteBytes(byte[] bytes) => _stream.Write(bytes, 0, bytes.Length);

        public void Dispose() { }
    }
}
