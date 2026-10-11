using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HdrScreenshot.Imaging;

namespace HdrScreenshot;

public enum SourceMode { Screen, App, Region }

public sealed class Settings
{
    public SourceMode Mode { get; set; } = SourceMode.Region;
    public int DelaySeconds { get; set; }
    public bool IncludeCursor { get; set; }
    public bool HideWhileCapturing { get; set; } = true;
    public ToneMapMode ToneMap { get; set; } = ToneMapMode.Natural;
    public double Exposure { get; set; }
    public ExportFormat Format { get; set; } = ExportFormat.HdrAndSdr;
    public string SaveFolder { get; set; } = DefaultFolder;
    public bool AutoSave { get; set; }
    public bool CopyToClipboard { get; set; } = true;
    public bool Hotkeys { get; set; } = true;

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "HDR Screenshots");

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HDRScreenshot", "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }
}
