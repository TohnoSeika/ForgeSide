using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Linq;
using Microsoft.Web.WebView2.WinForms;
using NAudio.Wave;
using System.Globalization;

namespace ForgeSide;

public class MainForm : Form
{
    /// <summary>
    /// NEO 的三态。以前只有「在跑 / 没在跑」两个布尔，
    /// 于是「正在终止」被算进了「没在跑」——而升级守卫、关窗确认、启动按钮
    /// 全都照这个判断行事，结果进程还在关闭中的那一小段时间里，
    /// 界面会理直气壮地做错事（最狠的一条是放行升级，去 reset 一个 python 还在读的仓库）。
    /// </summary>
    private enum NeoState { Idle, Running, Stopping }

    private WebView2 _webView = null!;
    private readonly ProcessManager _procMgr = new();
    private AppSettings _settings;
    private NeoState _neoState = NeoState.Idle;
    private string _lastImageJson = "";
    private long _imageVersion;
    private string _lastBgPath = "";
    private long _bgVersion;
    private string _lastPickedImagePath = "";
    private long _pickedImageVersion;
    private Rectangle _restoreBounds;
    private string _scanGalleryJson = "[]"; // 图库扫描结果缓存
    private ThumbnailCache? _thumbCache;

    // ── 控制台批量通道 ──
    // 以前每来一行就发一次 ExecuteScriptAsync。NEO 加载模型时几秒几千行，
    // C#→JS 这条通道自己就成了瓶颈，所以改成攒一批送一次。
    private readonly List<(string Text, string Type)> _pendingConsoleLines = new();
    private readonly object _consoleLock = new();
    private System.Threading.Timer? _consoleFlushTimer;
    private bool _consoleFlushArmed;   // 定时器已经排上了，别再往后推

    private const int ConsoleBatchMax = 100;
    private static readonly TimeSpan ConsoleFlushDelay = TimeSpan.FromMilliseconds(120);

    // ── 终止看门狗 ──
    // 「正在终止」不能永远停在那儿。Kill 发出去之后 Exited 一直不来的话，
    // 界面就卡在一个既点不动也不真实的状态里——那比说错话还糟：说错话还能点，卡住是真的没路走。
    // 到点就老实承认没停成，而不是一直装作正在停。
    private const int StopTimeoutSeconds = 10;
    private System.Threading.Timer? _stopWatchdog;

    // 「正在终止」也算还在——它确实还在。升级守卫和关窗确认要问的就是这个
    public bool IsNeoRunning => _neoState != NeoState.Idle;

    private bool CanLaunchNeo => _neoState == NeoState.Idle;

    // ── 在飞的长任务 ──
    // 版本升级这类活跑在后台线程上，窗口一关就是把它丢在半途：
    // git 那边可能停在 detached HEAD 或者半个 checkout 上，比 NEO 跑着关掉还难收拾。
    // 所以关窗也要拦一道，和 NEO 一样。跨线程读写，用 Interlocked
    private int _longTasksInFlight;

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
        // 建不出目录（只读位置 / 无权限）也要让用户看到原因，
        // 而不是双击图标之后什么都没有发生
        try
        {
            Directory.CreateDirectory(Path.Combine(outputDir, "images"));
            Directory.CreateDirectory(Path.Combine(outputDir, "grids"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Lang.T("startup.output_dir_fail", outputDir, ex.Message),
                Lang.T("startup.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

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

        // 缓存初始化失败（数据库损坏 / 被占用 / 无权限）不该拖垮整个程序，
        // 图库退化成没有缩略图，其余功能照常——代码里本来就用的是 _thumbCache?
        try
        {
            _thumbCache = new ThumbnailCache(AppDomain.CurrentDomain.BaseDirectory);
        }
        catch (Exception ex)
        {
            _thumbCache = null;
            MessageBox.Show(
                Lang.T("startup.thumbcache_fail", ex.Message,
                       Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".thumbcache")),
                Lang.T("startup.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        InitializeWebView();

        _procMgr.OnOutput += OnConsoleOutput;
        // 进程真的走了，才轮到「没在跑」——这是唯一一处把状态推回 Idle 的地方，
        // 所以「终止中」会一直挂着，直到 Exited 真的来
        _procMgr.OnExited += () => SafeInvoke(() =>
        {
            DisarmStopWatchdog();
            EnterNeoState(NeoState.Idle);
        });
        _procMgr.OnImageGenerated += () =>
        {
            if (_settings.NotificationSound)
                PlayNotificationSound();
            SafeInvoke(() => PushConsole(Lang.T("console.img_generated"), "info"));
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
        // 「终止中」也要拦——那会儿 NEO 还在，只是正在被收掉
        if (IsNeoRunning)
        {
            if (ShowCloseDialog(Lang.T("close_dialog.msg"), Lang.T("close_dialog.force")))
                StopNeo();
            e.Cancel = true; // 两种情况都不退软件：要么去停 NEO，要么留下
            return;
        }

        // 长任务还在飞的时候也不放行。它跑在后台线程上，窗口一关就等于把它丢在半途，
        // 而这边没有"停下来"的按钮可点——只能问一句，让 Minagi 自己决定
        if (Volatile.Read(ref _longTasksInFlight) > 0)
        {
            if (!ShowCloseDialog(Lang.T("close_dialog.busy_msg"), Lang.T("close_dialog.busy_force")))
                e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }

    /// <summary>
    /// 关窗确认框。返回 true = 用户按了左边那个"就这么办"的按钮。
    /// NEO 在跑和长任务在飞共用这一个框，只是文案不同——
    /// 两处各抄一份 Form 出来，改一次就得记得改两遍
    /// </summary>
    private bool ShowCloseDialog(string message, string proceedText)
    {
        using var dlg = new Form
        {
            Text = Lang.T("close_dialog.title"),
            Size = new Size(460, 168),
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
            Text = message,
            Location = new Point(24, 16),
            Size = new Size(412, 60),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = SystemFonts.MessageBoxFont,
            BackColor = SystemColors.Control,
            ForeColor = SystemColors.ControlText,
        });

        dlg.Controls.Add(new Button
        {
            Text = proceedText,
            Location = new Point(88, 90),
            Size = new Size(130, 30),
            FlatStyle = FlatStyle.System,
            DialogResult = DialogResult.Yes,
        });

        dlg.Controls.Add(new Button
        {
            Text = Lang.T("close_dialog.ok"),
            Location = new Point(242, 90),
            Size = new Size(130, 30),
            FlatStyle = FlatStyle.System,
            DialogResult = DialogResult.No,
        });

        return dlg.ShowDialog(this) == DialogResult.Yes;
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
            Filter = Lang.T("filter.audio"),
            Title = Lang.T("pick_sound.title")
        };
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            _settings.NotificationSoundPath = dlg.FileName;
            _settings.Save();
            // 同样不能手拼单引号——文件名里带一个 ' 就会把这句脚本撑破，
            // 而 ExecuteScriptAsync 出事是静默的，用户只会觉得"选了没反应"
            _webView.CoreWebView2?.ExecuteScriptAsync(
                $"forgeSide_onSoundPicked({JsonSerializer.Serialize(dlg.FileName)})");
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
            // 兜底那句话是照着「WebView2 没装」写的，可这个 catch 罩着的范围比那宽——
            // CreateAsync、EnsureCoreWebView2Async、注入宿主对象、导航，哪一个炸都落到这里。
            // 所以把原始报错接在后面：装是装了但还是起不来的话，好歹有个线索，
            // 不然那句"未检测到运行时"就是在把人往错的方向指
            MessageBox.Show(
                Lang.T("webview2.msg") + "\n\n" + ex.Message,
                Lang.T("webview2.title"),
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
        catch { PushConsole(Lang.T("console.open_url_err"), "error"); }
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


    // ═══ 状态 ═══
    /// <summary>
    /// 改状态和告诉界面永远一起发生，不会漏掉一边。
    /// 以前这两件事散在四五个地方各写各的，于是 C# 的 _isRunning 和界面各说各话——
    /// 「点停止后启动被吞掉」就是从那儿来的。
    /// </summary>
    private void EnterNeoState(NeoState state)
    {
        _neoState = state;
        string payload = JsonSerializer.Serialize(DescribeNeoState(state));
        _webView.CoreWebView2?.ExecuteScriptAsync($"forgeSide_onStatus({payload})");
    }

    /// <summary>界面认这三个词，所以名字得写死，不能跟着枚举名跑</summary>
    private static string DescribeNeoState(NeoState state) => state switch
    {
        NeoState.Running  => "running",
        NeoState.Stopping => "stopping",
        _                 => "idle",
    };

    /// <summary>
    /// 等 Exited 的上限。以前没有这个：Kill 失败的话界面会永远停在「正在终止」，
    /// 那比说错话更糟——说错话还能点，卡住是真的没路走。
    /// </summary>
    private void ArmStopWatchdog()
    {
        _stopWatchdog ??= new System.Threading.Timer(
            _ => SafeInvoke(OnStopTimedOut), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        SafeChange(_stopWatchdog, TimeSpan.FromSeconds(StopTimeoutSeconds));
    }

    private void DisarmStopWatchdog() => SafeChange(_stopWatchdog, Timeout.InfiniteTimeSpan);

    private void OnStopTimedOut()
    {
        if (_neoState != NeoState.Stopping) return;   // 已经收摊了，看门狗只是来得晚

        PushConsole(Lang.T("console.stop_timeout", StopTimeoutSeconds), "error");
        EnterNeoState(NeoState.Running);   // 它确实还在，别装作不在
    }

    // ═══ 核心功能 ═══
    public void LaunchNeo()
    {
        // 能走到这里的沉默只有一种情况：启动中连点两下。第一下已经清过控制台、
        // 推过状态了，第二下本来就该没事发生，所以这里不出声是对的
        if (!CanLaunchNeo) return;

        EnterNeoState(NeoState.Running);
        ClearConsole();   // 新一轮开始了，上一轮的输出就到此为止
        try
        {
            string neoRoot = FindNeoRoot();
            PushConsole(Lang.T("console.neo_root", neoRoot), "info");
            _procMgr.Start(neoRoot, _settings.LaunchMode, _settings.ProxyEnabled, _settings.ProxyUrl);
            _procMgr.StartOutputWatcher(neoRoot);
        }
        catch (Exception ex)
        {
            // Start 可能已经成功、只有后面的监视器失败了——
            // 这种时候必须把进程收掉，否则界面说"没启动"而 NEO 还在后台跑
            if (_procMgr.Stop() == ProcessManager.StopOutcome.KillFailed)
            {
                // 杀不掉就不能假装它没了。留在「运行中」，终止按钮才点得动，
                // 关窗和版本升级那边也才拦得住——退回 Idle 的话 NEO 就成了
                // 只能在任务管理器里收的孤儿，界面上连个抓手都没有
                PushConsole(Lang.T("console.launch_fail", ex.Message), "error");
                PushConsole(Lang.T("console.launch_fail_orphan"), "error");
                return;
            }

            EnterNeoState(NeoState.Idle);
            PushConsole(Lang.T("console.launch_fail", ex.Message), "error");
        }
    }

    public void StopNeo()
    {
        // 没在跑、或者已经在终止了——两条都不该再发一次 Kill。
        // 「终止中」这一条尤其要紧：重复点终止不能把它推回起点
        if (_neoState != NeoState.Running) return;

        var outcome = _procMgr.Stop();

        if (outcome == ProcessManager.StopOutcome.KillFailed)
        {
            // Kill 自己抛了，进程大概还在。不装作停了——说清楚，让 Minagi 再点一次
            PushConsole(Lang.T("console.stop_failed"), "error");
            return;   // 状态不动，还是「运行中」——它确实还在
        }

        if (outcome == ProcessManager.StopOutcome.NothingToStop)
        {
            // 进程其实已经没了，只是退出事件还在路上。直接收摊，
            // 别为了一个马上要来的事件把自己挂在「正在终止」
            EnterNeoState(NeoState.Idle);
            return;
        }

        // 这里不写控制台。「正在终止」是给当下看的一句话，
        // 落进日志就永远没有下文——过后读只知道"那会儿在终止"，不知道停成没停成。
        // 界面上的状态文字和那颗按钮已经在说这件事了，控制台留给结果：
        // 成功是 HandleProcessExited 那行「已退出（收到终止请求）」，失败是上面那两句 ❌
        EnterNeoState(NeoState.Stopping);
        ArmStopWatchdog();
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
        catch { PushConsole(Lang.T("console.open_browser_err"), "error"); }
    }

    /// <summary>
    /// 获取图库文件夹中所有支持的 AI 图片文件（png/jpg/jpeg/webp）。
    /// 跳过不可访问的子目录——否则把 D:\ 这类根目录加进图库就会整片扫描崩掉。
    /// </summary>
    private static string[] GetImageFiles(string folder)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
            };

            return new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" }
                .SelectMany(ext => Directory.GetFiles(folder, ext, options))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
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
        using var dlg = new OpenFileDialog { Filter = Lang.T("filter.ai_image") };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        _lastImageJson = ReadImageParams(dlg.FileName);
        Interlocked.Increment(ref _imageVersion);
        _lastPickedImagePath = dlg.FileName;
        Interlocked.Increment(ref _pickedImageVersion);
    }

    private void PickBackground()
    {
        using var dlg = new OpenFileDialog { Filter = Lang.T("filter.image") };
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
        // 用序列化器而不是手拼字符串：译文里万一带个引号，手拼出来的就不是 JSON 了
        catch { return JsonSerializer.Serialize(new { error = Lang.T("read.error") }); }
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
        catch { return JsonSerializer.Serialize(new { error = Lang.T("read.error") }); }
    }

    private static string SerializeParams(PngParser.ImageParams? p)
    {
        if (p == null) return JsonSerializer.Serialize(new { error = Lang.T("param.read_error") });
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

    /// <summary>
    /// 向上查找 Forge NEO 根目录（最多往上找 2 级）。
    /// 返回值一律去掉末尾的分隔符：BaseDirectory 带着一个尾巴，
    /// 而它会被拼进引号里交给子进程（见 RunGit），带尾巴会出事。
    /// </summary>
    private static string FindNeoRoot()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int level = 0; level <= 2; level++)
        {
            if (string.IsNullOrEmpty(dir)) break;
            if (LooksLikeNeoRoot(dir))
                return Path.TrimEndingDirectorySeparator(dir);
            dir = Directory.GetParent(dir)?.FullName!;
        }
        return Path.TrimEndingDirectorySeparator(AppDomain.CurrentDomain.BaseDirectory); // 回退
    }

    /// <summary>这个目录像不像 Forge NEO 的根目录——各家整合包的结构不一样，认到一种就算</summary>
    private static bool LooksLikeNeoRoot(string dir)
    {
        // 第三方包：有 webui/ 子文件夹
        if (Directory.Exists(Path.Combine(dir, "webui")))
            return true;
        // 官方版：webui.bat 在根目录
        if (File.Exists(Path.Combine(dir, "webui.bat")))
            return true;
        // licyk 版：有 core/ 或 hanamizuki.bat
        if (Directory.Exists(Path.Combine(dir, "core")))
            return true;
        if (File.Exists(Path.Combine(dir, "hanamizuki.bat")))
            return true;
        // 有 launch.py 也算
        return File.Exists(Path.Combine(dir, "launch.py"));
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
        lock (_consoleLock)
        {
            _pendingConsoleLines.Add((line, type));

            // 攒够一批就立刻走，不等定时器——NEO 吐得快的时候，
            // 光靠 120ms 一批会让延迟悄悄积起来
            if (_pendingConsoleLines.Count >= ConsoleBatchMax)
                FlushConsoleLocked();
            else
                ArmConsoleFlushLocked();
        }
    }

    /// <summary>定时器到点了，把攒着的行送出去</summary>
    private void FlushConsole()
    {
        lock (_consoleLock) FlushConsoleLocked();
    }

    /// <summary>
    /// 取走存货、序列化、发给 JS。**必须持着锁调用**——
    /// 发出去这个动作也得在锁里，不然两批并发时顺序会颠倒。
    /// 控制台的时间顺序一乱，查起来很费劲。
    /// </summary>
    private void FlushConsoleLocked()
    {
        // 先把定时器收掉再判断有没有货。反过来写的话，
        // 空手而归那一次会把"已排队"的旗子留在那儿，之后谁也再摇不动它了
        DisarmConsoleFlushLocked();

        if (_pendingConsoleLines.Count == 0) return;

        var batch = _pendingConsoleLines.ToArray();
        _pendingConsoleLines.Clear();

        // 参数交给 JsonSerializer 转义，不再手拼单引号——
        // 控制台里出现过带英文单引号的路径，手拼的引号会被它直接撑破。
        // （整个数组序列化出来本身就是合法的 JS 字面量）
        string payload = JsonSerializer.Serialize(
            batch.Select(item => new { text = item.Text, type = item.Type }));
        SafeInvoke(() => _webView.CoreWebView2?.ExecuteScriptAsync($"forgeSide_onConsole({payload})"));
    }

    /// <summary>
    /// 清空控制台。用户在「这一轮启动」和「上一轮」之间划的那条线就画在这里：
    /// 点启动的时候清，点停止的时候不清——刚跑完那一轮的日志还得留着给人回头看。
    /// 清存货和发清空指令在同一把锁里，所以上一轮的行不会漏进来。
    /// </summary>
    private void ClearConsole()
    {
        lock (_consoleLock)
        {
            _pendingConsoleLines.Clear();
            DisarmConsoleFlushLocked();
            SafeInvoke(() => _webView.CoreWebView2?.ExecuteScriptAsync("forgeSide_onConsoleClear()"));
        }
    }

    /// <summary>
    /// 排一次刷新。**已经排上就不再往后推**——这一点是这组函数存在的全部理由。
    /// 以前每来一行都调一次 timer.Change，那是"每次有人说话就把闹钟往后拨 120ms"：
    /// NEO 加载模型时每 100ms 吐一行、又攒不满 100 行的话，闹钟永远等不到响，
    /// 控制台能几分钟一动不动。120ms 该是"最迟这么久必须发一次"，
    /// 不是"安静 120ms 才发"。窗口销毁后 Change 会抛 ObjectDisposedException，那说明
    /// 程序正在退出，剩下几行丢掉也无所谓
    /// </summary>
    private void ArmConsoleFlushLocked()
    {
        if (_consoleFlushArmed) return;
        _consoleFlushArmed = true;

        _consoleFlushTimer ??= new System.Threading.Timer(
            _ => FlushConsole(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        SafeChange(_consoleFlushTimer, ConsoleFlushDelay);
    }

    /// <summary>把排好的那一次取消掉。收掉旗子才叫真取消，不然就再也排不上了</summary>
    private void DisarmConsoleFlushLocked()
    {
        _consoleFlushArmed = false;
        SafeChange(_consoleFlushTimer, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// 改定时器的到期时间。退出时定时器可能已经被 Dispose 了，
    /// 那种时候 Change 会抛 ObjectDisposedException——那正是我们想要的"别再响了"。
    /// 控制台通道和终止看门狗都要这一下，所以只有一份。
    /// </summary>
    private static void SafeChange(System.Threading.Timer? timer, TimeSpan dueTime)
    {
        try { timer?.Change(dueTime, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* 正在退出 */ }
    }

    /// <summary>
    /// 安全地把回调送回 UI 线程。
    /// 窗口已销毁时直接丢弃——这些回调跑在没有 Task 包裹的裸后台线程上，
    /// 异常没人接，会直接把整个进程带下去。
    /// </summary>
    private void SafeInvoke(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;

        try { BeginInvoke(action); }
        catch (InvalidOperationException) { /* 句柄恰好在判断和调用之间销毁了 */ }
    }


    // ═══════════════════════════════════════
    //  长任务通道
    // ═══════════════════════════════════════

    /// <summary>
    /// 长任务入口——立刻返回，活干完了再由 JS 那边取结果。
    ///
    /// 为什么不能直接同步返回：桥接调用是在 UI 线程上执行的，在它里面跑
    /// git fetch、WMI 查询这类活儿，窗口就会从点下去到命令结束一动不动。
    /// 升级尤其难受——fetch + status + reset 三条命令排下来，界面能僵一两分钟，
    /// 中间 PushConsole 打的日志也全堆在队列里，等命令回来才一起涌出来。
    ///
    /// 为什么不用 WebView2 的异步代理：那个「C# 方法返回 Task」的支持是
    /// 2024 年才进 SDK 的，桃华在这儿没法验证运行时认不认；而
    /// ExecuteScriptAsync 回调这条通道这个项目里早就在用了
    /// （PushConsole、forgeSide_onThumbnailsReady），用熟的更放心。
    /// </summary>
    public void RequestLongTask(string requestId, string taskName, string arg)
    {
        // 屏幕尺寸要趁现在问——这个请求还站在 UI 线程上。
        // 丢进后台再问的话就得隔着线程去读窗口句柄了
        string screen = taskName == "systemInfo" ? DescribeCurrentScreen() : "";

        Func<string> work = taskName switch
        {
            "upgrade"    => () => UpgradeToVersion(arg),
            "systemInfo" => () => GetSystemInfoJson(screen),
            "testProxy"  => () => TestProxyConnection(arg),
            _            => () => JsonSerializer.Serialize(new { error = Lang.T("task.unknown_long", taskName) }),
        };

        Interlocked.Increment(ref _longTasksInFlight);

        Task.Run(() =>
        {
            try
            {
                string payload;
                bool ok = true;

                try
                {
                    payload = work();
                }
                catch (Exception ex)
                {
                    // 这里必须兜住——裸后台线程上漏出去的异常没人接，会直接带走整个进程
                    ok = false;
                    payload = JsonSerializer.Serialize(new { error = ex.Message });
                }

                // 投递放在 try 外面：干活失败和投递失败是两件事，
                // 混在一起的话投递一出错就会再投一次
                DeliverLongTaskResult(requestId, ok, payload);
            }
            finally
            {
                // 计数一定要放掉，哪怕投递这一步炸了。
                // 漏掉一次的话关窗确认会永远以为还有活在跑，那窗口就再也关不掉了
                Interlocked.Decrement(ref _longTasksInFlight);
            }
        });
    }

    /// <summary>把长任务的结果送回 JS。参数同样交给 JsonSerializer 转义</summary>
    private void DeliverLongTaskResult(string requestId, bool ok, string payload)
    {
        string id = JsonSerializer.Serialize(requestId);
        string body = JsonSerializer.Serialize(payload);
        string flag = ok ? "true" : "false";

        SafeInvoke(() => _webView.CoreWebView2?.ExecuteScriptAsync(
            $"forgeSide_onLongTaskDone({id},{flag},{body})"));
    }

    /// <summary>当前窗口所在屏幕的尺寸描述。只能在 UI 线程上调用</summary>
    private string DescribeCurrentScreen()
    {
        var bounds = Screen.FromHandle(Handle).Bounds;
        return $"{bounds.Width} × {bounds.Height}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _consoleFlushTimer?.Dispose();
            _stopWatchdog?.Dispose();
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
            // 用户添加的文件夹照单全收——模型和出图放在别的盘是常态。
            // 以前这里用 StartsWith(neoRoot) 过滤，结果是界面回"添加成功"却永远扫不到，
            // 设置项还一直躺在 json 里，用户根本没有线索
            var folders = _settings.GalleryFolders
                .Where(Directory.Exists)
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
    private string GetSystemInfoJson(string screenDescription)
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
                    info["cpu"] = $"{obj["Name"]}（{Lang.T("sysinfo.cores", obj["NumberOfCores"], obj["NumberOfLogicalProcessors"])}）";
                    break;
                }
            }

            // GPU — 优先 nvidia-smi 获取准确显存，回退 WMI
            var gpus = new List<string>();
            var gpuProbe = RunProcess(
                "nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader", 5000);

            if (gpuProbe.exitCode == 0)
            {
                foreach (var line in gpuProbe.output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(',');
                    if (parts.Length < 2) continue;

                    string vramStr = parts[1].Trim();
                    // nvidia-smi 返回类似 "12288 MiB"，提取数字转 GB
                    var numMatch = System.Text.RegularExpressions.Regex.Match(vramStr, @"\d+");
                    if (numMatch.Success && long.TryParse(numMatch.Value, out long mb))
                        vramStr = $"{(mb / 1024.0):F1} GB";

                    gpus.Add($"{parts[0].Trim()}（{vramStr}）");
                }
            }

            // 看"解析出东西没有"，不是看"命令跑成功没有"。
            // 以前是 exitCode == 0 就算认了 nvidia，可万一格式变了、一行都没解析出来，
            // gpus 是空的却还是不走 WMI 兜底，显卡那一行就整个空掉——用户只会觉得"坏了"
            bool hasNvidia = gpus.Count > 0;
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
                        // AdapterRAM 在有些驱动上会读成 0。那种时候写「未知」就好，
                        // 别把 0 GB 当成真的显存报出去——猜一个数字比承认不知道更糟
                        string vramText = vram > 0 ? $"{vram / 1073741824.0:F0} GB" : Lang.T("sysinfo.unknown");
                        gpus.Add($"{gpuObj["Name"]}（{vramText}）");
                    }
                }
            }
            if (gpus.Count == 0) gpus.Add(Lang.T("sysinfo.no_gpu"));
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
                    if (string.IsNullOrEmpty(label)) label = Lang.T("sysinfo.unknown_ram");
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
                    string model = (obj["Model"]?.ToString() ?? Lang.T("sysinfo.unknown_disk")).Trim();
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

            // 屏幕尺寸是进这个方法之前、趁还站在 UI 线程上的时候先取好的，
            // 见 DescribeCurrentScreen——Screen.FromHandle 要读窗口句柄，不能带到后台来
            info["screen"] = screenDescription;

            // Python
            // python 2 把版本号写在 stderr 上，所以两边一起看。
            // exitCode 不为 0 说明进程根本没起来（PATH 里没有 python），
            // 这时候 error 里装的是启动失败的报错——可别把它当成版本号显示出去
            var pythonProbe = RunProcess("python", "--version", 3000);
            string pythonVersion = pythonProbe.exitCode == 0
                ? (pythonProbe.output + pythonProbe.error).Trim()
                : "";

            info["python"] = string.IsNullOrEmpty(pythonVersion)
                ? Lang.T("sysinfo.not_detected") : pythonVersion;

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
                // 尝试 git describe / git log。
                // 这两处以前各自拼了一套进程调用，而且都是先 ReadToEnd 再 WaitForExit——
                // git 一不吭声就是永久卡住（那个 3000ms 排在后面，根本没机会生效）。
                // 交给 RunGit：异步读输出、30 秒超时，本来就是同一个活儿。
                //
                // 仓库要认 origin 找（FindNeoRepo）：整合包根目录那个 .git 是整合包自己的，
                // 拿它的 commit 当版本号，显示的是一段跟 NEO 无关的哈希
                if (string.IsNullOrEmpty(ver))
                {
                    string repoDir = FindNeoRepo(neoRoot) ?? neoRoot;
                    var describeResult = RunGit(repoDir, "describe --tags --always");
                    if (describeResult.exitCode == 0 && describeResult.output.Trim().Length > 0)
                        ver = describeResult.output.Trim();
                    else
                    {
                        var logResult = RunGit(repoDir, "log -1 --format=%cd --date=short");
                        if (logResult.exitCode == 0 && logResult.output.Trim().Length > 0)
                            ver = "commit " + logResult.output.Trim();
                    }
                }
                // forge 放给人看的文案，forgeVersion 放干净的版本号。
                // 前端要判断"到底识别出版本没有"就看后者——以前它比对的是一句
                // 会跟着界面语言变的显示文案，换成非中文界面就认不出来了
                info["forge"] = string.IsNullOrEmpty(ver) ? Lang.T("sysinfo.path_prefix", neoRoot) : ver;
                info["forgeVersion"] = ver ?? "";
            }
            catch
            {
                info["forge"] = Lang.T("sysinfo.not_detected");
                info["forgeVersion"] = "";
            }
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

    /// <summary>
    /// Forge NEO 在 GitHub 上的仓库名。必须和 JS 里那个 GITHUB_REPO 是同一个——
    /// 版本列表是从那儿拉的，本地 reset 也得落在同一个仓库上，
    /// 否则就是拿 A 的 tag 去切 B 的文件。
    /// </summary>
    private const string NeoRepoName = "sd-webui-forge-classic";

    /// <summary>
    /// 找出 Forge NEO 自己的那个 Git 仓库，找不到就返回 null。
    ///
    /// 不能只看「哪个目录里有 .git」——第三方整合包在包根和 webui/ 下各有一个仓库，
    /// 实测包根那个是整合包自己的：没有 origin、一个 tag 都没有。
    /// 按目录顺序挑就会挑错，而挑错的代价是 reset --hard 去动一个跟 NEO 无关的仓库。
    /// 所以认 origin：谁的远程地址落在 NEO 的仓库上，谁才是它。
    ///
    /// 两个候选都符合时取靠里的那个（webui/）——越靠里范围越小，
    /// 万一还是挑错了，破坏面也小得多。
    /// </summary>
    private static string? FindNeoRepo(string neoRoot)
    {
        string[] candidates = [Path.Combine(neoRoot, "webui"), neoRoot];
        foreach (var dir in candidates)
        {
            if (!IsGitRepo(dir)) continue;
            var origin = RunGit(dir, "remote get-url origin");
            if (origin.exitCode == 0 && IsNeoOrigin(origin.output)) return dir;
        }
        return null;
    }

    /// <summary>.git 可能是目录（普通仓库），也可能是文件（子模块、worktree），两种都算仓库</summary>
    private static bool IsGitRepo(string dir)
        => Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"));

    /// <summary>
    /// 这条远程地址是不是 NEO 的仓库。
    /// 只比仓库名、不比 owner——用户可能用的是 fork 或者镜像，
    /// 那些的 tag 一样是 NEO 的 tag；而整合包自己的仓库名字对不上，正好被挡掉。
    /// </summary>
    private static bool IsNeoOrigin(string remoteUrl)
    {
        // https://github.com/o/r(.git) 和 git@github.com:o/r(.git) 都要认，
        // 末尾多个斜杠的也见过，索性一起归一化掉
        string url = remoteUrl.Trim().TrimEnd('/')
            .Replace("github.com:", "github.com/", StringComparison.OrdinalIgnoreCase);
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];

        int lastSlash = url.LastIndexOf('/');
        if (lastSlash < 0) return false;
        return url[(lastSlash + 1)..].Equals(NeoRepoName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从 `git status --porcelain` 的输出里挑出真正会被 reset --hard 抹掉的改动。
    ///
    /// 每行前两个字符是状态码，`??` 是未跟踪、`!!` 是被忽略——这两类 reset --hard
    /// 根本不会碰。而 ForgeSide 自己就住在仓库里：ForgeSide/ 目录、
    /// forgeside.settings.json、.thumbcache/ 全是未跟踪，一律算脏的话升级永远被自己挡住。
    /// </summary>
    private static List<string> TrackedChanges(string porcelainOutput)
    {
        var changes = new List<string>();
        foreach (var line in porcelainOutput.Split('\n'))
        {
            string entry = line.TrimEnd();
            if (entry.Length < 3) continue;          // 空行或者不成条目
            if (entry.StartsWith("??") || entry.StartsWith("!!")) continue;
            changes.Add(entry);
        }
        return changes;
    }

    /// <summary>升级/切换到指定 Git tag 版本。返回 JSON：{ success: bool, error?: string }</summary>
    private string UpgradeToVersion(string tagName)
    {
        string Failed(string message)
        {
            PushConsole($"[ForgeSide] ❌ {message}", "error");
            return JsonSerializer.Serialize(new { success = false, error = message });
        }

        try
        {
            // 升级要 reset --hard，会在正在跑的 NEO 脚下把文件换掉。
            // 这种时候拒绝比"尽力而为"好——半个新版本的文件比旧版本还糟
            if (IsNeoRunning)
                return Failed(Lang.T("console.upgrade_neo_running"));

            string neoRoot = FindNeoRoot();
            string? repoDir = FindNeoRepo(neoRoot);
            if (repoDir == null)
                return Failed(Lang.T("console.upgrade_repo_not_found", neoRoot, NeoRepoName));

            PushConsole(Lang.T("console.upgrade_to", tagName, repoDir), "info");
            PushConsole(Lang.T("console.fetch_tags"), "");

            // 1. 获取最新 tags
            var fetchResult = RunGit(repoDir, "fetch --tags");
            if (fetchResult.output.Length > 0)
                PushConsole($"[Git] {fetchResult.output.Trim()}", "");
            if (fetchResult.error.Length > 0)
                PushConsole($"[Git] {fetchResult.error.Trim()}", "");
            if (fetchResult.exitCode != 0)
                return Failed(Lang.T("console.fetch_fail", fetchResult.error));
            PushConsole(Lang.T("console.fetch_done"), "info");

            // 2. 检查有没有会被 reset --hard 抹掉的改动
            var statusResult = RunGit(repoDir, "status --porcelain");
            // status 自己失败（超时 / git 不见了）绝不能当成"干净"——
            // 那恰好就是 reset --hard 抹掉用户改动的最坏情况
            if (statusResult.exitCode != 0)
                return Failed(Lang.T("console.upgrade_status_unknown", statusResult.error));

            var trackedChanges = TrackedChanges(statusResult.output);
            if (trackedChanges.Count > 0)
            {
                PushConsole(Lang.T("console.upgrade_dirty_header"), "warn");
                foreach (var line in trackedChanges.Take(20))
                    PushConsole($"[Git] {line}", "warn");
                PushConsole(Lang.T("console.upgrade_dirty_discard"), "warn");
                PushConsole(Lang.T("console.upgrade_dirty_keep"), "warn");
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    dirty = true,
                    error = Lang.T("upgrade.dirty_error"),
                });
            }

            // 3. 切换到指定 tag
            PushConsole(Lang.T("console.switching", tagName), "");
            var checkoutResult = RunGit(repoDir, $"reset --hard tags/{tagName}");
            if (checkoutResult.output.Length > 0)
                PushConsole($"[Git] {checkoutResult.output.Trim()}", "");
            if (checkoutResult.error.Length > 0 && checkoutResult.exitCode != 0)
                PushConsole($"[Git] {checkoutResult.error.Trim()}", "");
            if (checkoutResult.exitCode != 0)
                return Failed(Lang.T("console.checkout_fail", checkoutResult.error));
            PushConsole(Lang.T("console.upgrade_done", tagName), "info");

            return JsonSerializer.Serialize(new { success = true });
        }
        catch (Exception ex)
        {
            return Failed(Lang.T("console.upgrade_err", ex.Message));
        }
    }

    /// <summary>
    /// 跑一个子进程，把它的 stdout / stderr 收回来。
    ///
    /// 必须异步读输出——同步 ReadToEnd 会在子进程写满管道缓冲时死锁：
    /// 它排在 WaitForExit 前面，子进程卡在写、父进程卡在读，那个超时根本没机会生效。
    /// git 是这样，nvidia-smi 更危险——驱动换个版本就可能往 stderr 吐一大堆警告，
    /// 4KB 的管道几行就满了。所以收口到这一个地方，别再各写各的。
    ///
    /// 返回 exitCode = -1 表示进程压根没起来（PATH 里找不到）或者超时被杀，
    /// 两种情况 error 里都有原因，别把 error 当成正常输出用。
    /// </summary>
    private static (int exitCode, string output, string error) RunProcess(
        string fileName, string arguments, int timeoutMs, string workingDir = "")
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!string.IsNullOrEmpty(workingDir)) startInfo.WorkingDirectory = workingDir;

            using var proc = new Process { StartInfo = startInfo };

            var output = new StringBuilder();
            var error = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (-1, output.ToString(),
                        error + "\n" + Lang.T("console.git_timeout", fileName, timeoutMs));
            }
            proc.WaitForExit();   // 等异步输出投递完，否则最后几行会丢

            return (proc.ExitCode, output.ToString(), error.ToString());
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    /// <summary>
    /// 跑一条 git 命令。30 秒够 fetch 了，再久就说明对面网络有问题。
    ///
    /// 路径末尾那个反斜杠必须去掉：`-C "D:\x\"` 里的 `\"` 在 Windows 命令行规则下
    /// 是被转义的引号，参数不会在那儿结束，整条命令就散架了——git 只会吐一句
    /// 看不清原因的错。ForgeSide 放在包根目录时 FindNeoRoot 返回的正是带尾巴的路径。
    /// </summary>
    private static (int exitCode, string output, string error) RunGit(string workingDir, string args)
        => RunProcess("git", $"-C \"{Path.TrimEndingDirectorySeparator(workingDir)}\" {args}", 30000);

    /// <summary>测试代理连通性。target: "github" 或 "google"。返回 JSON：{ success: bool, url: string, error?: string }</summary>
    private string TestProxyConnection(string target)
    {
        try
        {
            string url = target.ToLowerInvariant() switch
            {
                "google" => "https://www.google.com",
                "huggingface" => "https://huggingface.co",
                _ => throw new ArgumentException(Lang.T("proxy.unknown_target", target))
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
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ForgeSide/1.5");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = client.GetAsync(url).Result;
            sw.Stop();

            if (response.IsSuccessStatusCode)
                return JsonSerializer.Serialize(new { success = true, url, ms = sw.ElapsedMilliseconds });
            else
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    url,
                    error = Lang.T("proxy.status_code", (int)response.StatusCode),
                });
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
        using var dlg = new FolderBrowserDialog { Description = Lang.T("pick_folder.gallery") };
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

    /// <summary>
    /// 找不到 forge_side_ui.html 时顶上来的兜底页。
    /// 以前是 const —— const 里塞不进 Lang.T，于是这一页不管切到哪种语言都是同一句中文。
    /// 而它偏偏是"东西坏了"的时候唯一还看得见的东西，这种时候更该说人话
    /// </summary>
    private static string FallbackHtml => $@"<!DOCTYPE html>
<html><body style='margin:0;height:100vh;display:flex;align-items:center;justify-content:center;
                        background:#1a1816;color:#e0dcd7;font-family:Segoe UI,sans-serif;'>
<div style='text-align:center;'>
  <h2 style='margin:0 0 12px;font-weight:500;'>{Html(Lang.T("fallback.title"))}</h2>
  <p style='margin:0 0 8px;'>{Html(Lang.T("fallback.desc"))}</p>
  <p style='margin:0;color:#8a837b;'>{Html(Lang.T("fallback.hint"))}</p>
</div></body></html>";

    /// <summary>译文进 HTML 之前先转义。译文里万一冒出个 &amp; 或尖括号，页面就散了</summary>
    private static string Html(string text) => System.Net.WebUtility.HtmlEncode(text);
}
