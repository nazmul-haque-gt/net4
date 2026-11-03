using System.Text.Json;
namespace NetSpeedTray;

public class Settings
{
    public int UploadColorArgb { get; set; } = Color.FromArgb(255, 170, 60).ToArgb();   // orange
    public int DownloadColorArgb { get; set; } = Color.FromArgb(80, 220, 120).ToArgb(); // green
    public int UploadArrowColorArgb { get; set; } = Color.FromArgb(255, 170, 60).ToArgb();
    public int DownloadArrowColorArgb { get; set; } = Color.FromArgb(80, 220, 120).ToArgb();
    public int TotalColorArgb { get; set; } = Color.FromArgb(120, 190, 255).ToArgb();
    public int TotalArrowColorArgb { get; set; } = Color.FromArgb(120, 190, 255).ToArgb();
    public bool TotalOnly { get; set; } = false;       // one icon showing total (up+down)
    public bool SeparateIcons { get; set; } = false;   // two tray icons: upload and download, bigger text
    public int FontPx { get; set; } = 0;         // 0 = auto-fit (largest that fits)
    public string Unit { get; set; } = "Auto";   // Auto, K, M
    public bool Bits { get; set; } = false;      // false = KB/s MB/s, true = kbps Mbps
    public bool UploadOnTop { get; set; } = true;

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetSpeedTray");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save()
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
        catch { }
    }
}
