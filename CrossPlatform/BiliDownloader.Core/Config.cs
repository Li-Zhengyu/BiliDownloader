namespace BiliDownloader;

using System.IO;
using System.Text.Json;

public static class Config
{
    private static readonly string Dir = PlatformInfo.LocalDataDir;
    private static readonly string FilePath = Path.Combine(Dir, "config.json");
    private static AppConfig? _current;
    private static readonly object _lock = new();

    public static AppConfig Current
    {
        get { Load(); return _current!; }
    }

    public static void Load()
    {
        if (_current != null) return;
        lock (_lock)
        {
            if (_current != null) return;
            try
            {
                if (System.IO.File.Exists(FilePath))
                    _current = JsonSerializer.Deserialize<AppConfig>(System.IO.File.ReadAllText(FilePath)) ?? new AppConfig();
                else
                    _current = new AppConfig();
            }
            catch { _current = new AppConfig(); }
            try { Directory.CreateDirectory(_current.OutputDir); } catch { }
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            System.IO.File.WriteAllText(FilePath, JsonSerializer.Serialize(_current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
