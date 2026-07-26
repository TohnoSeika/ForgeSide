// ——这个文件是桃华帮 Minagi 写的哦
// 四语言翻译中枢：简体中文 / 繁体中文 / English / 日本語

using System.Globalization;

namespace ForgeSide;

public static class Lang
{
    private static string _current = "zh-CN";

    private static readonly Dictionary<string, Dictionary<string, string>> Data = new()
    {
        ["zh-CN"] = new()
        {
            ["window.title"]          = "ForgeSide V1.41",
            ["close_dialog.title"]    = "ForgeSide — 确认关闭",
            ["close_dialog.msg"]      = "Forge NEO 进程正在运行中，请先在控制台点击「终止NEO」按钮结束进程！",
            ["close_dialog.force"]    = "强制停止",
            ["close_dialog.ok"]       = "确定",
            ["filter.png"]            = "PNG 图片|*.png|所有文件|*.*",
            ["filter.image"]          = "图片|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*",
            ["filter.audio"]          = "音频文件|*.mp3;*.wav;*.ogg;*.aiff;*.flac;*.m4a|所有文件|*.*",
            ["filter.exe"]            = "可执行文件|*.exe|所有文件|*.*",
            ["pick_sound.title"]      = "选择提示音文件",
            ["pick_folder.gallery"]   = "选择要监听的图片文件夹",
            ["webview2.title"]        = "ForgeSide — 组件缺失",
            ["webview2.msg"]          = "未检测到 WebView2 运行时。\n\n本程序需要 WebView2 来显示用户界面。\n\nWindows 11 用户：系统已自带，请尝试更新系统或重新安装 WebView2。\n\nWindows 10 用户：请前往以下地址下载安装：\nhttps://developer.microsoft.com/zh-cn/microsoft-edge/webview2/",
            ["read.error"]            = "读取失败",
            ["param.read_error"]      = "无法读取图片参数",
            ["sysinfo.cores"]         = "{0} 核 {1} 线程",
            ["sysinfo.unknown"]       = "未知",
            ["sysinfo.no_gpu"]        = "未检测到独立显卡",
            ["sysinfo.unknown_ram"]   = "未知内存条",
            ["sysinfo.unknown_disk"]  = "未知磁盘",
            ["sysinfo.not_detected"]  = "未检测到",
            ["sysinfo.path_prefix"]   = "路径: {0}",
            ["console.img_generated"] = "[ForgeSide] 🎨 检测到新图片已生成",
            ["console.open_url_err"]  = "[ForgeSide] 无法打开链接",
            ["console.neo_root"]      = "[ForgeSide] NEO 根目录: {0}",
            ["console.launch_fail"]   = "[ForgeSide] ❌ 启动失败: {0}",
            ["console.stopped"]       = "[ForgeSide] 已发送终止信号",
            ["console.open_browser_err"] = "[ForgeSide] 无法打开浏览器",
            ["console.upgrade_to"]    = "[ForgeSide] 开始升级到 {0} …",
            ["console.fetch_tags"]    = "[ForgeSide] 正在连接 GitHub，获取最新版本列表…",
            ["console.fetch_fail"]    = "[ForgeSide] ❌ 获取远程标签失败",
            ["console.fetch_done"]    = "[ForgeSide] ✅ 版本列表获取完成",
            ["console.switching"]     = "[ForgeSide] 正在切换到 {0} …",
            ["console.checkout_fail"] = "[ForgeSide] ❌ 切换版本失败",
            ["console.upgrade_done"]  = "[ForgeSide] ✅ 已切换到 {0}，升级完成！请重启 Forge NEO 使新版本生效。",
            ["console.upgrade_err"]   = "[ForgeSide] ❌ 升级出错：{0}",
            ["proxy.unknown_target"]  = "未知目标: {0}",
            ["upgrade.not_same_ver"]  = "不能在相同版本间切换！",
            ["upgrade.same_ver"]      = "当前版本",
            ["fallback.title"]        = "ForgeSide",
            ["fallback.desc"]         = "找不到 UI 文件 forge_side_ui.html",
            ["fallback.hint"]         = "请将 HTML 文件放在程序同目录下",
        },

        ["zh-TW"] = new()
        {
            ["window.title"]          = "ForgeSide V1.41",
            ["close_dialog.title"]    = "ForgeSide — 確認關閉",
            ["close_dialog.msg"]      = "Forge NEO 程序正在執行中，請先在控制台點擊「終止NEO」按鈕結束程序！",
            ["close_dialog.force"]    = "強制停止",
            ["close_dialog.ok"]       = "確定",
            ["filter.png"]            = "PNG 圖片|*.png|所有檔案|*.*",
            ["filter.image"]          = "圖片|*.png;*.jpg;*.jpeg;*.bmp|所有檔案|*.*",
            ["filter.audio"]          = "音頻文件|*.mp3;*.wav;*.ogg;*.aiff;*.flac;*.m4a|所有檔案|*.*",
            ["filter.exe"]            = "可執行文件|*.exe|所有檔案|*.*",
            ["pick_sound.title"]      = "選擇提示音文件",
            ["pick_folder.gallery"]   = "選擇要監聽的圖片資料夾",
            ["webview2.title"]        = "ForgeSide — 組件缺失",
            ["webview2.msg"]          = "未檢測到 WebView2 執行時。\n\n本程序需要 WebView2 來顯示用戶界面。\n\nWindows 11 用戶：系統已自帶，請嘗試更新系統或重新安裝 WebView2。\n\nWindows 10 用戶：請前往以下地址下載安裝：\nhttps://developer.microsoft.com/zh-cn/microsoft-edge/webview2/",
            ["read.error"]            = "讀取失敗",
            ["param.read_error"]      = "無法讀取圖片參數",
            ["sysinfo.cores"]         = "{0} 核 {1} 執行緒",
            ["sysinfo.unknown"]       = "未知",
            ["sysinfo.no_gpu"]        = "未檢測到獨立顯卡",
            ["sysinfo.unknown_ram"]   = "未知記憶體條",
            ["sysinfo.unknown_disk"]  = "未知磁碟",
            ["sysinfo.not_detected"]  = "未檢測到",
            ["sysinfo.path_prefix"]   = "路徑: {0}",
            ["console.img_generated"] = "[ForgeSide] 🎨 檢測到新圖片已生成",
            ["console.open_url_err"]  = "[ForgeSide] 無法打開連結",
            ["console.neo_root"]      = "[ForgeSide] NEO 根目錄: {0}",
            ["console.launch_fail"]   = "[ForgeSide] ❌ 啟動失敗: {0}",
            ["console.stopped"]       = "[ForgeSide] 已發送終止信號",
            ["console.open_browser_err"] = "[ForgeSide] 無法打開瀏覽器",
            ["console.upgrade_to"]    = "[ForgeSide] 開始升級到 {0} …",
            ["console.fetch_tags"]    = "[ForgeSide] 正在連接 GitHub，獲取最新版本列表…",
            ["console.fetch_fail"]    = "[ForgeSide] ❌ 獲取遠端標籤失敗",
            ["console.fetch_done"]    = "[ForgeSide] ✅ 版本列表獲取完成",
            ["console.switching"]     = "[ForgeSide] 正在切換到 {0} …",
            ["console.checkout_fail"] = "[ForgeSide] ❌ 切換版本失敗",
            ["console.upgrade_done"]  = "[ForgeSide] ✅ 已切換到 {0}，升級完成！請重啟 Forge NEO 使新版本生效。",
            ["console.upgrade_err"]   = "[ForgeSide] ❌ 升級出錯：{0}",
            ["proxy.unknown_target"]  = "未知目標: {0}",
            ["upgrade.not_same_ver"]  = "不能在相同版本間切換！",
            ["upgrade.same_ver"]      = "目前版本",
            ["fallback.title"]        = "ForgeSide",
            ["fallback.desc"]         = "找不到 UI 檔案 forge_side_ui.html",
            ["fallback.hint"]         = "請將 HTML 檔案放在程式同目錄下",
        },

        ["en"] = new()
        {
            ["window.title"]          = "ForgeSide V1.41",
            ["close_dialog.title"]    = "ForgeSide — Confirm Close",
            ["close_dialog.msg"]      = "Forge NEO is still running. Please stop it from the Console tab first!",
            ["close_dialog.force"]    = "Force Stop",
            ["close_dialog.ok"]       = "OK",
            ["filter.png"]            = "PNG Image|*.png|All Files|*.*",
            ["filter.image"]          = "Image|*.png;*.jpg;*.jpeg;*.bmp|All Files|*.*",
            ["filter.audio"]          = "Audio|*.mp3;*.wav;*.ogg;*.aiff;*.flac;*.m4a|All Files|*.*",
            ["filter.exe"]            = "Executable|*.exe|All Files|*.*",
            ["pick_sound.title"]      = "Select Notification Sound",
            ["pick_folder.gallery"]   = "Select Gallery Folder",
            ["webview2.title"]        = "ForgeSide — Missing Component",
            ["webview2.msg"]          = "WebView2 runtime not detected.\n\nForgeSide requires WebView2 to display the UI.\n\nWindows 11 users: It's built-in — try updating Windows or reinstalling WebView2.\n\nWindows 10 users: Download and install from:\nhttps://developer.microsoft.com/en-us/microsoft-edge/webview2/",
            ["read.error"]            = "Read Failed",
            ["param.read_error"]      = "Unable to read image parameters",
            ["sysinfo.cores"]         = "{0} cores / {1} threads",
            ["sysinfo.unknown"]       = "Unknown",
            ["sysinfo.no_gpu"]        = "No dedicated GPU detected",
            ["sysinfo.unknown_ram"]   = "Unknown RAM",
            ["sysinfo.unknown_disk"]  = "Unknown Disk",
            ["sysinfo.not_detected"]  = "Not detected",
            ["sysinfo.path_prefix"]   = "Path: {0}",
            ["console.img_generated"] = "[ForgeSide] 🎨 New image detected",
            ["console.open_url_err"]  = "[ForgeSide] Unable to open link",
            ["console.neo_root"]      = "[ForgeSide] NEO root: {0}",
            ["console.launch_fail"]   = "[ForgeSide] ❌ Launch failed: {0}",
            ["console.stopped"]       = "[ForgeSide] Stop signal sent",
            ["console.open_browser_err"] = "[ForgeSide] Unable to open browser",
            ["console.upgrade_to"]    = "[ForgeSide] Starting upgrade to {0} …",
            ["console.fetch_tags"]    = "[ForgeSide] Connecting to GitHub, fetching releases…",
            ["console.fetch_fail"]    = "[ForgeSide] ❌ Failed to fetch remote tags",
            ["console.fetch_done"]    = "[ForgeSide] ✅ Release list fetched",
            ["console.switching"]     = "[ForgeSide] Switching to {0} …",
            ["console.checkout_fail"] = "[ForgeSide] ❌ Failed to switch version",
            ["console.upgrade_done"]  = "[ForgeSide] ✅ Switched to {0}. Please restart Forge NEO for the changes to take effect.",
            ["console.upgrade_err"]   = "[ForgeSide] ❌ Upgrade error: {0}",
            ["proxy.unknown_target"]  = "Unknown target: {0}",
            ["upgrade.not_same_ver"]  = "Cannot switch to the same version!",
            ["upgrade.same_ver"]      = "Current Version",
            ["fallback.title"]        = "ForgeSide",
            ["fallback.desc"]         = "UI file forge_side_ui.html not found",
            ["fallback.hint"]         = "Please place the HTML file in the same directory as the program",
        },

        ["ja"] = new()
        {
            ["window.title"]          = "ForgeSide V1.41",
            ["close_dialog.title"]    = "ForgeSide — 終了確認",
            ["close_dialog.msg"]      = "Forge NEO が実行中です。先にコンソールタブで「NEO停止」ボタンをクリックしてください！",
            ["close_dialog.force"]    = "強制終了",
            ["close_dialog.ok"]       = "OK",
            ["filter.png"]            = "PNG画像|*.png|すべてのファイル|*.*",
            ["filter.image"]          = "画像|*.png;*.jpg;*.jpeg;*.bmp|すべてのファイル|*.*",
            ["filter.audio"]          = "音声ファイル|*.mp3;*.wav;*.ogg;*.aiff;*.flac;*.m4a|すべてのファイル|*.*",
            ["filter.exe"]            = "実行ファイル|*.exe|すべてのファイル|*.*",
            ["pick_sound.title"]      = "通知音ファイルを選択",
            ["pick_folder.gallery"]   = "ギャラリーフォルダを選択",
            ["webview2.title"]        = "ForgeSide — コンポーネント不足",
            ["webview2.msg"]          = "WebView2 ランタイムが見つかりません。\n\n本プログラムは UI 表示に WebView2 を必要とします。\n\nWindows 11 ユーザー：システムに内蔵されています。Windows 更新または WebView2 の再インストールをお試しください。\n\nWindows 10 ユーザー：以下からダウンロードしてインストールしてください：\nhttps://developer.microsoft.com/ja-jp/microsoft-edge/webview2/",
            ["read.error"]            = "読み取り失敗",
            ["param.read_error"]      = "画像パラメータを読み取れません",
            ["sysinfo.cores"]         = "{0} コア / {1} スレッド",
            ["sysinfo.unknown"]       = "不明",
            ["sysinfo.no_gpu"]        = "GPU が検出されていません",
            ["sysinfo.unknown_ram"]   = "不明なメモリ",
            ["sysinfo.unknown_disk"]  = "不明なディスク",
            ["sysinfo.not_detected"]  = "未検出",
            ["sysinfo.path_prefix"]   = "パス: {0}",
            ["console.img_generated"] = "[ForgeSide] 🎨 新しい画像を検出しました",
            ["console.open_url_err"]  = "[ForgeSide] リンクを開けません",
            ["console.neo_root"]      = "[ForgeSide] NEO ルート: {0}",
            ["console.launch_fail"]   = "[ForgeSide] ❌ 起動失敗: {0}",
            ["console.stopped"]       = "[ForgeSide] 停止信号を送信しました",
            ["console.open_browser_err"] = "[ForgeSide] ブラウザを開けません",
            ["console.upgrade_to"]    = "[ForgeSide] {0} へのアップグレードを開始…",
            ["console.fetch_tags"]    = "[ForgeSide] GitHub に接続中、リリース一覧を取得しています…",
            ["console.fetch_fail"]    = "[ForgeSide] ❌ リモートタグの取得に失敗しました",
            ["console.fetch_done"]    = "[ForgeSide] ✅ リリース一覧を取得しました",
            ["console.switching"]     = "[ForgeSide] {0} に切り替え中…",
            ["console.checkout_fail"] = "[ForgeSide] ❌ バージョン切替に失敗しました",
            ["console.upgrade_done"]  = "[ForgeSide] ✅ {0} に切り替えました。変更を反映するには Forge NEO を再起動してください。",
            ["console.upgrade_err"]   = "[ForgeSide] ❌ アップグレードエラー: {0}",
            ["proxy.unknown_target"]  = "不明なターゲット: {0}",
            ["upgrade.not_same_ver"]  = "同じバージョンには切り替えられません！",
            ["upgrade.same_ver"]      = "現在のバージョン",
            ["fallback.title"]        = "ForgeSide",
            ["fallback.desc"]         = "UI ファイル forge_side_ui.html が見つかりません",
            ["fallback.hint"]         = "HTML ファイルをプログラムと同じディレクトリに配置してください",
        },
    };

    public static void SetLanguage(string lang)
    {
        _current = Data.ContainsKey(lang) ? lang : "en";
    }

    public static string T(string key, params object?[] args)
    {
        if (Data.TryGetValue(_current, out var dict) && dict.TryGetValue(key, out var val))
            return args.Length > 0 ? string.Format(val, args) : val;
        if (Data.TryGetValue("en", out var enDict) && enDict.TryGetValue(key, out var enVal))
            return args.Length > 0 ? string.Format(enVal, args) : enVal;
        return key;
    }

    public static string DetectSystemLanguage()
    {
        var ui = CultureInfo.CurrentUICulture;
        var name = ui.Name;
        if (name.StartsWith("zh-CN") || name.StartsWith("zh-Hans") || name.Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            if (name.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("zh-MO", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("zh-TW", StringComparison.OrdinalIgnoreCase))
                return "zh-TW";
            return "zh-CN";
        }
        if (name.StartsWith("zh-TW") || name.StartsWith("zh-HK") || name.StartsWith("zh-MO") || name.StartsWith("zh-Hant"))
            return "zh-TW";
        if (name.StartsWith("ja"))
            return "ja";
        return "en";
    }
}
