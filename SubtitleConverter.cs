namespace BiliDownloader;

using System.Text;
using System.Text.Json;

public static class SubtitleConverter
{
    public static string JsonToSrt(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var body = doc.RootElement.GetProperty("body");
        var sb = new StringBuilder();
        int i = 1;
        foreach (var item in body.EnumerateArray())
        {
            double from = item.GetProperty("from").GetDouble();
            double to = item.GetProperty("to").GetDouble();
            string text = item.GetProperty("content").GetString() ?? "";
            sb.AppendLine(i.ToString());
            sb.AppendLine($"{Fmt(from)} --> {Fmt(to)}");
            sb.AppendLine(text);
            sb.AppendLine();
            i++;
        }
        return sb.ToString();
    }

    private static string Fmt(double s)
    {
        var t = TimeSpan.FromSeconds(s);
        return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2},{t.Milliseconds:D3}";
    }
}
