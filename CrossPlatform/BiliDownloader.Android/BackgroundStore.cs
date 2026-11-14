using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BiliDownloader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace BiliDownloader.Android;

/// <summary>
/// 亚克力背景：默认播放内置的预模糊帧序列（12fps，零运行时模糊开销），
/// 用户也可用系统图片选择器替换为自定义静态背景。
/// </summary>
internal static class BackgroundStore
{
    private const int FrameCount = 48;
    private const string FrameUriFormat = "avares://BiliDownloader.Android/Assets/bg/frame_{0:000}.jpg";

    public static string UserPath =>
        Path.Combine(PlatformInfo.AndroidDataDirOverride ?? Path.GetTempPath(), "background_user.bin");

    public static bool HasUserBackground => File.Exists(UserPath);

    public static Bitmap? LoadUserBackground()
    {
        try
        {
            if (!HasUserBackground) return null;
            using var stream = File.OpenRead(UserPath);
            return Bitmap.DecodeToWidth(stream, 1080);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>后台线程加载全部内置帧（约 24MB 解码后内存）。</summary>
    public static Task<List<Bitmap>> LoadFramesAsync() => Task.Run(() =>
    {
        var frames = new List<Bitmap>(FrameCount);
        for (var i = 0; i < FrameCount; i++)
        {
            var uri = new Uri(string.Format(FrameUriFormat, i));
            using var stream = AssetLoader.Open(uri);
            frames.Add(new Bitmap(stream));
        }
        return frames;
    });

    public static async Task<bool> SaveUserBackgroundAsync(string uri)
    {
        try
        {
            var context = global::Android.App.Application.Context;
            var parsed = global::Android.Net.Uri.Parse(uri);
            if (parsed == null) return false;

            using var input = context.ContentResolver?.OpenInputStream(parsed);
            if (input == null) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(UserPath)!);
            await using var output = File.Create(UserPath);
            await input.CopyToAsync(output);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Reset()
    {
        try
        {
            if (File.Exists(UserPath)) File.Delete(UserPath);
        }
        catch
        {
            // 忽略
        }
    }
}
