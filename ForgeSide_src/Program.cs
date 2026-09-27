using System.Text;

namespace ForgeSide;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ApplicationConfiguration.Initialize();

        // MainForm 构造函数里也会设一次语言（那次是按用户的设置来）。
        // 这里先按系统语言设，是为了补一个缺口：万一崩在构造函数读设置之前，
        // 崩溃提示就还是 Lang 的默认值——中文。而现在至少是系统的语言。
        // 两边不冲突：MainForm 那次在后面，会照设置覆盖掉这里
        Lang.SetLanguage(Lang.DetectSystemLanguage());

        // 兜底：漏网的异常至少要留下痕迹。
        // 以前构造函数里任何一步失败，都是"双击图标，然后什么都没有发生"
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        // 只准开一个。
        // 两个实例会互相盖设置——Save() 每次都把内存里整份写出去，后写的赢，
        // 你在第一个窗口改的主题、语言、图库会被第二个窗口那份旧快照冲掉，
        // 而且一声不响。它们还会同时抢 .thumbcache 里那个 sqlite，
        // 而 SQLite 默认要等满 30 秒才认输——正好撞上图库读路径没有兜底的地方：
        // 界面卡住半分钟，然后图库崩
        using var singleInstance = new Mutex(true, @"Local\ForgeSide.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                Lang.T("app.already_running"),
                "ForgeSide", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new MainForm());
    }

    /// <summary>把崩溃写进日志再告诉用户——至少让他知道发生了什么</summary>
    private static void ReportCrash(Exception? ex)
    {
        string detail = ex?.ToString() ?? Lang.T("crash.no_info");

        try
        {
            File.AppendAllText(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "forgeside_crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\r\n{detail}\r\n\r\n");
        }
        catch { /* 连日志都写不下去，那就只能放弃记录 */ }

        try
        {
            MessageBox.Show(
                Lang.T("crash.message", ex?.Message),
                "ForgeSide", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}