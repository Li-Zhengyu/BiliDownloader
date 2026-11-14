using Avalonia;
using Avalonia.Styling;
using BiliDownloader;
using System;
using System.IO;

namespace BiliDownloader.Android;

/// <summary>主题偏好：跟随系统 / 浅色 / 深色，持久化到应用数据目录。</summary>
internal static class ThemeStore
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    private static string FilePath =>
        Path.Combine(PlatformInfo.AndroidDataDirOverride ?? Path.GetTempPath(), "theme.txt");

    public static string Mode { get; private set; } = System;

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var value = File.ReadAllText(FilePath).Trim();
            if (value is Light or Dark or System) Mode = value;
        }
        catch
        {
            // 忽略
        }
    }

    public static void Save(string mode)
    {
        Mode = mode;
        try { File.WriteAllText(FilePath, mode); } catch { }
        Apply();
    }

    public static void Apply()
    {
        if (Avalonia.Application.Current is not { } app) return;

        app.RequestedThemeVariant = Mode switch
        {
            Light => ThemeVariant.Light,
            Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
