using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ForgeSide;

/// <summary>管理 Forge NEO 进程的启动与终止</summary>
public partial class ProcessManager : IDisposable
{
    private Process? _neoProcess;
    private CancellationTokenSource? _cts;
    private string? _tempBatPath;
    private FileSystemWatcher? _outputWatcher;

    public bool IsRunning => _neoProcess is { HasExited: false };

    public event Action<string>? OnOutput;
    public event Action? OnExited;
    public event Action? OnImageGenerated;

    /// <summary>启动 Forge NEO，根据启动模式动态生成 bat 内容</summary>
    public void Start(string neoDir, string launchMode, bool proxyEnabled, string proxyUrl)
    {
        if (IsRunning) return;

        string neoRoot = neoDir.TrimEnd(Path.DirectorySeparatorChar) + "\\";

        // 检测版本结构：第三方包有 system/environment.bat，官方版 webui.bat 在根目录
        bool isThirdParty = File.Exists(Path.Combine(neoDir, "system", "environment.bat"));
        bool isOfficial = File.Exists(Path.Combine(neoDir, "webui.bat"));

        // 根据版本和模式构建 COMMANDLINE_ARGS
        string extraArgs;
        if (isOfficial)
        {
            // 官方版：通用参数，不含第三方包特有的路径和插件
            extraArgs = "--theme day --port 7869 --cuda-malloc --cuda-stream --pin-shared-memory";
        }
        else
        {
            // 第三方包：保留原有的模型路径和插件参数
            extraArgs = "--theme day --port 7869 --cuda-malloc --cuda-stream --pin-shared-memory ^\r\n"
                + "\t--ckpt-dirs D:/sd-models/Checkpoint ^\r\n"
                + "\t--ckpt-dirs D:/sd-models/diffusion_model ^\r\n"
                + "\t--text-encoder-dirs D:/sd-models/text_encoder ^\r\n"
                + "\t--lora-dirs D:/sd-models/Lora ^\r\n"
                + "\t--vae-dirs D:/sd-models/VAE ^\r\n"
                + "\t--ad-no-huggingface";
        }

        string modeFlags = launchMode.ToLowerInvariant() switch
        {
            "sdp" => " --disable-sage --disable-flash --disable-xformers",
            "standard" => "",
            _ => "" // sage 模式：不加禁用参数，保留 SageAttention
        };

        // 动态生成 bat 内容（自适应两种结构）
        string content;
        if (isOfficial)
        {
            // 官方版：webui.bat 在根目录，没有 system/environment.bat
            // 读取 webui-user.bat 中的 PYTHON 路径设置
            string pythonPath = "";
            string userBat = Path.Combine(neoDir, "webui-user.bat");
            if (File.Exists(userBat))
            {
                foreach (var line in File.ReadLines(userBat))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"^set\s+PYTHON=(.+)$");
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
            // 第三方包：有 system/environment.bat，webui.bat 在 webui/ 里
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
            // 无法识别：直接尝试 webui.bat
            content = "@echo off\r\nchcp 65001 >nul\r\n"
                + $"cd /d \"{neoDir}\"\r\n"
                + "call .\\webui.bat\r\n";
        }
        _tempBatPath = Path.Combine(Path.GetTempPath(), $"forgeside_{Guid.NewGuid():N}.bat");
        File.WriteAllText(_tempBatPath, content);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{_tempBatPath}\"\"",
            WorkingDirectory = isOfficial ? neoDir : Path.Combine(neoDir, "webui"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // 注入代理环境变量
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

        OnOutput?.Invoke($"[ForgeSide] 启动 Forge NEO ({launchMode.ToUpper()} 模式)...");
    }

    /// <summary>启动输出目录监控，新图片生成时触发 OnImageGenerated</summary>
    public void StartOutputWatcher(string neoDir)
    {
        StopOutputWatcher();
        string outputDir = Path.Combine(neoDir, "webui", "output", "images");
        if (!Directory.Exists(outputDir)) return;

        _outputWatcher = new FileSystemWatcher(outputDir)
        {
            Filter = "*.png",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        _outputWatcher.Created += (_, _) => OnImageGenerated?.Invoke();
        _outputWatcher.Renamed += (_, e) =>
        {
            if (e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                OnImageGenerated?.Invoke();
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

    // ANSI 转义序列 + 回车进度条清理
    [GeneratedRegex(@"\x1b\[[0-9;]*[a-zA-Z]")]
    private static partial Regex AnsiRegex();

    private static string CleanLine(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;

        // 去掉 ANSI 转义序列（颜色、光标控制等）
        var cleaned = AnsiRegex().Replace(raw, "");

        // 处理回车更新的进度条：取最后一个 \r 后的内容
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
