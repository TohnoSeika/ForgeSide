using System.Text.Json;

namespace ForgeSide;

/// <summary>ForgeSide 配置模型 —— JSON 序列化持久化</summary>
public class AppSettings
{
    public bool   IsDarkMode       { get; set; } = false;
    public bool   ProxyEnabled     { get; set; } = false;
    public string ProxyUrl         { get; set; } = "http://127.0.0.1:7897";
    public string FileManager      { get; set; } = "explorer";  // explorer | xyplorer | totalcmd | dopus | custom
    public string CustomFileManagerPath { get; set; } = "";
    public string LaunchMode       { get; set; } = "sdp";       // sdp | sage | standard
    public string LaunchMethod     { get; set; } = "auto";      // auto | direct | bat
    public string BackgroundImage  { get; set; } = "";          // 空 = 默认占位
    public int    BackgroundZoom   { get; set; } = 100;         // 缩放百分比，100 = 原始
    public float  BackgroundPosX   { get; set; } = 50;          // 水平位置百分比
    public float  BackgroundPosY   { get; set; } = 50;          // 垂直位置百分比
    public bool   NotificationSound { get; set; } = true;         // 生成完毕提示音开关
    public string NotificationSoundPath { get; set; } = "";        // 空 = 使用系统默认提示音
    public List<string> GalleryFolders { get; set; } = new();     // 图库：监听的文件夹路径列表
    public string ThumbnailSize { get; set; } = "medium";         // 图库：缩略图尺寸 small / medium / large

    private static readonly string DefaultPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "forgeside.settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(DefaultPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(DefaultPath)) ?? new();
        }
        catch { /* 文件损坏则回退默认 */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(DefaultPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 静默失败 */ }
    }
}
