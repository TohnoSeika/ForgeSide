using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ForgeSide;

/// <summary>管理 Forge NEO 进程的启动与终止</summary>
public partial class ProcessManager : IDisposable
{
    /// <summary>包类型——决定用哪种方式启动</summary>
    enum ForgePackage { Unknown, ThirdParty, Official, Licyk }

    private Process? _neoProcess;
    private CancellationTokenSource? _cts;
    private string? _tempBatPath;
    private FileSystemWatcher? _outputWatcher;

    public bool IsRunning => _neoProcess is { HasExited: false };

    public event Action<string>? OnOutput;
    public event Action? OnExited;
    public event Action? OnImageGenerated;

    /// <summary>启动 Forge NEO —— 根据 launchMethod 选择启动方式</summary>
    public void Start(string neoDir, string launchMode, string launchMethod, bool proxyEnabled, string proxyUrl)
    {
        if (IsRunning) return;

        string neoRoot = neoDir.TrimEnd(Path.DirectorySeparatorChar) + "\\";

        // ── 检测包类型 ──
        var pkg = DetectPackage(neoRoot);
        string webuiDir = FindWebuiDir(neoRoot);
        string pythonExe = !string.IsNullOrEmpty(webuiDir) ? FindPythonExe(webuiDir, neoRoot) : "";

        bool canDirect = !string.IsNullOrEmpty(webuiDir)
                      && File.Exists(Path.Combine(webuiDir, "launch.py"))
                      && !string.IsNullOrEmpty(pythonExe);

        OnOutput?.Invoke($"[ForgeSide] 检测到包类型: {pkg}");
        if (canDirect)
            OnOutput?.Invoke($"[ForgeSide] webui 目录: {webuiDir}，Python: {Path.GetFileName(pythonExe)}");

        // ── 根据 launchMethod 选择路径 ──
        switch (launchMethod.ToLowerInvariant())
        {
            case "direct":
                if (canDirect)
                    goto tryDirect;
                OnOutput?.Invoke("[ForgeSide] ⚠ 强制直接启动但找不到 Python/launch.py，切换到 bat 方式…");
                goto fallback;

            case "bat":
                OnOutput?.Invoke("[ForgeSide] 使用 bat 方式（用户设定）…");
                goto fallback;

            default: // auto
                if (canDirect)
                    goto tryDirect;
                goto fallback;
        }

    tryDirect:
        StartDirect(neoRoot, webuiDir, pythonExe, pkg, launchMode, proxyEnabled, proxyUrl);
        return;

    fallback:
        StartViaBat(neoRoot, pkg, launchMode, proxyEnabled, proxyUrl);
    }

    // ═══════════════════════════════════════
    //  方式 A：直接 python launch.py
    // ═══════════════════════════════════════

    /// <summary>直接用 Python 启动 launch.py，不依赖 bat 文件</summary>
    private void StartDirect(string neoRoot, string webuiDir, string pythonExe, ForgePackage pkg, string launchMode, bool proxyEnabled, string proxyUrl)
    {
        string args = BuildLaunchArgs(pkg, launchMode);

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = $"launch.py {args}",
            WorkingDirectory = webuiDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // 注入第三方包需要的内置 Python 环境变量
        if (pkg == ForgePackage.ThirdParty)
        {
            string sysDir = Path.Combine(neoRoot.TrimEnd('\\'), "system") + "\\";
            string pythonDir = Path.Combine(sysDir, "python");
            psi.Environment["SKIP_VENV"] = "1";
            psi.Environment["HF_HOME"]   = Path.Combine(sysDir, "hf-cache");
            psi.Environment["HF_ENDPOINT"] = "https://hf-mirror.com/";
            // 把内置 Python 加到 PATH 开头
            string newPath = $"{pythonDir};{Path.Combine(pythonDir, "Scripts")};" +
                $"{Path.Combine(sysDir, "git", "bin")};%PATH%";
            psi.Environment["PATH"] = newPath;
        }

        // 注入代理环境变量
        if (proxyEnabled && !string.IsNullOrWhiteSpace(proxyUrl))
        {
            psi.Environment["HTTP_PROXY"]  = proxyUrl;
            psi.Environment["HTTPS_PROXY"] = proxyUrl;
            psi.Environment["ALL_PROXY"]   = proxyUrl;
            psi.Environment["NO_PROXY"]    = "localhost,127.0.0.1";
        }

        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding  = Encoding.UTF8;

        _neoProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

        _neoProcess.OutputDataReceived += (_, e) => { if (e.Data != null) OnOutput?.Invoke(CleanLine(e.Data)); };
        _neoProcess.ErrorDataReceived  += (_, e) => { if (e.Data != null) OnOutput?.Invoke(CleanLine(e.Data)); };
        _neoProcess.Exited += (_, _) => OnExited?.Invoke();

        _cts = new CancellationTokenSource();

        _neoProcess.Start();
        _neoProcess.BeginOutputReadLine();
        _neoProcess.BeginErrorReadLine();

        OnOutput?.Invoke($"[ForgeSide] 启动 Forge NEO ({launchMode.ToUpper()} 模式 / 直接启动)...");
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
    private void StartViaBat(string neoRoot, ForgePackage pkg, string launchMode, bool proxyEnabled, string proxyUrl)
    {
        string neoDir = neoRoot.TrimEnd('\\');

        // 根据包类型构建 COMMANDLINE_ARGS
        string extraArgs = pkg == ForgePackage.Official
            ? "--theme day --port 7869 --cuda-malloc --cuda-stream --pin-shared-memory"
            : "--theme day --port 7869 --cuda-malloc --cuda-stream --pin-shared-memory ^\r\n"
                + "\t--ckpt-dirs D:/sd-models/Checkpoint ^\r\n"
                + "\t--ckpt-dirs D:/sd-models/diffusion_model ^\r\n"
                + "\t--text-encoder-dirs D:/sd-models/text_encoder ^\r\n"
                + "\t--lora-dirs D:/sd-models/Lora ^\r\n"
                + "\t--vae-dirs D:/sd-models/VAE ^\r\n"
                + "\t--ad-no-huggingface";

        string modeFlags = launchMode.ToLowerInvariant() switch
        {
            "sdp" => " --disable-sage --disable-flash --disable-xformers",
            "standard" => "",
            _ => "",
        };

        // 动态生成 bat 内容
        string content;
        bool isOfficial = pkg == ForgePackage.Official;
        bool isThirdParty = pkg == ForgePackage.ThirdParty;

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

            if (launchMode.ToLowerInvariant() == "standard")
            {
                content = "@echo off\r\nchcp 65001 >nul\r\n"
                    + $"cd /d \"{neoDir}\"\r\n";
                if (!string.IsNullOrEmpty(pythonPath))
                    content += $"set \"PYTHON={pythonPath}\"\r\n";
                content += "call .\\webui.bat\r\n";
            }
        }
        else if (isThirdParty)
        {
            content = "@echo off\r\nchcp 65001 >nul\r\n"
                + $"call \"{neoRoot}system\\environment.bat\"\r\n"
                + $"cd /d \"{neoRoot}webui\"\r\n"
                + $"set COMMANDLINE_ARGS={extraArgs}{modeFlags}\r\n"
                + "call .\\webui.bat\r\n";

            if (launchMode.ToLowerInvariant() == "standard")
            {
                content = "@echo off\r\nchcp 65001 >nul\r\n"
                    + $"call \"{neoRoot}system\\environment.bat\"\r\n"
                    + $"cd /d \"{neoRoot}webui\"\r\n"
                    + "call .\\webui.bat\r\n";
            }
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

        if (proxyEnabled && !string.IsNullOrWhiteSpace(proxyUrl))
        {
            psi.Environment["HTTP_PROXY"]  = proxyUrl;
            psi.Environment["HTTPS_PROXY"] = proxyUrl;
            psi.Environment["ALL_PROXY"]   = proxyUrl;
            psi.Environment["NO_PROXY"]    = "localhost,127.0.0.1";
        }

        // bat 开头已切到 UTF-8 代码页（chcp 65001），用 UTF-8 读取避免乱码
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding  = Encoding.UTF8;

        _neoProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

        _neoProcess.OutputDataReceived += (_, e) => { if (e.Data != null) OnOutput?.Invoke(CleanLine(e.Data)); };
        _neoProcess.ErrorDataReceived  += (_, e) => { if (e.Data != null) OnOutput?.Invoke(CleanLine(e.Data)); };
        _neoProcess.Exited += (_, _) =>
        {
            CleanupTempBat();
            OnExited?.Invoke();
        };

        _cts = new CancellationTokenSource();

        _neoProcess.Start();
        _neoProcess.BeginOutputReadLine();
        _neoProcess.BeginErrorReadLine();

        OnOutput?.Invoke($"[ForgeSide] 启动 Forge NEO ({launchMode.ToUpper()} 模式 / bat 方式)...");
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

    /// <summary>按优先级查找可用的 Python 路径</summary>
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
        return "python"; // 最后尝试系统 PATH
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
            Filter = "*.png",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        _outputWatcher.Created += (_, e) =>
        {
            if (IsImageFile(e.Name)) OnImageGenerated?.Invoke();
        };
        _outputWatcher.Renamed += (_, e) =>
        {
            if (IsImageFile(e.Name)) OnImageGenerated?.Invoke();
        };
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

    /// <summary>终止 NEO 及其整个进程树</summary>
    public void Stop()
    {
        if (_neoProcess == null || _neoProcess.HasExited) return;

        try
        {
            _neoProcess.Kill(entireProcessTree: true);
        }
        catch { /* already exited */ }
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
        _cts?.Cancel();
        _cts?.Dispose();
        _neoProcess?.Dispose();
        StopOutputWatcher();
        CleanupTempBat();
    }
}
