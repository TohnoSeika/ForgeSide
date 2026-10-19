╔══════════════════════════════════════════════════╗
║         ForgeSide — 安装说明                      ║
║         Forge NEO 辅助启动工具                     ║
╚══════════════════════════════════════════════════╝

📋 概述
────────────────────────────────────────
ForgeSide 是 Forge NEO 的辅助启动工具，提供图形化界面来
管理和启动 Forge NEO，包含控制台输出、图库浏览、版本管理等
功能。


🔧 系统要求
────────────────────────────────────────
1. 操作系统：仅支持 Windows 10 和 Windows 11（64 位）
   不支持 Windows 7 / 8 / 8.1，不支持 Linux 和 macOS

2. .NET 10 Desktop Runtime（必须安装，否则无法运行）
   下载地址：
   https://dotnet.microsoft.com/download/dotnet/10.0

   或直接搜索「.NET 10 Desktop Runtime」下载

3. WebView2 运行时（用于渲染程序界面）
   • Windows 11 自带，无需额外安装
   • Windows 10 可能需要手动安装
   下载地址：
   https://developer.microsoft.com/zh-cn/microsoft-edge/webview2/

   或直接搜索「WebView2 Runtime」下载


📦 安装步骤
────────────────────────────────────────
1. 安装 .NET 10 Desktop Runtime（如果已安装请跳过）
   下载后双击安装即可，很简单

2. 将 ForgeSide 文件夹完整解压到任意位置
   建议解压到 Forge NEO 根目录下，例如：
   └── sd-webui-forge-neo/
       ├── webui/
       ├── ...（其他文件）
       └── ForgeSide/          ← 解压到这里
           ├── ForgeSide.exe
           ├── forge_side_ui.html
           └── ...（其他文件）


🚀 运行方式
────────────────────────────────────────
双击 ForgeSide 文件夹里的 ForgeSide.exe 即可。

首次启动可能稍慢（WebView2 初始化），之后会很快。


📝 注意事项
────────────────────────────────────────
• ForgeSide 需要在 Forge NEO 根目录附近才能正确定位
  NEO 的安装位置，建议放在 NEO 根目录的子文件夹中
• 本软件是免费软件，不会收取任何费用
• 本软件以 MIT 许可证开源，可以自由使用、修改和分发
  许可证全文见同目录下的 LICENSE 文件
• 如果有问题或反馈，请联系开发者

   Bilibili：https://space.bilibili.com/14816
   GitHub：   https://github.com/TohnoSeika

