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
    public string LaunchMode       { get; set; } = "sage";      // sdp | sage | standard
    public string BackgroundImage  { get; set; } = "";          // 空 = 默认占位
    public int    BackgroundZoom   { get; set; } = 100;         // 缩放百分比，100 = 原始
    public float  BackgroundPosX   { get; set; } = 50;          // 水平位置百分比
    public float  BackgroundPosY   { get; set; } = 50;          // 垂直位置百分比
    public bool   NotificationSound { get; set; } = true;         // 生成完毕提示音开关
    public string NotificationSoundPath { get; set; } = "";        // 空 = 使用系统默认提示音
    public List<string> GalleryFolders { get; set; } = new();     // 图库：监听的文件夹路径列表
    public string ThumbnailSize { get; set; } = "medium";         // 图库：缩略图尺寸 small / medium / large
    public string Language { get; set; } = "auto";                // auto | zh-CN | zh-TW | en | ja

    private static readonly string DefaultPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "forgeside.settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(DefaultPath))
                return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(DefaultPath)) ?? new()).Normalized();
        }
        catch { /* 文件损坏则回退默认 */ }
        return new AppSettings();
    }

    /// <summary>
    /// 把 JSON 里显式写成 null 的字段拉回默认值。
    /// 上面那些 = "auto" / = new() 只管「字段根本不在文件里」的情况；
    /// 文件里明写着 "Language": null 时反序列化会照单全收 ——
    /// Nullable enable 只是编译期的，运行时一点防线都没有。
    /// 而后果不是「设置丢了」这么轻：Language 是 null 的话，MainForm 构造函数里
    /// 那句 Lang.SetLanguage 会走到 Dictionary.ContainsKey(null) 直接抛，
    /// 那一步在所有 try 之外，用户不手动改配置文件就永远起不来
    /// </summary>
    private AppSettings Normalized()
    {
        ProxyUrl              ??= "http://127.0.0.1:7897";
        FileManager           ??= "explorer";
        CustomFileManagerPath ??= "";
        LaunchMode            ??= "sage";
        BackgroundImage       ??= "";
        NotificationSoundPath ??= "";
        ThumbnailSize         ??= "medium";
        Language              ??= "auto";

        // 列表里混进 null 的话，前端那个 escAttr 会当场抛，
        // 正好落进 init() 的静默重试里，谁都看不出发生了什么
        GalleryFolders = (GalleryFolders ?? new())
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .ToList();

        return this;
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
