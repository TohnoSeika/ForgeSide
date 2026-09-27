using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ForgeSide;

/// <summary>管理 Forge NEO 进程的启动与终止</summary>
public partial class ProcessManager : IDisposable
{
    /// <summary>包类型——决定用哪种方式启动</summary>
    enum ForgePackage { Unknown, ThirdParty, Official, Licyk }

    /// <summary>启动方式。不再让用户选，由包里有什么决定</summary>
    enum LaunchPlan { Direct, ViaBat }

    /// <summary>
    /// 一次终止请求的结果。以前 Stop() 是 void，Kill 抛了就在这里吞掉——
    /// 外面只知道"我叫过停了"，不知道"到底停没停"，
    /// 于是界面替进程宣布了它已经死掉，而它还在。
    /// </summary>
    public enum StopOutcome
    {
        /// <summary>本来就不在跑，不用等谁退出</summary>
        NothingToStop,
        /// <summary>终止请求发出去了，但进程还没退出，要等 Exited 事件来确认</summary>
        Terminating,
        /// <summary>Kill 自己失败了，进程大概还活着</summary>
        KillFailed,
    }

    /// <summary>一次启动要用到的全部信息——换另一种方式重试时要复用同一份</summary>
    private sealed record LaunchContext(
        string NeoRoot,
        string WebuiDir,
        string PythonExe,       // 空 = 包里没有解释器，只能用系统 PATH 上那个
        ForgePackage Package,
        string LaunchMode,
        bool ProxyEnabled,
        string ProxyUrl);

    private Process? _neoProcess;
    private CancellationTokenSource? _cts;
    private string? _tempBatPath;
    private FileSystemWatcher? _outputWatcher;

    private LaunchContext? _ctx;
    private LaunchPlan _plan;
    private DateTime _startedAt;
    private bool _retriedPlan;              // 换方式重试只给一次机会，别来回弹
    private bool _stoppingIntentionally;    // 区分"用户点的停止"和"自己崩了"

    /// <summary>
    /// 进程状态的总闸。启动、停止、退出处理三边都在碰 _neoProcess 和上面的标志位，
    /// 而退出事件是从线程池上来的——不锁的话会撞出两种很难复现的坏结果：
    /// 用户点停止的同时自动重试又拉起一个；或者界面显示没在跑、NEO 在后台安静地吃显存。
    /// 锁是可重入的，所以 IsRunning 在持锁的 Start 里被调用也没问题
    /// </summary>
    private readonly object _gate = new();

    /// <summary>撑过这么久就算真的起来了，之后再退出不再当作启动失败</summary>
    private static readonly TimeSpan HealthyRunThreshold = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 是否正在运行。
    /// HasExited 在"从未成功启动"的 Process 上会抛异常，那种状态一律当作没在运行——
    /// 否则一次启动失败，之后每次点启动都会崩在这里。
    /// </summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                if (_neoProcess == null) return false;
                try { return !_neoProcess.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
        }
    }

    public event Action<string>? OnOutput;
    public event Action? OnExited;
    public event Action? OnImageGenerated;

    /// <summary>
    /// 启动 Forge NEO。
    /// 启动方式不再问用户——包里有什么就用什么。一种方式起来后马上挂掉时，
    /// 会自动换另一种再试一次；两种都失败才会报退出。
    /// </summary>
    public void Start(string neoDir, string launchMode, bool proxyEnabled, string proxyUrl)
    {
        // 探测放在锁外面：这里要在 Z: / F: 这种盘上挨个 stat，
        // 拽着总闸一起等不值得——锁里只留状态判断和拉起那两件事
        string neoRoot = neoDir.TrimEnd(Path.DirectorySeparatorChar) + "\\";
        var pkg = DetectPackage(neoRoot);
        string webuiDir = FindWebuiDir(neoRoot);
        string pythonExe = !string.IsNullOrEmpty(webuiDir) ? FindPythonExe(webuiDir, neoRoot) : "";

        var ctx = new LaunchContext(neoRoot, webuiDir, pythonExe, pkg, launchMode, proxyEnabled, proxyUrl);
        LaunchPlan? plan = ChooseLaunchPlan(ctx);

        lock (_gate)
        {
            // 判断和拉起在同一把锁里，不然连点两下启动会拉起两个 NEO
            if (IsRunning) return;

            _ctx = ctx;
            _retriedPlan = false;
            _stoppingIntentionally = false;

            OnOutput?.Invoke(Lang.T("console.package_type", pkg));
            if (!string.IsNullOrEmpty(webuiDir))
                OnOutput?.Invoke(Lang.T("console.webui_dir", webuiDir));
            // 打全路径——只显示 "python.exe" 的话，出问题时根本看不出用的是包里的还是系统的
            if (!string.IsNullOrEmpty(pythonExe))
                OnOutput?.Invoke($"[ForgeSide] Python: {pythonExe}");

            if (plan == null)
            {
                ReportNothingToLaunch(ctx);
                throw new InvalidOperationException(Lang.T("error.no_launch_mode"));
            }

            Launch(plan.Value);
        }
    }

    /// <summary>
    /// 按证据决定启动方式。顺序是有讲究的：
    /// 第三方包的 environment.bat 里还有 ffmpeg / vcredist 这些 ForgeSide 复刻不全的东西，
    /// 所以一律交回给包作者写的入口，不自作聪明。
    /// </summary>
    private static LaunchPlan? ChooseLaunchPlan(LaunchContext ctx)
    {
        if (ctx.Package == ForgePackage.ThirdParty && HasBatEntry(ctx))
            return LaunchPlan.ViaBat;

        if (!string.IsNullOrEmpty(ctx.WebuiDir) && !string.IsNullOrEmpty(ctx.PythonExe))
            return LaunchPlan.Direct;

        // 没有包内解释器、但有 webui.bat —— 交给它去建 venv。
        // 这一步必须排在"用系统 Python 硬启动"前面：全新包就是用系统 Python 跑才坏的，
        // 那种情况下 forge 会试图往系统 Python 里装一整套依赖，又慢又脏
        if (HasBatEntry(ctx))
            return LaunchPlan.ViaBat;

        // 实在没有别的办法，用系统 PATH 上那个 python 试一次
        if (!string.IsNullOrEmpty(ctx.WebuiDir))
            return LaunchPlan.Direct;

        return null;
    }

    /// <summary>真正开始一次启动</summary>
    private void Launch(LaunchPlan plan)
    {
        _plan = plan;
        _startedAt = DateTime.UtcNow;

        if (plan == LaunchPlan.Direct) StartDirect(_ctx!);
        else                           StartViaBat(_ctx!);

        OnOutput?.Invoke(Lang.T("console.launching", _ctx!.LaunchMode.ToUpperInvariant(), Describe(plan)));
    }

    private static string Describe(LaunchPlan plan)
        => plan == LaunchPlan.Direct ? Lang.T("launch.direct") : Lang.T("launch.via_bat");

    // bat 方式最终要 call 的那个文件。判断可用性和生成 bat 必须照着同一份走，
    // 不然会出现"以为可用、生成出来却 call 了个不存在的文件"
    private static string BatEntryPath(LaunchContext ctx)
        => ctx.Package == ForgePackage.ThirdParty
            ? Path.Combine(ctx.NeoRoot, "webui", "webui.bat")
            : Path.Combine(ctx.NeoRoot.TrimEnd('\\'), "webui.bat");

    private static bool HasBatEntry(LaunchContext ctx) => File.Exists(BatEntryPath(ctx));

    /// <summary>什么都没找到时，把找过哪些地方说清楚——静默失败是最坏的结果</summary>
    private void ReportNothingToLaunch(LaunchContext ctx)
    {
        OnOutput?.Invoke(Lang.T("console.nothing_to_launch", ctx.NeoRoot));
        OnOutput?.Invoke(Lang.T("console.looked_for"));
        OnOutput?.Invoke(Lang.T("console.place_hint"));
    }

    // ═══════════════════════════════════════
    //  方式 A：直接 python launch.py
    // ═══════════════════════════════════════

    /// <summary>方式 A：直接用 Python 启动 launch.py，不依赖 bat 文件</summary>
    private void StartDirect(LaunchContext ctx)
    {
        string args = BuildLaunchArgs(ctx.Package, ctx.LaunchMode);
        // 包里没有解释器时只能借系统 PATH 上那个——这种情况 ChooseLaunchPlan 已经尽量避开了
        string pythonExe = string.IsNullOrEmpty(ctx.PythonExe) ? "python" : ctx.PythonExe;

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = $"launch.py {args}",
            WorkingDirectory = ctx.WebuiDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // 注入第三方包需要的内置 Python 环境变量
        if (ctx.Package == ForgePackage.ThirdParty)
        {
            string sysDir = Path.Combine(ctx.NeoRoot.TrimEnd('\\'), "system") + "\\";
            string pythonDir = Path.Combine(sysDir, "python");
            psi.Environment["SKIP_VENV"] = "1";
            psi.Environment["HF_HOME"]   = Path.Combine(sysDir, "hf-cache");
            psi.Environment["HF_ENDPOINT"] = "https://hf-mirror.com/";
            // 把内置 Python 加到 PATH 开头。
            // 这里不能写 %PATH%——UseShellExecute=false 时没有 cmd 参与，
            // CreateProcess 不展开变量，字面量会原样塞进子进程的 PATH，
            // 继承来的系统 PATH 反而全丢了
            string inheritedPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.Environment["PATH"] =
                $"{pythonDir};{Path.Combine(pythonDir, "Scripts")};" +
                $"{Path.Combine(sysDir, "git", "bin")};{inheritedPath}";
        }

        // 注入代理环境变量
        if (ctx.ProxyEnabled && !string.IsNullOrWhiteSpace(ctx.ProxyUrl))
        {
            psi.Environment["HTTP_PROXY"]  = ctx.ProxyUrl;
            psi.Environment["HTTPS_PROXY"] = ctx.ProxyUrl;
            psi.Environment["ALL_PROXY"]   = ctx.ProxyUrl;
            psi.Environment["NO_PROXY"]    = "localhost,127.0.0.1";
        }

        // 光设 StandardOutputEncoding 是不够的——Python 在 stdout 被重定向时
        // 用的是 locale.getpreferredencoding()，中文 Windows 上就是 GBK，
        // 父进程按 UTF-8 解就是满屏乱码
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding  = Encoding.UTF8;

        _neoProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

        _neoProcess.OutputDataReceived += (_, e) => { if (e.Data != null) OnOutput?.Invoke(CleanLine(e.Data)); };
        _neoProcess.ErrorDataReceived  += (_, e) => { if (e.Data != null) OnOutput?.Invoke(CleanLine(e.Data)); };
        // 把 sender 一并带上，让"这是不是现在这个进程"的判断在锁里做。
        // 在事件外面先判一次再进函数的话，判完到拿到锁之间新进程可能已经起来了，
        // 那时候就会拿着老进程的退出去处理新进程——界面显示没在跑，NEO 在后台转
        _neoProcess.Exited += (sender, _) => HandleProcessExited(sender as Process);

        _cts = new CancellationTokenSource();

        try
        {
            _neoProcess.Start();
            _neoProcess.BeginOutputReadLine();
            _neoProcess.BeginErrorReadLine();
        }
        catch
        {
            // 启动失败就把这个"从未启动过"的 Process 丢掉，
            // 否则之后每次 IsRunning / Stop 都会在它身上抛异常
            DiscardFailedProcess();
            throw;
        }

    }

    /// <summary>构建 launch.py 命令行参数</summary>
    private static string BuildLaunchArgs(ForgePackage pkg, string launchMode)
    {
        string args = "--theme day --port 7869 --cuda-malloc --cuda-stream --pin-shared-memory";

        // 第三方包加额外的模型路径
        if (pkg == ForgePackage.ThirdParty)
        {
            args += " --ckpt-dirs D:/sd-models/Checkpoint"
                + " --ckpt-dirs D:/sd-models/diffusion_model"
                + " --text-encoder-dirs D:/sd-models/text_encoder"
                + " --lora-dirs D:/sd-models/Lora"
                + " --vae-dirs D:/sd-models/VAE"
                + " --ad-no-huggingface";
        }

        // 启动模式
        args += launchMode.ToLowerInvariant() switch
        {
            "sdp" => " --disable-sage --disable-flash --disable-xformers",
            "standard" => "",
            _ => "", // sage 模式：不加禁用参数
        };

        return args;
    }

    // ═══════════════════════════════════════
    //  方式 B：生成 bat → 调 webui.bat
    // ═══════════════════════════════════════

    /// <summary>通过生成临时 bat 调用 webui.bat 来启动（fallback）</summary>
    /// <summary>方式 B：生成临时 bat 调用包自带的 webui.bat（能顺带把 venv 建起来）</summary>
    private void StartViaBat(LaunchContext ctx)
    {
        string neoRoot = ctx.NeoRoot;
        string neoDir  = neoRoot.TrimEnd('\\');

        // 根据包类型构建 COMMANDLINE_ARGS。
        // 参数是走环境变量进去的——webui.bat 只写 launch.py %*，真正把它塞进
        // sys.argv 的是包里的 modules/paths_internal.py
        string extraArgs = ctx.Package == ForgePackage.Official
            ? "--theme day --port 7869 --cuda-malloc --cuda-stream --pin-shared-memory"
            : "--theme day --port 7869 --cuda-malloc --cuda-stream --pin-shared-memory ^\r\n"
                + "\t--ckpt-dirs D:/sd-models/Checkpoint ^\r\n"
                + "\t--ckpt-dirs D:/sd-models/diffusion_model ^\r\n"
                + "\t--text-encoder-dirs D:/sd-models/text_encoder ^\r\n"
                + "\t--lora-dirs D:/sd-models/Lora ^\r\n"
                + "\t--vae-dirs D:/sd-models/VAE ^\r\n"
                + "\t--ad-no-huggingface";

        string modeFlags = ctx.LaunchMode.ToLowerInvariant() switch
        {
            "sdp" => " --disable-sage --disable-flash --disable-xformers",
            "standard" => "",
            _ => "",
        };

        // 动态生成 bat 内容
        string content;
        bool isOfficial = ctx.Package == ForgePackage.Official;
        bool isThirdParty = ctx.Package == ForgePackage.ThirdParty;

        if (isOfficial)
        {
            string pythonPath = "";
            string userBat = Path.Combine(neoDir, "webui-user.bat");
            if (File.Exists(userBat))
            {
                foreach (var line in File.ReadLines(userBat))
                {
                    var m = Regex.Match(line, @"^set\s+PYTHON=(.+)$");
                    if (m.Success) { pythonPath = m.Groups[1].Value.Trim(); break; }
                }
            }

            content = "@echo off\r\nchcp 65001 >nul\r\n"
                + $"cd /d \"{neoDir}\"\r\n";
            if (!string.IsNullOrEmpty(pythonPath))
                content += $"set \"PYTHON={pythonPath}\"\r\n";
            content += $"set COMMANDLINE_ARGS={extraArgs}{modeFlags}\r\n"
                + "call .\\webui.bat\r\n";
        }
        else if (isThirdParty)
        {
            content = "@echo off\r\nchcp 65001 >nul\r\n"
                + $"call \"{neoRoot}system\\environment.bat\"\r\n"
                + $"cd /d \"{neoRoot}webui\"\r\n"
                + $"set COMMANDLINE_ARGS={extraArgs}{modeFlags}\r\n"
                + "call .\\webui.bat\r\n";
        }
        else
        {
            // Licyk / Unknown：尝试 webui.bat
            content = "@echo off\r\nchcp 65001 >nul\r\n"
                + $"cd /d \"{neoDir}\"\r\n"
                + "call .\\webui.bat\r\n";
        }

        _tempBatPath = Path.Combine(Path.GetTempPath(), $"forgeside_{Guid.NewGuid():N}.bat");
        File.WriteAllText(_tempBatPath, content);

        string workDir = isOfficial ? neoDir : Path.Combine(neoDir, "webui");

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{_tempBatPath}\"\"",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (ctx.ProxyEnabled && !string.IsNullOrWhiteSpace(ctx.ProxyUrl))
        {
            psi.Environment["HTTP_PROXY"]  = ctx.ProxyUrl;
            psi.Environment["HTTPS_PROXY"] = ctx.ProxyUrl;
            psi.Environment["ALL_PROXY"]   = ctx.ProxyUrl;
            psi.Environment["NO_PROXY"]    = "localhost,127.0.0.1";
        }

        // bat 开头已切到 UTF-8 代码页（chcp 65001），用 UTF-8 读取避免乱码。
        // 但 chcp 只管控制台代码页、管不到被重定向的管道，所以还要压 Python 用 UTF-8
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding  = Encoding.UTF8;

        _neoProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

        _neoProcess.OutputDataReceived += (_, e) => { if (e.Data != null) OnOutput?.Invoke(CleanLine(e.Data)); };
        _neoProcess.ErrorDataReceived  += (_, e) => { if (e.Data != null) OnOutput?.Invoke(CleanLine(e.Data)); };
        // 把 sender 一并带上，让"这是不是现在这个进程"的判断在锁里做。
        // 在事件外面先判一次再进函数的话，判完到拿到锁之间新进程可能已经起来了，
        // 那时候就会拿着老进程的退出去处理新进程——界面显示没在跑，NEO 在后台转
        _neoProcess.Exited += (sender, _) => HandleProcessExited(sender as Process);

        _cts = new CancellationTokenSource();

        try
        {
            _neoProcess.Start();
            _neoProcess.BeginOutputReadLine();
            _neoProcess.BeginErrorReadLine();
        }
        catch
        {
            DiscardFailedProcess();
            throw;
        }

    }

    // ═══════════════════════════════════════
    //  退出处理 / 自动换方式重试
    // ═══════════════════════════════════════

    /// <summary>两种启动方式共用同一个退出处理。exited 是发来这个事件的那个进程</summary>
    private void HandleProcessExited(Process? exited)
    {
        // OnOutput 的订阅者只是把一行字塞进控制台队列，不会再调回这边，
        // 所以在锁里发它是安全的；OnExited 会推着界面换状态，放在锁外面发
        lock (_gate)
        {
            // 只认"现在这一个"进程的退出。老进程的退出事件可能晚到，
            // 那时候 _neoProcess 已经指向新进程了——照单全收会把新进程的状态带歪
            if (!ReferenceEquals(exited, _neoProcess)) return;

            CleanupTempBat();

            if (ShouldRetryOtherWay())
            {
                LaunchPlan next = _plan == LaunchPlan.Direct ? LaunchPlan.ViaBat : LaunchPlan.Direct;
                OnOutput?.Invoke(Lang.T("console.died_early",
                    (DateTime.UtcNow - _startedAt).TotalSeconds, Describe(next)));
                _retriedPlan = true;
                try
                {
                    Launch(next);
                    return;
                }
                catch (Exception ex)
                {
                    // 换个方式连拉都拉不起来，那就到此为止——但要说清楚是哪一步断的。
                    // 这里不能往外抛：Exited 回调在线程池线程上，抛出去会变成全局崩溃弹窗
                    OnOutput?.Invoke(Lang.T("console.retry_failed", Describe(next), ex.Message));
                }
            }

            // 退出这件事只报一行，原因是并进来的。
            // 以前 Stop() 那边会先喊一句「正在终止 NEO…」，那句写进日志就永远没有下文——
            // 「正在」是给当下看的，过后再读只知道"那会儿在终止"，不知道到底停成没停成。
            // 现在把"为什么退出"放到结果这一行，日志干净，信息也没少
            OnOutput?.Invoke(_stoppingIntentionally
                ? Lang.T("console.exit_requested")
                : Lang.T("console.exit_unexpected"));
        }

        OnExited?.Invoke();
    }

    /// <summary>
    /// 起来之后马上死掉，是"启动失败"而不是"跑完了"——只有这种才值得换方式。
    /// 跑够久了才退出、或者用户自己点的停止，都不能重试，
    /// 否则用户点一下停止，程序会自作主张再启动一遍。
    /// </summary>
    private bool ShouldRetryOtherWay()
    {
        if (_stoppingIntentionally || _retriedPlan || _ctx == null) return false;
        if (DateTime.UtcNow - _startedAt > HealthyRunThreshold) return false;

        if (_plan == LaunchPlan.Direct)
            return HasBatEntry(_ctx);

        // 第三方包不往"直接启动"退。它的 environment.bat 里还有 ffmpeg、vcredist、
        // HF_HOME 这些 ForgeSide 复刻不全的东西，绕过去拿包里的裸解释器硬拉，
        // 起步就是一场缺依赖的连环错——而且它本来就跟 ChooseLaunchPlan 说好了
        // 「一律交回包作者的入口，不自作聪明」，重试这条路不能自己把它推翻
        if (_ctx.Package == ForgePackage.ThirdParty) return false;

        // 反过来往直接启动重试：包里得有真解释器才值得试。
        // 否则等于拿系统 Python 去 pip 装一整套依赖，又慢又脏
        return !string.IsNullOrEmpty(_ctx.PythonExe);
    }

    // ═══════════════════════════════════════
    //  包类型检测
    // ═══════════════════════════════════════

    /// <summary>检测 NEO 包的类型，用于决定启动方式</summary>
    private static ForgePackage DetectPackage(string neoRoot)
    {
        bool hasWebuiDir = Directory.Exists(Path.Combine(neoRoot, "webui"));
        bool hasCoreDir   = Directory.Exists(Path.Combine(neoRoot, "core"));
        bool hasSystemBat = File.Exists(Path.Combine(neoRoot, "system", "environment.bat"));
        bool hasWebuiBat = File.Exists(Path.Combine(neoRoot, "webui.bat"));
        bool hasHanamizuki = File.Exists(Path.Combine(neoRoot, "hanamizuki.bat"));
        bool hasVenv     = Directory.Exists(Path.Combine(neoRoot, "venv"));
        bool hasLaunchPy = File.Exists(Path.Combine(neoRoot, "launch.py"));

        if (hasWebuiDir && hasSystemBat) return ForgePackage.ThirdParty;
        if (hasWebuiBat || hasVenv || hasLaunchPy) return ForgePackage.Official;
        if (hasCoreDir || hasHanamizuki) return ForgePackage.Licyk;
        if (hasWebuiDir) return ForgePackage.ThirdParty;
        return ForgePackage.Unknown;
    }

    /// <summary>找到 launch.py 所在的 webui 子目录</summary>
    private static string FindWebuiDir(string neoRoot)
    {
        // 已知子目录
        foreach (var sub in new[] { "webui", "core" })
        {
            string dir = Path.Combine(neoRoot, sub);
            if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "launch.py")))
                return dir;
        }
        // 根目录自身
        if (File.Exists(Path.Combine(neoRoot, "launch.py")))
            return neoRoot;
        return "";
    }

    /// <summary>按优先级查找包内可用的 Python。找不到返回空字符串</summary>
    private static string FindPythonExe(string webuiDir, string neoRoot)
    {
        string[] candidates = [
            // 第三方包的内置 Python（有 system/environment.bat 的包）
            Path.Combine(neoRoot, "system", "python", "python.exe"),
            // webui 目录下的 venv
            Path.Combine(webuiDir, "venv", "Scripts", "python.exe"),
            // webui 目录下的内置 Python
            Path.Combine(webuiDir, "python", "python.exe"),
            // 根目录下的 venv
            Path.Combine(neoRoot, "venv", "Scripts", "python.exe"),
            // 根目录下的内置 Python
            Path.Combine(neoRoot, "python", "python.exe"),
        ];
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        // 找不到就返回空，不要返回裸 "python"。
        // 调用方必须能分辨"环境齐了"和"只能碰运气"——前者可以直接启动，
        // 后者应该优先交给 webui.bat 去把 venv 建起来
        return "";
    }

    // ═══════════════════════════════════════
    //  输出目录监控
    // ═══════════════════════════════════════

    /// <summary>启动输出目录监控，新图片生成时触发 OnImageGenerated</summary>
    public void StartOutputWatcher(string neoDir)
    {
        StopOutputWatcher();

        // 自适应不同包结构的 output 目录
        string[] candidates = [
            Path.Combine(neoDir, "webui", "output", "images"),
            Path.Combine(neoDir, "core",  "output", "images"),
            Path.Combine(neoDir, "outputs"),
            Path.Combine(neoDir, "output"),
        ];
        string outputDir = "";
        foreach (var c in candidates)
        {
            if (Directory.Exists(c)) { outputDir = c; break; }
        }
        if (string.IsNullOrEmpty(outputDir)) return;

        _outputWatcher = new FileSystemWatcher(outputDir)
        {
            // 格式交给 IsImageFile 判断，别在这里定死 *.png——
            // jpg / webp 出图同样该触发提示音
            Filter = "*.*",
            IncludeSubdirectories = true,   // Forge 默认按日期分目录存图
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
        };
        _outputWatcher.Created += (_, e) =>
        {
            if (IsImageFile(e.Name)) OnImageGenerated?.Invoke();
        };
        _outputWatcher.Renamed += (_, e) =>
        {
            if (IsImageFile(e.Name)) OnImageGenerated?.Invoke();
        };

        // 事件挂完再开始监听，否则第一个事件可能落在还没人听的窗口里
        _outputWatcher.EnableRaisingEvents = true;
    }

    private void StopOutputWatcher()
    {
        if (_outputWatcher != null)
        {
            _outputWatcher.EnableRaisingEvents = false;
            _outputWatcher.Dispose();
            _outputWatcher = null;
        }
    }

    /// <summary>
    /// 终止 NEO 及其整个进程树。
    /// 注意 Kill 只是"发出请求"，不是"等它死"——拿到 Terminating 之后
    /// 还得靠 Exited 事件来确认，调用方不能拿它当"已经停了"。
    /// </summary>
    public StopOutcome Stop()
    {
        lock (_gate)
        {
            // 先记下意图——不然进程退出时会被当成"启动失败"，然后自作主张再启动一遍。
            // 这句必须在锁里：自动重试那边读的就是它，读早了照样会重来一次
            _stoppingIntentionally = true;

            if (_neoProcess == null) return StopOutcome.NothingToStop;

            try
            {
                if (_neoProcess.HasExited) return StopOutcome.NothingToStop;
                _neoProcess.Kill(entireProcessTree: true);
                return StopOutcome.Terminating;
            }
            catch (InvalidOperationException) { return StopOutcome.NothingToStop; }  // 这个 Process 从未成功启动
            catch { return StopOutcome.KillFailed; }                                 // 以前这里一声不吭
        }
    }

    /// <summary>
    /// 丢掉一个没能正常启动起来的 Process。
    /// 已经在跑的必须先杀掉——Dispose 只是把 .NET 这边的句柄放掉，不会替你结束进程。
    /// 以前这里直接 Dispose 就走，于是"界面说没启动、NEO 在后台安静地吃显存"，
    /// 只能去任务管理器里收尸。锁是可重入的，从持锁的调用点进来也没关系
    /// </summary>
    private void DiscardFailedProcess()
    {
        lock (_gate)
        {
            if (_neoProcess == null) return;

            try
            {
                if (!_neoProcess.HasExited)
                    _neoProcess.Kill(entireProcessTree: true);
            }
            catch
            {
                // 杀不动。这时候**不能**把引用清掉：留着它，
                // 外面的 Stop() 才有东西可以再试一次，界面也才守得住「还在运行」这个事实。
                // 直接把引用扔了才是真的放手不管——那正是孤儿的由来
                return;
            }

            _neoProcess.Dispose();
            _neoProcess = null;
        }
    }

    /// <summary>判断文件名是否是支持的 AI 图片格式</summary>
    private static bool IsImageFile(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
    }

    // ANSI 转义序列 + 回车进度条清理
    [GeneratedRegex(@"\x1b\[[0-9;]*[a-zA-Z]")]
    private static partial Regex AnsiRegex();

    private static string CleanLine(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;

        var cleaned = AnsiRegex().Replace(raw, "");

        int lastCR = cleaned.LastIndexOf('\r');
        if (lastCR >= 0)
            cleaned = cleaned[(lastCR + 1)..];

        return cleaned.TrimEnd();
    }

    private void CleanupTempBat()
    {
        try
        {
            if (_tempBatPath != null && File.Exists(_tempBatPath))
                File.Delete(_tempBatPath);
        }
        catch { /* 清理失败不影响主流程 */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stoppingIntentionally = true;   // 正在收摊，别把退出当成启动失败
            _cts?.Cancel();
            _cts?.Dispose();
            _neoProcess?.Dispose();
            StopOutputWatcher();
            CleanupTempBat();
        }
    }
}
