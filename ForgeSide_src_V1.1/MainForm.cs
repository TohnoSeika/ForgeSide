using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using NAudio.Wave;

namespace ForgeSide;

public class MainForm : Form
{
    private WebView2 _webView = null!;
    private readonly ProcessManager _procMgr = new();
    private AppSettings _settings;
    private bool _isRunning;
    private string _lastImageJson = "";
    private long _imageVersion;
    private string _lastBgPath = "";
    private long _bgVersion;
    private string _lastPickedImagePath = "";
    private long _pickedImageVersion;

    public bool IsNeoRunning => _isRunning;

    public MainForm()
    {
        _settings = AppSettings.Load();
        ThemeColors.IsDark = _settings.IsDarkMode;

        Text = "ForgeSide";
        Size = new Size(840, 640);
        MinimumSize = new Size(640, 480);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;

        // 从嵌入资源加载图标（任务栏 + 窗口标题栏）
        try
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("ForgeSide.ico.ico");
            if (stream != null)
                Icon = new Icon(stream);
        }
        catch { /* 静默回退 */ }

        InitializeWebView();

        _procMgr.OnOutput += OnConsoleOutput;
        _procMgr.OnExited += () => BeginInvoke(() =>
        {
            _isRunning = false;
            _webView.CoreWebView2?.ExecuteScriptAsync("forgeSide_onStatus(false)");
        });
        _procMgr.OnImageGenerated += () =>
        {
            if (_settings.NotificationSound)
                PlayNotificationSound();
            BeginInvoke(() => PushConsole("[ForgeSide] 🎨 检测到新图片已生成", "info"));
        };

        Resize += (_, _) =>
        {
            if (_webView != null) _webView.Size = ClientSize;
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            cp.Style |= 0x00020000; // WS_MINIMIZEBOX — 任务栏点击最小化/恢复
            return cp;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_isRunning)
        {
            var result = MessageBox.Show(
                "Forge NEO 进程正在运行中，请先在控制台点击「终止NEO」按钮结束进程！",
                "ForgeSide — 确认关闭",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            e.Cancel = true;
            return;
            if (result == DialogResult.No)
                e.Cancel = true;
        }
        base.OnFormClosing(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11 原生圆角（系统级抗锯齿，远好于 SetWindowRgn）
        int cornerPref = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(Handle, 33, ref cornerPref, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>从嵌入资源生成 base64 PNG 并注入到 HTML 的 logo 元素</summary>
    private void InjectLogoBase64()
    {
        try
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("ForgeSide.ico.ico");
            if (stream == null) return;
            using var icon = new Icon(stream);
            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            string base64 = Convert.ToBase64String(ms.ToArray());
            _webView.CoreWebView2?.ExecuteScriptAsync(
                $"document.querySelector('.logo img')?.setAttribute('src','data:image/png;base64,{base64}')");
        }
        catch { /* 注入失败不影响主流程 */ }
    }

    /// <summary>播放提示音：优先用户自定义，否则用系统提示音</summary>
    private void PlayNotificationSound()
    {
        string customPath = _settings.NotificationSoundPath;
        if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
        {
            try
            {
                using var reader = new AudioFileReader(customPath);
                using var player = new WaveOutEvent();
                player.Init(reader);
                player.Play();
                while (player.PlaybackState == PlaybackState.Playing)
                    Thread.Sleep(50);
                return;
            }
            catch { /* 播放失败则回退系统提示音 */ }
        }
        System.Media.SystemSounds.Exclamation.Play();
    }

    /// <summary>打开音频文件选择器，供 JS 调用</summary>
    public void PickNotificationSound()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "音频文件|*.mp3;*.wav;*.ogg;*.aiff;*.flac;*.m4a|所有文件|*.*",
            Title = "选择提示音文件"
        };
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            _settings.NotificationSoundPath = dlg.FileName;
            _settings.Save();
            _webView.CoreWebView2?.ExecuteScriptAsync(
                $"forgeSide_onSoundPicked('{JsonEncodedText.Encode(dlg.FileName).Value}')");
        }
    }

    private async void InitializeWebView()
    {
        _webView = new WebView2 { Dock = DockStyle.Fill, Size = ClientSize };
        Controls.Add(_webView);

        var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync();
        await _webView.EnsureCoreWebView2Async(env);

        _webView.CoreWebView2.AddHostObjectToScript("forgeSide", new BridgeHost(this));

        // 加载 HTML
        string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "forge_side_ui.html");
        if (File.Exists(htmlPath))
            _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
        else
            _webView.NavigateToString(FallbackHtml);

        // HTML 加载完成后注入 logo（从嵌入资源读取，不留磁盘文件）
        _webView.NavigationCompleted += (_, _) => InjectLogoBase64();
    }

    // ═══ JS → C# 消息路由 ═══
    public void HandleAction(string action, string arg)
    {
        switch (action)
        {
            case "launch":         LaunchNeo(); break;
            case "stop":           StopNeo(); break;
            case "openFolder":     OpenFolderByName(arg); break;
            case "pickImage":      PickImage(); break;
            case "pickBg":         PickBackground(); break;
            case "setTheme":       SetTheme(arg == "true"); break;
            case "saveSetting":    HandleSaveSetting(arg); break;
            case "minimize":       WindowState = FormWindowState.Minimized; break;
            case "close":          Close(); break;
            case "drag":           BeginDrag(arg); break;
            case "openWebui":      OpenWebuiUrl(); break;
            case "pickSound":      PickNotificationSound(); break;
        }
    }

    private void HandleSaveSetting(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string key = r.GetProperty("key").GetString() ?? "";
            string value = r.GetProperty("value").GetString() ?? "";
            SaveSetting(key, value);
        }
        catch { }
    }

    private void BeginDrag(string _)
    {
        ReleaseCapture();
        SendMessage(Handle, 0xA1, 0x2, 0);
    }

    [DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();


    // ═══ 核心功能 ═══
    public void LaunchNeo()
    {
        if (_isRunning) return;
        _isRunning = true;
        string neoRoot = FindNeoRoot();
        _procMgr.Start(neoRoot, _settings.LaunchMode, _settings.ProxyEnabled, _settings.ProxyUrl);
        _procMgr.StartOutputWatcher(neoRoot);
        _webView.CoreWebView2?.ExecuteScriptAsync("forgeSide_onStatus(true)");
    }

    public void StopNeo()
    {
        _procMgr.Stop();
        _isRunning = false;
        _webView.CoreWebView2?.ExecuteScriptAsync("forgeSide_onStatus(false)");
        PushConsole("[ForgeSide] 已发送终止信号", "info");
    }

    private void OpenWebuiUrl()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "http://127.0.0.1:7869",
                UseShellExecute = true,
            });
        }
        catch { PushConsole("[ForgeSide] 无法打开浏览器", "error"); }
    }

    public void SetTheme(bool dark)
    {
        _settings.IsDarkMode = dark;
        _settings.Save();
    }

    public string GetSettingsJson()
    {
        return JsonSerializer.Serialize(new
        {
            isDarkMode = _settings.IsDarkMode,
            proxyEnabled = _settings.ProxyEnabled,
            proxyUrl = _settings.ProxyUrl,
            fileManager = _settings.FileManager,
            customFileManagerPath = _settings.CustomFileManagerPath,
            launchMode = _settings.LaunchMode,
            backgroundImage = _settings.BackgroundImage,
            backgroundZoom = _settings.BackgroundZoom,
            backgroundPosX = _settings.BackgroundPosX,
            backgroundPosY = _settings.BackgroundPosY,
            notificationSound = _settings.NotificationSound,
            notificationSoundPath = _settings.NotificationSoundPath,
        });
    }

    public void SaveSetting(string key, string value)
    {
        switch (key)
        {
            case "proxyEnabled":    _settings.ProxyEnabled = bool.Parse(value); break;
            case "proxyUrl":        _settings.ProxyUrl = value; break;
            case "fileManager":     _settings.FileManager = value; break;
            case "launchMode":      _settings.LaunchMode = value; break;
            case "backgroundImage": _settings.BackgroundImage = value; break;
            case "backgroundZoom":  _settings.BackgroundZoom = int.TryParse(value, out var z) ? z : 100; break;
            case "backgroundPosX":  _settings.BackgroundPosX = float.TryParse(value, out var px) ? px : 50; break;
            case "backgroundPosY":  _settings.BackgroundPosY = float.TryParse(value, out var py) ? py : 50; break;
            case "notificationSound": _settings.NotificationSound = bool.Parse(value); break;
            case "notificationSoundPath": _settings.NotificationSoundPath = value; break;
            case "resetSound": _settings.NotificationSoundPath = ""; _settings.Save(); break;
            case "customFileManagerPath": _settings.CustomFileManagerPath = value; break;
        }
        _settings.Save();
    }

    // ═══ 图片参数（轮询模式：JS 端定时检查，避免依赖 ExecuteScriptAsync）═══
    public long GetImageVersion() => _imageVersion;
    public string GetLastImageJson() => _lastImageJson;
    public long GetBgVersion() => _bgVersion;
    public string GetLastBgPath() => _lastBgPath;
    public long GetPickedImageVersion() => _pickedImageVersion;
    public string GetLastPickedImagePath() => _lastPickedImagePath;

    private void PickImage()
    {
        using var dlg = new OpenFileDialog { Filter = "PNG 图片|*.png|所有文件|*.*" };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        _lastImageJson = ReadImageParams(dlg.FileName);
        Interlocked.Increment(ref _imageVersion);
        _lastPickedImagePath = dlg.FileName;
        Interlocked.Increment(ref _pickedImageVersion);
    }

    private void PickBackground()
    {
        using var dlg = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*" };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        _settings.BackgroundImage = dlg.FileName;
        _settings.BackgroundZoom = 100;
        _settings.BackgroundPosX = 50;
        _settings.BackgroundPosY = 50;
        _settings.Save();
        _lastBgPath = dlg.FileName;
        Interlocked.Increment(ref _bgVersion);
    }

    public string ReadImageParams(string path)
    {
        try
        {
            var p = PngParser.Read(path);
            return SerializeParams(p);
        }
        catch { return "{\"error\":\"读取失败\"}"; }
    }

    /// <summary>从 base64 数据解析 PNG 参数（用于拖放场景）</summary>
    public string ReadImageFromBase64(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes);
            var p = PngParser.Read(ms);
            var json = SerializeParams(p);
            _lastImageJson = json;
            Interlocked.Increment(ref _imageVersion);
            return json;
        }
        catch { return "{\"error\":\"读取失败\"}"; }
    }

    private static string SerializeParams(PngParser.ImageParams? p)
    {
        if (p == null) return "{\"error\":\"无法读取图片参数\"}";
        return JsonSerializer.Serialize(new
        {
            prompt         = p.Prompt,
            negativePrompt = p.NegativePrompt,
            seed           = p.Seed,
            steps          = p.Steps,
            cfgScale       = p.CfgScale,
            sampler        = p.Sampler,
            model          = p.Model,
            width          = p.Width,
            height         = p.Height,
            rawText        = p.RawText,
            isComfyUI      = p.IsComfyUI,
        });
    }

    /// <summary>向上查找包含 webui 子目录的文件夹，作为 NEO 根目录</summary>
    private static string FindNeoRoot()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "webui")))
                return dir;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        return AppDomain.CurrentDomain.BaseDirectory; // 回退
    }

    private void OpenFolderByName(string name)
    {
        string rel = name switch
        {
            "output"     => "webui/output/images",
            "models"     => "webui/models/Stable-diffusion",
            "lora"       => "webui/models/Lora",
            "embeddings" => "webui/embeddings",
            "root"       => ".",
            _ => "webui"
        };
        OpenFolder(rel);
    }

    public void OpenFolder(string relativePath)
    {
        string neoDir = FindNeoRoot();
        string fullPath = Path.GetFullPath(Path.Combine(neoDir, relativePath));
        if (!Directory.Exists(fullPath))
            fullPath = Path.GetFullPath(Path.Combine(neoDir, "webui"));

        string mgr = _settings.FileManager;
        string customPath = _settings.CustomFileManagerPath;
        try
        {
            if (mgr == "xyplorer")
            {
                string exe = !string.IsNullOrEmpty(customPath) && File.Exists(customPath)
                    ? customPath : "xyplorer.exe";
                Process.Start(exe, $"\"{fullPath}\"");
            }
            else if (mgr == "totalcmd")
            {
                string exe = !string.IsNullOrEmpty(customPath) && File.Exists(customPath)
                    ? customPath
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "totalcmd", "TOTALCMD64.EXE");
                Process.Start(exe, $"/O \"{fullPath}\"");
            }
            else if (mgr == "dopus")
            {
                string exe = !string.IsNullOrEmpty(customPath) && File.Exists(customPath)
                    ? customPath : "dopus.exe";
                Process.Start(exe, $"\"{fullPath}\"");
            }
            else
                Process.Start("explorer.exe", fullPath);
        }
        catch { Process.Start("explorer.exe", fullPath); }
    }

    // ═══ 控制台 ═══
    private void OnConsoleOutput(string line)
    {
        string type = "normal";
        if (line.Contains("Error") || line.Contains("Traceback") || line.Contains("CUDA out of memory"))
            type = "error";
        else if (line.Contains("Warning") || line.Contains("warn"))
            type = "warn";
        else if (line.Contains("Running") || line.Contains("Loaded") || line.Contains("Startup") || line.Contains("[ForgeSide]"))
            type = "info";

        PushConsole(line, type);
    }

    private void PushConsole(string line, string type)
    {
        string escaped = JsonEncodedText.Encode(line).Value;
        BeginInvoke(() =>
        {
            _webView.CoreWebView2?.ExecuteScriptAsync(
                $"forgeSide_onConsole('{escaped}','{type}')");
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _procMgr.Dispose();
        base.Dispose(disposing);
    }

    private const string FallbackHtml = @"<!DOCTYPE html><html><body style='background:#1a1816;color:#e0dcd7;font-family:Segoe UI;display:flex;align-items:center;justify-content:center;height:100vh;'>
<div style='text-align:center'><h2>ForgeSide</h2><p>找不到 UI 文件 forge_side_ui.html</p><p style='color:#8a837b'>请将 HTML 文件放在程序同目录下</p></div></body></html>";
}
