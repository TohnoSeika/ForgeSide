<#
═══════════════════════════════════════════════
  ForgeSide 编译 + 同步脚本
  这是桃华帮 Minagi 写的哦～ 🎀
  编译源码并同步到三个位置：两个运行目录 + dist
═══════════════════════════════════════════════
#>

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$BuildOut   = Join-Path $ScriptRoot "bin\Debug\net10.0-windows"

$Targets = @(
    "F:\Programmes\AICreater\novelai-webui-aki\sd-webui-forge-neo\sd-webui-forge-neo\ForgeSide",
    "F:\Programmes\AICreater\novelai-webui-aki\sd-webui-forge-neo-offcial\ForgeSide",
    "F:\Programmes\AICreater\novelai-webui-aki\sd-webui-forge-neo-licyk\sd_webui_forge_neo-licyk\ForgeSide",
    (Join-Path $ScriptRoot "dist\ForgeSide")
)

$Files = @(
    "ForgeSide.dll",
    "ForgeSide.exe",
    "ForgeSide.pdb",
    "ForgeSide.deps.json",
    "ForgeSide.runtimeconfig.json",
    "forge_side_ui.html",
    "README.txt"
)

Write-Host "🌸 桃华开始编译 ForgeSide …" -ForegroundColor Magenta

# 步骤 1：编译
Set-Location $ScriptRoot
dotnet build
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ 编译失败，同步中止" -ForegroundColor Red
    exit 1
}
Write-Host "✅ 编译成功`n" -ForegroundColor Green

# 步骤 2：同步到三个目录
Write-Host "📦 开始同步到运行目录和 dist …" -ForegroundColor Cyan

foreach ($dst in $Targets) {
    $name = Split-Path $dst -Leaf
    Write-Host "  → $name" -ForegroundColor Yellow
    foreach ($file in $Files) {
        $srcFile = Join-Path $BuildOut $file
        $dstFile = Join-Path $dst $file
        try {
            Copy-Item -Path $srcFile -Destination $dstFile -Force
            Write-Host "    ✓ $file" -ForegroundColor Green
        } catch {
            Write-Host "    ⚠ $file — 文件被占用，跳过（$_）" -ForegroundColor Red
        }
    }
}

Write-Host "`n✨ 全部搞定！桃华的任务完成啦～" -ForegroundColor Magenta
