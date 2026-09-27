using System.Runtime.InteropServices;

namespace ForgeSide;

/// <summary>JS ↔ C# 桥接对象，暴露给 WebView2 的 JS 调用</summary>
[ClassInterface(ClassInterfaceType.AutoDual)]
[ComVisible(true)]
public class BridgeHost
{
    private readonly MainForm _form;

    public BridgeHost(MainForm form) => _form = form;

    // ── 进程控制 ──
    public void LaunchNeo() => _form.Invoke(() => _form.LaunchNeo());
    public void StopNeo()   => _form.Invoke(() => _form.StopNeo());
    public bool IsRunning() => _form.IsNeoRunning;

    // ── 设置 ──
    public string GetSettingsJson() => _form.GetSettingsJson();
    public void SaveSetting(string key, string value) => _form.Invoke(() => _form.SaveSetting(key, value));

    // ── 文件夹 ──
    public void OpenFolder(string name) => _form.Invoke(() => OpenFolderByName(name));

    // ── 图片参数 ──
    public long GetImageVersion() => _form.GetImageVersion();
    public string GetLastImageJson() => _form.GetLastImageJson();
    public long GetBgVersion() => _form.GetBgVersion();
    public string GetLastBgPath() => _form.GetLastBgPath();
    public long GetPickedImageVersion() => _form.GetPickedImageVersion();
    public string GetLastPickedImagePath() => _form.GetLastPickedImagePath();
    public string ReadImageParams(string path) => _form.ReadImageParams(path);
    public string ReadImageFromBase64(string base64) => _form.ReadImageFromBase64(base64);

    // ── 主题 ──
    public void SetTheme(bool dark) => _form.Invoke(() => _form.SetTheme(dark));

    // ── 窗口 ──
    public void Minimize() => _form.Invoke(() => _form.WindowState = FormWindowState.Minimized);
    public void Maximize() => _form.Invoke(() => _form.ToggleMaximize());
    public void CloseApp() => _form.Invoke(() => Application.Exit());
    public string GetWindowRect() => _form.GetWindowRectJson();
    public void SetWindowRect(int x, int y, int w, int h) => _form.Invoke(() => _form.SetWindowRect(x, y, w, h));

    // ── 通用消息路由 ──
    public void HandleAction(string action, string arg) => _form.Invoke(() => _form.HandleAction(action, arg));

    // ── 长任务 ──
    // 刻意不套 _form.Invoke：桥接调用本来就落在 UI 线程上，而它必须在 UI 线程上
    // 立刻返回，把活丢给 C# 自己安排到后台。结果通过 ExecuteScriptAsync 回调送回去。
    public void RequestLongTask(string requestId, string taskName, string arg)
        => _form.RequestLongTask(requestId, taskName, arg);

    // ── 图库 ──
    public void StartFileDrag(string path) => _form.StartFileDrag(path);
    public string ScanGallery() => _form.ScanGallery();
    public string SearchGallery(string query) => _form.SearchGallery(query);
    public string GetGalleryMeta(string path) => _form.GetGalleryMeta(path);
    public string GetThumbBase64(string imagePath, string sizeKey) => _form.GetThumbBase64(imagePath, sizeKey);
    public void CleanThumbOrphans() => _form.Invoke(() => _form.CleanThumbOrphans());
    public void PreGenerateThumbnails(string jsonPaths) => _form.Invoke(() => _form.PreGenerateThumbnails(jsonPaths));
    public void RefreshGalleryFolder(string folder) => _form.Invoke(() => _form.RefreshGalleryFolder(folder));
    public void RefreshGallery() => _form.Invoke(() => _form.RefreshGallery());
    public string GetDefaultBg() => _form.GetDefaultBackgroundBase64();

    // ── 选择文件 ──
    public string PickFile(string filter) => _form.Invoke(() =>
    {
        using var dlg = new OpenFileDialog { Filter = filter };
        return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : "";
    });

    private void OpenFolderByName(string name)
    {
        string rel = name switch
        {
            "output" => "webui/output",
            "models" => "webui/models/Stable-diffusion",
            "lora"   => "webui/models/Lora",
            "embeddings" => "webui/embeddings",
            _ => "webui"
        };
        _form.OpenFolder(rel);
    }
}
