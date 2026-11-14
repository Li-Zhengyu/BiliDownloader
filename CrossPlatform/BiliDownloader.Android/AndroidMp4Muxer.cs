using Android.Media;
using Java.Nio;
using System;
using System.IO;

namespace BiliDownloader.Android;

/// <summary>
/// 使用 Android 系统的 MediaExtractor + MediaMuxer 把 DASH 的 .m4s 音视频合成标准 MP4。
/// 手机上没有 ffmpeg，用系统原生能力即可产出可直接播放的视频，不需要额外依赖。
/// </summary>
public static class AndroidMp4Muxer
{
    /// <summary>把视频流与音频流合成为一个 MP4，成功返回 true</summary>
    public static bool TryMux(string videoPath, string audioPath, string outputPath)
    {
        MediaMuxer? muxer = null;
        MediaExtractor? videoEx = null;
        MediaExtractor? audioEx = null;
        try
        {
            videoEx = new MediaExtractor();
            videoEx.SetDataSource(videoPath);
            audioEx = new MediaExtractor();
            audioEx.SetDataSource(audioPath);

            var vTrack = SelectTrack(videoEx, "video/");
            var aTrack = SelectTrack(audioEx, "audio/");
            if (vTrack < 0 || aTrack < 0)
            {
                BiliClient.Log($"MediaMuxer: 未找到音视频轨道 v={vTrack} a={aTrack}");
                return false;
            }

            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }

            muxer = new MediaMuxer(outputPath, MuxerOutputType.Mpeg4);
            var vOut = muxer.AddTrack(videoEx.GetTrackFormat(vTrack));
            var aOut = muxer.AddTrack(audioEx.GetTrackFormat(aTrack));
            muxer.Start();

            if (!CopyTrack(videoEx, vTrack, muxer, vOut)) return false;
            if (!CopyTrack(audioEx, aTrack, muxer, aOut)) return false;

            muxer.Stop();
            muxer.Release();
            muxer = null;

            var ok = File.Exists(outputPath) && new FileInfo(outputPath).Length > 1024;
            if (!ok) BiliClient.Log("MediaMuxer: 输出文件为空");
            return ok;
        }
        catch (Exception ex)
        {
            BiliClient.Log($"MediaMuxer 合流失败: {ex.Message}");
            return false;
        }
        finally
        {
            try { muxer?.Release(); } catch { }
            try { videoEx?.Release(); } catch { }
            try { audioEx?.Release(); } catch { }
        }
    }

    private static int SelectTrack(MediaExtractor ex, string mimePrefix)
    {
        var count = ex.TrackCount;
        for (var i = 0; i < count; i++)
        {
            var format = ex.GetTrackFormat(i);
            var mime = format.GetString("mime") ?? "";
            if (mime.StartsWith(mimePrefix, StringComparison.Ordinal))
            {
                ex.SelectTrack(i);
                return i;
            }
        }
        return -1;
    }

    private static bool CopyTrack(MediaExtractor ex, int track, MediaMuxer muxer, int outTrack)
    {
        var info = new MediaCodec.BufferInfo();

        // 不使用 SampleSize（仅 API 28+ 可用），按轨道声明的最大样本大小准备缓冲区
        var capacity = 4 * 1024 * 1024;
        try
        {
            var max = ex.GetTrackFormat(track).GetInteger("max-input-size");
            if (max > capacity) capacity = max;
        }
        catch { }
        var buffer = ByteBuffer.Wrap(new byte[capacity]);

        while (true)
        {
            buffer.Clear();
            var read = ex.ReadSampleData(buffer, 0);
            if (read < 0) break;

            info.Offset = 0;
            info.Size = read;
            info.PresentationTimeUs = ex.SampleTime;
            // 关键帧标记（1）与 MediaCodec 的 KeyFrame 取值一致，直接按位转换
            info.Flags = (MediaCodecBufferFlags)ex.SampleFlags;
            muxer.WriteSampleData(outTrack, buffer, info);

            if (!ex.Advance()) break;
        }
        return true;
    }
}
