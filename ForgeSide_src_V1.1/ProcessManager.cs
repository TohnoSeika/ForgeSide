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

    /// <summary>启动 Forge NEO，执行对应的 bat 文件</summary>
    public void Start(string neoDir, string launchMode, bool proxyEnabled, string proxyUrl)
    {
        if (IsRunning) return;

        string batName = launchMode == "sage"
            ? "Launch_ForgeNeo_Sage.bat"
            : "Launch_ForgeNeo_SDP.bat";

        string batPath = Path.Combine(neoDir, batName);
        if (!File.Exists(batPath))
        {
            OnOutput?.Invoke($"[ForgeSide] 找不到启动文件: {batPath}");
            return;
        }

        // 程序化启动的 cmd.exe 无法通过相对名找到 .bat 文件（Windows 安全策略），
        // 复制 bat 到临时文件并把 %~dp0 替换为 NEO 根目录绝对路径
        string neoRoot = neoDir.TrimEnd(Path.DirectorySeparatorChar) + "\\";
        string content = File.ReadAllText(batPath);
        content = content.Replace("%~dp0", neoRoot);
        content = content.Replace("call webui.bat", "call .\\webui.bat");
        _tempBatPath = Path.Combine(Path.GetTempPath(), $"forgeside_{Guid.NewGuid():N}.bat");
        File.WriteAllText(_tempBatPath, content);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{_tempBatPath}\"\"",
            WorkingDirectory = Path.Combine(neoDir, "webui"),
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

        // 使用系统 OEM 编码避免中文乱码
        var oem = Encoding.GetEncoding(0);
        psi.StandardOutputEncoding = oem;
        psi.StandardErrorEncoding  = oem;

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
