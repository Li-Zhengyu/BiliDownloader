namespace BiliDownloader;

using System.IO;
using System.Text.Json;

public static class Config
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliDownloader");
    private static readonly string File = Path.Combine(Dir, "config.json");
    private static AppConfig? _current;

    public static AppConfig Current
    {
        get { Load(); return _current!; }
    }

    public static void Load()
    {
        if (_current != null) return;
        try
        {
            if (System.IO.File.Exists(File))
                _current = JsonSerializer.Deserialize<AppConfig>(System.IO.File.ReadAllText(File)) ?? new AppConfig();
            else
                _current = new AppConfig();
        }
        catch { _current = new AppConfig(); }
        Directory.CreateDirectory(_current.OutputDir);
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(_current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
