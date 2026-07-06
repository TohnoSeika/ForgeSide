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
    public void CloseApp() => _form.Invoke(() => Application.Exit());

    // ── 通用消息路由 ──
    public void HandleAction(string action, string arg) => _form.Invoke(() => _form.HandleAction(action, arg));

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
