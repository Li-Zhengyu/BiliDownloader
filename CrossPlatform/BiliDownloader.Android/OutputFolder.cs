using Android.Provider;
using BiliDownloader;
using System;
using System.IO;
using System.Threading.Tasks;

namespace BiliDownloader.Android;

/// <summary>
/// 用户通过系统目录选择器选定的保存目录（SAF）。
/// 下载先写入应用私有目录，完成后再复制到所选目录。
/// </summary>
internal static class OutputFolder
{
    private static string SettingsPath =>
        Path.Combine(PlatformInfo.AndroidDataDirOverride ?? Path.GetTempPath(), "output_folder.txt");

    public static string? TreeUri { get; private set; }

    public static string? DisplayName { get; private set; }

    public static bool IsCustom => !string.IsNullOrEmpty(TreeUri);

    public static void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var lines = File.ReadAllLines(SettingsPath);
            if (lines.Length >= 1) TreeUri = lines[0].Trim();
            if (lines.Length >= 2) DisplayName = lines[1].Trim();
        }
        catch
        {
            TreeUri = null;
            DisplayName = null;
        }
    }

    public static void Save(string treeUri)
    {
        TreeUri = treeUri;
        DisplayName = Describe(treeUri);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllLines(SettingsPath, new[] { TreeUri!, DisplayName ?? "" });
        }
        catch
        {
            // 保存失败不影响本次使用
        }
    }

    public static void Reset()
    {
        TreeUri = null;
        DisplayName = null;
        try { File.Delete(SettingsPath); } catch { }
    }

    /// <summary>把 URI 转成可读的目录名，如 "下载/B站视频"。</summary>
    public static string Describe(string treeUri)
    {
        try
        {
            var path = Uri.UnescapeDataString(treeUri);
            var index = path.IndexOf("tree/", StringComparison.OrdinalIgnoreCase);
            var tail = index >= 0 ? path[(index + 5)..] : path;
            var colon = tail.IndexOf(':');
            if (colon >= 0) tail = tail[(colon + 1)..];
            var slash = tail.IndexOf('/');
            if (slash >= 0) tail = tail[..slash];
            tail = tail.Trim();
            return string.IsNullOrEmpty(tail) ? "已选目录" : tail;
        }
        catch
        {
            return "已选目录";
        }
    }

    /// <summary>把成品文件复制到所选目录，成功返回 true。</summary>
    public static async Task<bool> PublishAsync(string filePath)
    {
        if (!IsCustom || string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;

        var context = global::Android.App.Application.Context;
        var resolver = context.ContentResolver;
        var tree = global::Android.Net.Uri.Parse(TreeUri);
        if (tree == null || resolver == null) return false;

        var treeDocId = global::Android.Provider.DocumentsContract.GetTreeDocumentId(tree);
        if (string.IsNullOrEmpty(treeDocId)) return false;

        var dirUri = global::Android.Provider.DocumentsContract.BuildDocumentUriUsingTree(tree, treeDocId!);
        if (dirUri == null) return false;

        // 部分 DocumentsProvider（如系统「下载」根目录）会拒绝含 :*?"<>| 的文件名，这里统一替换
        var name = Sanitize(Path.GetFileName(filePath));
        var mime = PublicStorage.GuessMime(name);

        try
        {
            var docUri = global::Android.Provider.DocumentsContract.CreateDocument(resolver, dirUri, mime, name);
            if (docUri == null)
            {
                BiliClient.Log($"复制到所选目录失败：系统拒绝在该目录创建 {name}");
                return false;
            }

            await using var input = File.OpenRead(filePath);
            await using var output = resolver.OpenOutputStream(docUri);
            if (output == null)
            {
                BiliClient.Log($"复制到所选目录失败：无法写入 {name}");
                return false;
            }

            await input.CopyToAsync(output);
            BiliClient.Log($"已复制到所选目录：{DisplayName}/{name}");
            return true;
        }
        catch (Exception ex)
        {
            BiliClient.Log($"复制到所选目录失败：{ex.Message}");
            return false;
        }
    }

    private static string Sanitize(string name)
    {
        foreach (var c in new[] { ':', '*', '?', '"', '<', '>', '|', '\\', '/' })
            name = name.Replace(c, '_');
        return name;
    }
}
