using Android.Content;
using Android.OS;
using Android.Provider;
using System;
using System.IO;
using System.Threading.Tasks;

namespace BiliDownloader.Android;

/// <summary>
/// 把成品保存到系统「下载/B站视频」，文件管理器里直接可见（Android 10+ 走 MediaStore，无需权限）。
/// </summary>
internal static class PublicStorage
{
    private const string Folder = "B站视频";

    /// <summary>保存文件到公共下载目录，成功返回用户可见的相对路径。</summary>
    public static async Task<string?> SaveAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;

        var name = Path.GetFileName(filePath);
        var context = global::Android.App.Application.Context;
        var resolver = context.ContentResolver;
        if (resolver == null) return null;

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            var collection = MediaStore.Downloads.GetContentUri(MediaStore.VolumeExternalPrimary);
            var values = new ContentValues();
            values.Put("_display_name", name);
            values.Put("mime_type", GuessMime(name));
            values.Put("relative_path", "Download/" + Folder);
            values.Put("is_pending", 1);

            var uri = resolver.Insert(collection, values);
            if (uri == null)
            {
                BiliClient.Log($"保存到公共下载目录失败：无法创建 {name}");
                return null;
            }

            try
            {
                await using (var output = resolver.OpenOutputStream(uri))
                {
                    if (output == null) throw new IOException("无法打开系统下载目录写入流");
                    await using var input = File.OpenRead(filePath);
                    await input.CopyToAsync(output);
                }

                values.Clear();
                values.Put("is_pending", 0);
                resolver.Update(uri, values, null, null);

                BiliClient.Log($"已保存到公共下载目录：下载/{Folder}/{name}");
                return $"下载/{Folder}/{name}";
            }
            catch (Exception ex)
            {
                BiliClient.Log($"保存到公共下载目录失败：{ex.Message}");
                try { resolver.Delete(uri, null, null); } catch { }
                return null;
            }
        }

        // Android 9 及以下：直接写公共下载目录（需要 WRITE_EXTERNAL_STORAGE）
        try
        {
            var dir = Path.Combine(
                global::Android.OS.Environment.GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryDownloads)!
                    .AbsolutePath!, Folder);
            Directory.CreateDirectory(dir);
            File.Copy(filePath, Path.Combine(dir, name), true);
            BiliClient.Log($"已保存到公共下载目录：下载/{Folder}/{name}");
            return $"下载/{Folder}/{name}";
        }
        catch (Exception ex)
        {
            BiliClient.Log($"保存到公共下载目录失败：{ex.Message}");
            return null;
        }
    }

    public static string GuessMime(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".mp4" => "video/mp4",
            ".m4a" => "audio/mp4",
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".aac" => "audio/aac",
            ".wav" => "audio/wav",
            ".opus" => "audio/opus",
            ".srt" => "text/plain",
            ".json" => "application/json",
            _ => "application/octet-stream",
        };
    }
}
