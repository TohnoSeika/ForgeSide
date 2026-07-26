using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Linq;
using Microsoft.Web.WebView2.WinForms;
using NAudio.Wave;
using System.Globalization;

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
    private Rectangle _restoreBounds;
    private string _scanGalleryJson = "[]"; // 图库扫描结果缓存
    private ThumbnailCache? _thumbCache;

    public bool IsNeoRunning => _isRunning;

    public MainForm()
    {
        _settings = AppSettings.Load();
        ThemeColors.IsDark = _settings.IsDarkMode;

        // ── 语言初始化 ──
        string lang = _settings.Language;
        if (lang == "auto")
            lang = Lang.DetectSystemLanguage();
        Lang.SetLanguage(lang);

        // 确保输出目录存在（兼容第三方包和官方版）
        string neoRoot = FindNeoRoot();
        string outputDir;
        if (Directory.Exists(Path.Combine(neoRoot, "webui")))
            outputDir = Path.Combine(neoRoot, "webui", "output");
        else
            outputDir = Path.Combine(neoRoot, "outputs");
        Directory.CreateDirectory(Path.Combine(outputDir, "images"));
        Directory.CreateDirectory(Path.Combine(outputDir, "grids"));

        Text = Lang.T("window.title");
        Size = new Size(840, 640);
        MinimumSize = new Size(840, 640);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;
        _restoreBounds = Bounds;

        // 从嵌入资源加载图标（任务栏 + 窗口标题栏）
        try
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("ForgeSide.ico.ico");
            if (stream != null)
                Icon = new Icon(stream);
        }
        catch { /* 静默回退 */ }

        _thumbCache = new ThumbnailCache(AppDomain.CurrentDomain.BaseDirectory);

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

        _lastWindowState = WindowState;
    }

    private FormWindowState _lastWindowState;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 检测 Aero Snap / 双击标题栏等系统触发的窗口状态变化，同步按钮图标
        if (_lastWindowState != WindowState && _webView?.CoreWebView2 != null)
        {
            _lastWindowState = WindowState;
            _webView.CoreWebView2.ExecuteScriptAsync(
                $"forgeSide_onMaximizeChanged({(WindowState == FormWindowState.Maximized ? "true" : "false")})");
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            // WS_MINIMIZEBOX | WS_MAXIMIZEBOX — 允许最小化/最大化
            cp.Style |= 0x00020000 | 0x00010000;
            return cp;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_isRunning)
        {
            using var dlg = new Form
            {
                Text = "ForgeSide — 确认关闭",
                Size = new Size(420, 145),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                ControlBox = false,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                Font = SystemFonts.MessageBoxFont,
                BackColor = SystemColors.Control,
                ForeColor = SystemColors.ControlText,
            };
            dlg.Controls.Add(new Label
            {
                Text = "Forge NEO 进程正在运行中，请先在控制台点击「终止NEO」按钮结束进程！",
                Location = new Point(20, 16),
                Size = new Size(380, 40),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = SystemFonts.MessageBoxFont,
                BackColor = SystemColors.Control,
                ForeColor = SystemColors.ControlText,
            });
            var btnForce = new Button
            {
                Text = "强制停止",
                Location = new Point(70, 70),
                Size = new Size(120, 28),
                FlatStyle = FlatStyle.System,
                DialogResult = DialogResult.Yes,
            };
            var btnCancel = new Button
            {
                Text = "确定",
                Location = new Point(220, 70),
                Size = new Size(120, 28),
                FlatStyle = FlatStyle.System,
                DialogResult = DialogResult.No,
            };
            dlg.Controls.Add(btnForce);
            dlg.Controls.Add(btnCancel);
            if (dlg.ShowDialog(this) == DialogResult.Yes)
            {
                StopNeo();
                e.Cancel = true; // 只停止进程，不退出软件
            }
            else
            {
                e.Cancel = true;
            }
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11 原生圆角（系统级抗锯齿，远好于 SetWindowRgn）
        int cornerPref = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(Handle, 33, ref cornerPref, sizeof(int));
        // 限制最大化范围为工作区（排除任务栏），否则 FormBorderStyle.None 会覆盖全屏
        MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // ═══ 最大化 / 还原 ═══
    public void ToggleMaximize()
    {
        if (WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Normal;
            Bounds = _restoreBounds;
        }
        else
        {
            _restoreBounds = Bounds;
            WindowState = FormWindowState.Maximized;
        }
        _webView.CoreWebView2?.ExecuteScriptAsync(
            $"forgeSide_onMaximizeChanged({(WindowState == FormWindowState.Maximized ? "true" : "false")})");
    }

    /// <summary>从嵌入资源读取默认背景图，返回 base64 Data URI</summary>
    public string GetDefaultBackgroundBase64()
    {
        try
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("ForgeSide.default_bg.jpg");
            if (stream == null) return "";
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch { return ""; }
    }

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
        try
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
        catch (Exception ex)
        {
            MessageBox.Show(
                "未检测到 WebView2 运行时。\n\n" +
                "本程序需要 WebView2 来显示用户界面。\n\n" +
                "Windows 11 用户：系统已自带，请尝试更新系统或重新安装 WebView2。\n\n" +
                "Windows 10 用户：请前往以下地址下载安装：\n" +
                "https://developer.microsoft.com/zh-cn/microsoft-edge/webview2/",
                "ForgeSide — 组件缺失",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Environment.Exit(1);
        }
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
            case "maximize":       ToggleMaximize(); break;
            case "close":          Close(); break;
            case "drag":           BeginDrag(arg); break;
            case "setRect":        HandleSetRect(arg); break;
            case "openWebui":      OpenWebuiUrl(); break;
            case "pickSound":      PickNotificationSound(); break;
            case "addGalleryFolder":    AddGalleryFolder(); break;
            case "removeGalleryFolder":  RemoveGalleryFolder(arg); break;
            case "refreshGalleryFolder": RefreshGalleryFolder(arg); break;
            case "scanGallery":     ScanGalleryJson = ScanGallery(); break; // 仅供 JS 通过 BridgeHost 同步调用
            case "openFile":        OpenFileWithDefaultApp(arg); break;
            case "openUrl":         OpenUrl(arg); break;
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            if (!string.IsNullOrEmpty(url))
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { PushConsole("[ForgeSide] 无法打开链接", "error"); }
    }

    private void HandleSetRect(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            int x = r.GetProperty("x").GetInt32();
            int y = r.GetProperty("y").GetInt32();
            int w = r.GetProperty("w").GetInt32();
            int h = r.GetProperty("h").GetInt32();
            SetWindowRect(x, y, w, h);
        }
        catch { /* 参数解析失败则忽略 */ }
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

    /// <summary>返回窗口当前屏幕坐标与尺寸，供 JS 手动 resize 时计算用</summary>
    public string GetWindowRectJson()
    {
        return JsonSerializer.Serialize(new
        {
            x = Left,
            y = Top,
            w = Width,
            h = Height,
        });
    }

    /// <summary>JS 手动拖拽边缘时调用，使用 SetWindowPos 实时更新窗口——不阻塞消息泵，WebView2 可同步重绘</summary>
    public void SetWindowRect(int x, int y, int width, int height)
    {
        // 确保不低于最小尺寸
        if (width  < MinimumSize.Width)  width  = MinimumSize.Width;
        if (height < MinimumSize.Height) height = MinimumSize.Height;

        const int SWP_NOZORDER = 0x0004;
        SetWindowPos(Handle, IntPtr.Zero, x, y, width, height, SWP_NOZORDER);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();


    // ═══ 核心功能 ═══
    public void LaunchNeo()
    {
        if (_isRunning) return;
        _isRunning = true;
        try
        {
            string neoRoot = FindNeoRoot();
            PushConsole($"[ForgeSide] NEO 根目录: {neoRoot}", "info");
            _procMgr.Start(neoRoot, _settings.LaunchMode, _settings.LaunchMethod, _settings.ProxyEnabled, _settings.ProxyUrl);
            _procMgr.StartOutputWatcher(neoRoot);
            _webView.CoreWebView2?.ExecuteScriptAsync("forgeSide_onStatus(true)");
        }
        catch (Exception ex)
        {
            _isRunning = false;
            PushConsole($"[ForgeSide] ❌ 启动失败: {ex.Message}", "error");
            _webView.CoreWebView2?.ExecuteScriptAsync("forgeSide_onStatus(false)");
        }
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

    /// <summary>获取图库文件夹中所有支持的 AI 图片文件（png/jpg/jpeg/webp）</summary>
    private static string[] GetImageFiles(string folder)
    {
        return new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" }
            .SelectMany(ext => Directory.GetFiles(folder, ext, SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void OpenFileWithDefaultApp(string filePath)
    {
        try
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true,
                });
            }
        }
        catch { /* 静默失败 */ }
    }

    public void SetTheme(bool dark)
    {
        _settings.IsDarkMode = dark;
        _settings.Save();
    }

    public string GetSettingsJson()
    {
        // 如果背景图路径不存在了，自动回退到默认内置背景图
        string bg = _settings.BackgroundImage;
        if (!string.IsNullOrEmpty(bg) && bg != "_default_" && !File.Exists(bg))
        {
            _settings.BackgroundImage = "_default_";
            _settings.BackgroundZoom = 100;
            _settings.BackgroundPosX = 50;
            _settings.BackgroundPosY = 50;
            _settings.Save();
            bg = "_default_";  // ——这是桃华帮 Minagi 修的哦
        }

        return JsonSerializer.Serialize(new
        {
            isDarkMode = _settings.IsDarkMode,
            proxyEnabled = _settings.ProxyEnabled,
            proxyUrl = _settings.ProxyUrl,
            fileManager = _settings.FileManager,
            customFileManagerPath = _settings.CustomFileManagerPath,
            launchMode = _settings.LaunchMode,
            launchMethod = _settings.LaunchMethod,
            backgroundImage = bg,
            backgroundZoom = _settings.BackgroundZoom,
            backgroundPosX = _settings.BackgroundPosX,
            backgroundPosY = _settings.BackgroundPosY,
            notificationSound = _settings.NotificationSound,
            notificationSoundPath = _settings.NotificationSoundPath,
            galleryFolders = ActiveGalleryFolders,
            thumbnailSize = _settings.ThumbnailSize,
            language = ResolveLanguage(),
        });
    }

    /// <summary>解析有效语言代码（auto → 系统检测）</summary>
    private string ResolveLanguage()
    {
        return _settings.Language == "auto" ? Lang.DetectSystemLanguage() : _settings.Language;
    }

    public void SaveSetting(string key, string value)
    {
        switch (key)
        {
            case "proxyEnabled":    _settings.ProxyEnabled = bool.Parse(value); break;
            case "proxyUrl":        _settings.ProxyUrl = value; break;
            case "fileManager":     _settings.FileManager = value; break;
            case "launchMode":      _settings.LaunchMode = value; break;
            case "launchMethod":    _settings.LaunchMethod = value; break;
            case "backgroundImage": _settings.BackgroundImage = value; break;
            case "backgroundZoom":  _settings.BackgroundZoom = int.TryParse(value, out var z) ? z : 100; break;
            case "backgroundPosX":  _settings.BackgroundPosX = float.TryParse(value, out var px) ? px : 50; break;
            case "backgroundPosY":  _settings.BackgroundPosY = float.TryParse(value, out var py) ? py : 50; break;
            case "notificationSound": _settings.NotificationSound = bool.Parse(value); break;
            case "notificationSoundPath": _settings.NotificationSoundPath = value; break;
            case "resetSound": _settings.NotificationSoundPath = ""; _settings.Save(); break;
            case "customFileManagerPath": _settings.CustomFileManagerPath = value; break;
            case "thumbnailSize": _settings.ThumbnailSize = value; break;
            case "addGalleryFolder":
                var added = value;
                if (!string.IsNullOrEmpty(added) && !_settings.GalleryFolders.Contains(added))
                { _settings.GalleryFolders.Add(added); _settings.Save(); }
                break;
            case "removeGalleryFolder":
                _settings.GalleryFolders.Remove(value);
                _settings.Save();
                break;
            case "language":
                _settings.Language = value;
                _settings.Save();
                Lang.SetLanguage(value == "auto" ? Lang.DetectSystemLanguage() : value);
                break;
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
        using var dlg = new OpenFileDialog { Filter = "AI 图片|*.png;*.jpg;*.jpeg;*.webp|所有文件|*.*" };
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

    /// <summary>向上查找 Forge NEO 根目录（最多往上找 2 级）</summary>
    private static string FindNeoRoot()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int level = 0; level <= 2; level++)
        {
            if (string.IsNullOrEmpty(dir)) break;
            // 第三方包：有 webui/ 子文件夹
            if (Directory.Exists(Path.Combine(dir, "webui")))
                return dir;
            // 官方版：webui.bat 在根目录
            if (File.Exists(Path.Combine(dir, "webui.bat")))
                return dir;
            // licyk 版：有 core/ 或 hanamizuki.bat
            if (Directory.Exists(Path.Combine(dir, "core")))
                return dir;
            if (File.Exists(Path.Combine(dir, "hanamizuki.bat")))
                return dir;
            // 有 launch.py 也算
            if (File.Exists(Path.Combine(dir, "launch.py")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName!;
        }
        return AppDomain.CurrentDomain.BaseDirectory; // 回退
    }

    private void OpenFolderByName(string name)
    {
        // 检测版本结构
        string neoRoot = FindNeoRoot();
        bool isThirdParty = Directory.Exists(Path.Combine(neoRoot, "webui"));
        string rel = name switch
        {
            "output" => isThirdParty ? "webui/output/images" : "outputs",
            "models" => isThirdParty ? "webui/models/Stable-diffusion" : "models/Stable-diffusion",
            "lora" => isThirdParty ? "webui/models/Lora" : "models/Lora",
            "embeddings" => isThirdParty ? "webui/embeddings" : "embeddings",
            "root" => ".",
            _ => isThirdParty ? "webui" : "."
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
            else if (mgr == "other")
            {
                string exe = !string.IsNullOrEmpty(customPath) && File.Exists(customPath)
                    ? customPath : "";
                if (!string.IsNullOrEmpty(exe))
                    Process.Start(exe, $"\"{fullPath}\"");
                else
                    Process.Start("explorer.exe", fullPath);
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
        if (disposing)
        {
            _procMgr.Dispose();
            _thumbCache?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ═══ 图库 ═══

    /// <summary>每次启动动态计算默认图库路径 + 用户保存的文件夹列表，不持久化默认路径</summary>
    private List<string> ActiveGalleryFolders
    {
        get
        {
            string neoRoot = FindNeoRoot();
            string defaultFolder;
            if (Directory.Exists(Path.Combine(neoRoot, "webui")))
                defaultFolder = Path.GetFullPath(Path.Combine(neoRoot, "webui", "output", "images"));
            else
                defaultFolder = Path.GetFullPath(Path.Combine(neoRoot, "outputs"));
            // 只保留当前 Neo 根目录下的用户添加的文件夹（换版本后自动失效）
            var folders = _settings.GalleryFolders
                .Where(f => Directory.Exists(f) && f.StartsWith(neoRoot, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (!folders.Contains(defaultFolder, StringComparer.OrdinalIgnoreCase))
                folders.Insert(0, defaultFolder);
            return folders;
        }
    }

    public string ScanGalleryJson
    {
        get => _scanGalleryJson;
        private set => _scanGalleryJson = value;
    }

    /// <summary>从 SQLite 缓存加载图库（瞬间，不碰原文件）</summary>
    public string ScanGallery()
    {
        _scanGalleryJson = _thumbCache?.LoadCachedGallery(ActiveGalleryFolders) ?? "{\"images\":[]}";
        return _scanGalleryJson;
    }

    /// <summary>完整扫描图库文件夹（写缓存 + 生成三档缩略图），从设置页调用</summary>
    public void RefreshGallery()
    {
        _thumbCache?.ScanFolders(ActiveGalleryFolders);
        var allPaths = new List<string>();
        foreach (var folder in ActiveGalleryFolders)
        {
            if (!Directory.Exists(folder)) continue;
            allPaths.AddRange(GetImageFiles(folder));
        }
        if (allPaths.Count > 0)
            PreGenerateThumbnails(JsonSerializer.Serialize(allPaths));
    }

    /// <summary>刷新指定文件夹（清除缓存 + 重新扫描 + 生成缩略图）</summary>
    public void RefreshGalleryFolder(string folder)
    {
        _thumbCache?.InvalidateFolder(folder);
        _thumbCache?.ScanFolders(new List<string> { folder });
        if (Directory.Exists(folder))
        {
            var paths = GetImageFiles(folder);
            if (paths.Length > 0)
                PreGenerateThumbnails(JsonSerializer.Serialize(paths));
        }
    }

    /// <summary>搜索图库（按 prompt 文本），返回匹配图片 + 元数据 JSON</summary>
    public string SearchGallery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ScanGallery(); // 空查询返回全部

        var list = new List<object>();
        foreach (var folder in ActiveGalleryFolders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in GetImageFiles(folder))
            {
                try
                {
                    var p = PngParser.Read(file);
                    if (p == null) continue;
                    string text = (p.Prompt + " " + p.NegativePrompt).ToLowerInvariant();
                    // 按逗号拆分搜索词，每个词独立匹配（顺序无关）
                    var terms = query.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (terms.Length == 0) continue;
                    bool allMatch = true;
                    foreach (var term in terms)
                    {
                        if (!text.Contains(term)) { allMatch = false; break; }
                    }
                    if (!allMatch) continue;

                    var fi = new FileInfo(file);
                    list.Add(new
                    {
                        path = file,
                        name = fi.Name,
                        folder = Path.GetDirectoryName(file) ?? "",
                        modified = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                        width = p.Width,
                        height = p.Height,
                        prompt = p.Prompt,
                        negativePrompt = p.NegativePrompt,
                        seed = p.Seed,
                        steps = p.Steps,
                        cfgScale = p.CfgScale,
                        sampler = p.Sampler,
                        model = p.Model,
                    });
                }
                catch { /* 跳过 */ }
            }
        }
        list.Sort((a, b) =>
        {
            dynamic da = a, db = b;
            return string.CompareOrdinal(db.modified, da.modified);
        });
        return JsonSerializer.Serialize(new { images = list });
    }

    /// <summary>获取单张图片的 PNG 参数元数据 JSON</summary>
    public string GetGalleryMeta(string path)
    {
        var p = PngParser.Read(path);
        return SerializeParams(p);
    }

    /// <summary>获取缩略图 Base64 Data URI（sizeKey: small/medium/large）</summary>
    public string GetThumbBase64(string imagePath, string sizeKey)
    {
        if (_thumbCache == null) return "";
        return _thumbCache.GetBase64(imagePath, sizeKey);
    }

    /// <summary>收集系统信息 JSON</summary>
    public string GetSystemInfoJson()
    {
        var info = new Dictionary<string, object>();
        try
        {
            // OS — WMI Caption 准确区分 Win10/Win11
            try
            {
                using var searcher2 = new System.Management.ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem");
                using var results2 = searcher2.Get();
                foreach (var obj2 in results2)
                {
                    string c = obj2["Caption"]?.ToString() ?? "";
                    info["os"] = c.Replace("Microsoft ", "").Trim();
                    break;
                }
            }
            catch { info["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription; }

            // CPU
            using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
            using (var results = searcher.Get())
            {
                foreach (var obj in results)
                {
                    info["cpu"] = $"{obj["Name"]}（{obj["NumberOfCores"]} 核 {obj["NumberOfLogicalProcessors"]} 线程）";
                    break;
                }
            }

            // GPU — 优先 nvidia-smi 获取准确显存，回退 WMI
            var gpus = new List<string>();
            bool hasNvidia = false;
            try
            {
                var psi2 = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=name,memory.total --format=csv,noheader",
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                using var p = Process.Start(psi2);
                if (p != null)
                {
                    string o = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(5000);
                    if (!string.IsNullOrEmpty(o) && p.ExitCode == 0)
                    {
                        hasNvidia = true;
                        foreach (var line in o.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var parts = line.Split(',');
                            if (parts.Length >= 2)
                            {
                                string vramStr = parts[1].Trim();
                                // nvidia-smi 返回类似 "12288 MiB"，提取数字转 GB
                                var numMatch = System.Text.RegularExpressions.Regex.Match(vramStr, @"\d+");
                                if (numMatch.Success && long.TryParse(numMatch.Value, out long mb))
                                    vramStr = $"{(mb / 1024.0):F1} GB";
                                gpus.Add($"{parts[0].Trim()}（{vramStr}）");
                            }
                        }
                    }
                }
            }
            catch { }
            if (!hasNvidia)
            {
                using (var gpuSe = new System.Management.ManagementObjectSearcher("SELECT Name, AdapterRAM, PNPDeviceID FROM Win32_VideoController"))
                using (var gpuRe = gpuSe.Get())
                {
                    foreach (var gpuObj in gpuRe)
                    {
                        string pnpId = gpuObj["PNPDeviceID"]?.ToString() ?? "";
                        if (pnpId.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                        if (pnpId.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
                        long vram = 0;
                        try { vram = Convert.ToInt64(gpuObj["AdapterRAM"]); } catch { }
                        gpus.Add($"{gpuObj["Name"]}（{(vram > 0 ? $"{(vram / 1073741824.0):F0} GB" : "未知")}）");
                    }
                }
            }
            if (gpus.Count == 0) gpus.Add("未检测到独立显卡");
            info["gpu"] = string.Join("\n", gpus);

            // 内存 — 每根型号 + DDR 版本 + 容量，多根多行
            var memDdrMap = new Dictionary<int, string>
            {
                [20] = "DDR", [21] = "DDR2", [22] = "DDR2", [24] = "DDR3",
                [26] = "DDR4", [34] = "DDR5"
            };
            var memList = new List<string>();
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Manufacturer, PartNumber, Capacity, SMBIOSMemoryType, MemoryType, ConfiguredClockSpeed FROM Win32_PhysicalMemory");
                using var results = searcher.Get();
                foreach (var obj in results)
                {
                    string mfr = (obj["Manufacturer"]?.ToString() ?? "").Trim();
                    string pn = (obj["PartNumber"]?.ToString() ?? "").Trim();
                    long cap = 0;
                    try { cap = Convert.ToInt64(obj["Capacity"]); } catch { }
                    // 优先 SMBIOSMemoryType，其次 MemoryType，最后用频率推断
                    int type = 0;
                    try { type = Convert.ToInt32(obj["SMBIOSMemoryType"]); } catch { }
                    if (type == 0) try { type = Convert.ToInt32(obj["MemoryType"]); } catch { }
                    string ddr = memDdrMap.TryGetValue(type, out var t) ? t : "";
                    if (ddr.Length == 0)
                    {
                        int speed = 0;
                        try { speed = Convert.ToInt32(obj["ConfiguredClockSpeed"]); } catch { }
                        if (speed >= 4800) ddr = "DDR5";
                        else if (speed >= 2133) ddr = "DDR4";
                        else if (speed >= 1066) ddr = "DDR3";
                    }
                    string label = string.Join(" ", new[] { mfr, pn }.Where(s => !string.IsNullOrEmpty(s)));
                    if (string.IsNullOrEmpty(label)) label = "未知内存条";
                    memList.Add($"{label}  {ddr}  {(cap > 0 ? $"{(cap / 1073741824.0):F0} GB" : "")}".Trim());
                }
            }
            catch { }
            if (memList.Count == 0)
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
                using var results = searcher.Get();
                foreach (var obj in results)
                {
                    long total = Convert.ToInt64(obj["TotalVisibleMemorySize"]) * 1024;
                    memList.Add($"{(total / 1073741824.0):F0} GB");
                    break;
                }
            }
            info["ram"] = string.Join("\n", memList);

            // 磁盘（型号 + 盘符 + 容量，每个一行）
            var diskList = new List<string>();
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT DeviceID, Model, Size FROM Win32_DiskDrive");
                using var results = searcher.Get();
                foreach (var obj in results)
                {
                    string model = (obj["Model"]?.ToString() ?? "未知磁盘").Trim();
                    long size = 0;
                    try { size = Convert.ToInt64(obj["Size"]); } catch { }
                    string sizeStr = size > 0 ? $"{(size / 1073741824.0):F0} GB" : "";
                    // 尝试匹配盘符
                    string drives = "";
                    try
                    {
                        using var partSearcher = new System.Management.ManagementObjectSearcher(
                            $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{obj["DeviceID"]}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                        using var partResults = partSearcher.Get();
                        foreach (var part in partResults)
                        {
                            using var logSearcher = new System.Management.ManagementObjectSearcher(
                                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{part["DeviceID"]}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
                            using var logResults = logSearcher.Get();
                            foreach (var log in logResults)
                                drives += log["DeviceID"]?.ToString() + " ";
                        }
                    }
                    catch { }
                    string driveStr = drives.Length > 0 ? drives.Trim() : "";
                    diskList.Add($"{driveStr}  {model}  {sizeStr}".Trim());
                }
            }
            catch { }
            if (diskList.Count == 0)
            {
                foreach (var drive in DriveInfo.GetDrives())
                    if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                        diskList.Add($"{drive.Name} {(drive.TotalSize / 1073741824.0):F0} GB");
            }
            diskList.Sort((a, b) => string.CompareOrdinal(a, b)); // 按盘符字母排序
            info["disk"] = string.Join("\n", diskList);

            // 屏幕
            var screen = Screen.FromHandle(Handle);
            info["screen"] = $"{screen.Bounds.Width} × {screen.Bounds.Height}";

            // Python
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "python", Arguments = "--version",
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd().Trim() + proc.StandardError.ReadToEnd().Trim();
                    proc.WaitForExit(3000);
                    info["python"] = string.IsNullOrEmpty(output) ? "未检测到" : output;
                }
            }
            catch { info["python"] = "未检测到"; }

            // Forge 版本
            try
            {
                string neoRoot = FindNeoRoot();
                string ver = null;
                // 判断结构：官方版（文件在 neoRoot 下）vs 第三方整合包（文件在 neoRoot/webui/ 下）
                string webuiDir = Directory.Exists(Path.Combine(neoRoot, "webui"))
                    ? Path.Combine(neoRoot, "webui") : neoRoot;
                // 尝试 version.txt
                string vf = Path.Combine(webuiDir, "version.txt");
                if (File.Exists(vf)) { ver = File.ReadAllText(vf).Trim(); }
                // 尝试从 modules_forge/forge_version.py 读取版本号
                if (string.IsNullOrEmpty(ver))
                {
                    string forgeVerPy = Path.Combine(webuiDir, "modules_forge", "forge_version.py");
                    if (File.Exists(forgeVerPy))
                    {
                        try
                        {
                            string content = File.ReadAllText(forgeVerPy);
                            var relMatch = System.Text.RegularExpressions.Regex.Match(content, @"^release\s*=\s*""([^""]+)""", System.Text.RegularExpressions.RegexOptions.Multiline);
                            var verMatch = System.Text.RegularExpressions.Regex.Match(content, @"^version\s*=\s*""([^""]+)""", System.Text.RegularExpressions.RegexOptions.Multiline);
                            string rel = relMatch.Success ? relMatch.Groups[1].Value : "";
                            string verPart = verMatch.Success ? verMatch.Groups[1].Value : "";
                            if (!string.IsNullOrEmpty(rel))
                                ver = string.IsNullOrEmpty(verPart) ? rel : $"{verPart}-{rel}";
                        }
                        catch { }
                    }
                }
                // 尝试 git describe
                if (string.IsNullOrEmpty(ver))
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "git", Arguments = "describe --tags --always",
                            WorkingDirectory = neoRoot,
                            RedirectStandardOutput = true, RedirectStandardError = true,
                            UseShellExecute = false, CreateNoWindow = true,
                        };
                        using var proc = Process.Start(psi);
                        if (proc != null) { ver = proc.StandardOutput.ReadToEnd().Trim(); proc.WaitForExit(3000); }
                    }
                    catch { }
                }
                // 尝试 git log
                if (string.IsNullOrEmpty(ver))
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "git", Arguments = "log -1 --format=%cd --date=short",
                            WorkingDirectory = neoRoot,
                            RedirectStandardOutput = true, RedirectStandardError = true,
                            UseShellExecute = false, CreateNoWindow = true,
                        };
                        using var proc = Process.Start(psi);
                        if (proc != null) { ver = "commit " + proc.StandardOutput.ReadToEnd().Trim(); proc.WaitForExit(3000); }
                    }
                    catch { }
                }
                info["forge"] = string.IsNullOrEmpty(ver) ? $"路径: {neoRoot}" : ver;
            }
            catch { info["forge"] = "未检测到"; }
        }
        catch (Exception ex) { info["error"] = ex.Message; }

        return JsonSerializer.Serialize(info);
    }
    public void CleanThumbOrphans()
    {
        _thumbCache?.CleanOrphans();
    }

    /// <summary>启动原生文件拖放（JS 无法直接拖出本地文件，需要 C# 通过 DoDragDrop 实现）</summary>
    public void StartFileDrag(string filePath)
    {
        if (!File.Exists(filePath)) return;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { filePath });
            DoDragDrop(data, DragDropEffects.Copy);
        }
        catch { /* 静默 */ }
    }

    /// <summary>升级/切换到指定 Git tag 版本。返回 JSON：{ success: bool, error?: string }</summary>
    public string UpgradeToVersion(string tagName)
    {
        try
        {
            string neoRoot = FindNeoRoot();
            // 官方版：git 仓库就在 neoRoot 本身（有 launch.py / webui.bat）
            // 第三方整合包：git 仓库在 neoRoot/webui/ 下
            string webuiDir;
            if (Directory.Exists(Path.Combine(neoRoot, ".git")))
                webuiDir = neoRoot;
            else if (Directory.Exists(Path.Combine(neoRoot, "webui", ".git")))
                webuiDir = Path.Combine(neoRoot, "webui");
            else
                return JsonSerializer.Serialize(new { success = false, error = "未找到 Forge NEO 的 Git 仓库，无法使用此方式升级" });

            PushConsole($"[ForgeSide] 开始升级到 {tagName} …", "info");
            PushConsole("[ForgeSide] 正在连接 GitHub，获取最新版本列表…", "");

            // 1. 获取最新 tags
            var fetchResult = RunGit(webuiDir, "fetch --tags");
            if (fetchResult.output.Length > 0)
                PushConsole($"[Git] {fetchResult.output.Trim()}", "");
            if (fetchResult.error.Length > 0)
                PushConsole($"[Git] {fetchResult.error.Trim()}", "");
            if (fetchResult.exitCode != 0)
            {
                PushConsole("[ForgeSide] ❌ 获取远程标签失败", "error");
                return JsonSerializer.Serialize(new { success = false, error = $"获取远程标签失败：{fetchResult.error}" });
            }
            PushConsole("[ForgeSide] ✅ 版本列表获取完成", "info");

            // 2. 切换到指定 tag
            PushConsole($"[ForgeSide] 正在切换到 {tagName} …", "");
            var checkoutResult = RunGit(webuiDir, $"reset --hard tags/{tagName}");
            if (checkoutResult.output.Length > 0)
                PushConsole($"[Git] {checkoutResult.output.Trim()}", "");
            if (checkoutResult.error.Length > 0 && checkoutResult.exitCode != 0)
                PushConsole($"[Git] {checkoutResult.error.Trim()}", "");
            if (checkoutResult.exitCode != 0)
            {
                PushConsole("[ForgeSide] ❌ 切换版本失败", "error");
                return JsonSerializer.Serialize(new { success = false, error = $"切换版本失败：{checkoutResult.error}" });
            }
            PushConsole($"[ForgeSide] ✅ 已切换到 {tagName}，升级完成！请重启 Forge NEO 使新版本生效。", "info");

            return JsonSerializer.Serialize(new { success = true });
        }
        catch (Exception ex)
        {
            PushConsole($"[ForgeSide] ❌ 升级出错：{ex.Message}", "error");
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    private static (int exitCode, string output, string error) RunGit(string workingDir, string args)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"-C \"{workingDir}\" {args}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            string error = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);
            return (proc.ExitCode, output, error);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    /// <summary>测试代理连通性。target: "github" 或 "google"。返回 JSON：{ success: bool, url: string, error?: string }</summary>
    public string TestProxyConnection(string target)
    {
        try
        {
            string url = target.ToLowerInvariant() switch
            {
                "google" => "https://www.google.com",
                "huggingface" => "https://huggingface.co",
                _ => throw new ArgumentException($"未知目标: {target}")
            };

            var settings = AppSettings.Load();
            string proxyUrl = settings.ProxyUrl;

            HttpClientHandler handler;
            if (settings.ProxyEnabled && !string.IsNullOrWhiteSpace(proxyUrl))
            {
                handler = new HttpClientHandler
                {
                    Proxy = new System.Net.WebProxy(proxyUrl),
                    UseProxy = true,
                };
            }
            else
            {
                handler = new HttpClientHandler
                {
                    UseProxy = false,
                };
            }
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ForgeSide/1.4");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = client.GetAsync(url).Result;
            sw.Stop();

            if (response.IsSuccessStatusCode)
                return JsonSerializer.Serialize(new { success = true, url, ms = sw.ElapsedMilliseconds });
            else
                return JsonSerializer.Serialize(new { success = false, url, error = $"返回状态码 {(int)response.StatusCode}" });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    /// <summary>后台批量预生成三档缩略图</summary>
    public void PreGenerateThumbnails(string jsonPaths)
    {
        string[]? paths;
        try { paths = JsonSerializer.Deserialize<string[]>(jsonPaths); }
        catch { return; }
        if (paths == null || paths.Length == 0) return;

        Task.Run(() =>
        {
            _thumbCache?.PreGenerateAll(paths);
            BeginInvoke(() =>
            {
                _webView.CoreWebView2?.ExecuteScriptAsync("forgeSide_onThumbnailsReady()");
            });
        });
    }

    /// <summary>添加图库文件夹</summary>
    public void AddGalleryFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "选择要监听的图片文件夹" };
        if (dlg.ShowDialog() == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
        {
            if (!_settings.GalleryFolders.Contains(dlg.SelectedPath))
            {
                _settings.GalleryFolders.Add(dlg.SelectedPath);
                _settings.Save();
                // 通知 JS 刷新
                _webView.CoreWebView2?.ExecuteScriptAsync("forgeSide_onGalleryFoldersChanged()");
            }
        }
    }

    /// <summary>移除图库文件夹</summary>
    public void RemoveGalleryFolder(string path)
    {
        _settings.GalleryFolders.Remove(path);
        _settings.Save();
        _webView.CoreWebView2?.ExecuteScriptAsync("forgeSide_onGalleryFoldersChanged()");
    }

    private const string FallbackHtml = @"<!DOCTYPE html><html><body style='background:#1a1816;color:#e0dcd7;font-family:Segoe UI;display:flex;align-items:center;justify-content:center;height:100vh;'>
<div style='text-align:center'><h2>ForgeSide</h2><p>找不到 UI 文件 forge_side_ui.html</p><p style='color:#8a837b'>请将 HTML 文件放在程序同目录下</p></div></body></html>";
}
