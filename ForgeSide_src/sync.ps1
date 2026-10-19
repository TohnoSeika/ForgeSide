<#
═══════════════════════════════════════════════
  ForgeSide 编译 + 同步脚本
  这是桃华帮 Minagi 写的哦～ 🎀

  用法：
    .\sync.ps1                              # 编译，只同步到 dist
    .\sync.ps1 -RunDirs "D:\ForgeSide"      # 再同步一份到运行目录
    .\sync.ps1 -RunDirs @("D:\A\ForgeSide", "D:\B\ForgeSide")
═══════════════════════════════════════════════
#>

param(
    [string[]]$RunDirs = @()
)

# 让失败真的变成失败。
# 不加这一行的话，Copy-Item 失败只会写一行红字就过去了：脚本照打绿勾、
# 照报「全部搞定」、退出码还是 0。目标被 ForgeSide.exe 占着、盘符没挂、
# 目录没有写权限 —— 三种情况从输出上完全看不出来。
# （Copy-Item 抛的是 non-terminating error，try/catch 接不住它，
#   实测：路径不存在时红字报错，紧跟着的代码照常执行，$LASTEXITCODE 是空的）
$ErrorActionPreference = 'Stop'

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$BuildOut   = Join-Path $ScriptRoot "bin\Debug\net10.0-windows"

# 运行目录由调用者传入，不再写死本机路径。
# dist 是公开产物的落点，永远排在最后。
$Targets = @(
    foreach ($runDir in $RunDirs) {
        @{ Path = $runDir; Full = $true }
    }
    @{ Path = (Join-Path $ScriptRoot "dist\ForgeSide"); Full = $false }
)

$CoreFiles = @(
    "ForgeSide.dll", "ForgeSide.exe", "ForgeSide.pdb",
    "ForgeSide.deps.json", "ForgeSide.runtimeconfig.json",
    "forge_side_ui.html", "README.txt"
)

# 这些不是「程序文件」，是程序跑起来之后自己写在 exe 旁边的。
# 完整同步原本会把它们一起盖过去 —— 用户在运行目录里设好的代理地址、
# 图库文件夹、背景图、语言，每同步一次就被开发机上的那份冲掉一次，
# 而且一声不响。
# 要排除的是「源里的它们」：目标里那份是用户的东西，动不得
$UserDataNames = @(
    "forgeside.settings.json",   # 配置
    ".thumbcache",               # 缩略图缓存
    "outputs",                   # 出图目录
    "*.WebView2"                 # WebView2 浏览器缓存，几十 MB
)

function Test-IsUserData([string]$name) {
    foreach ($pattern in $UserDataNames) {
        if ($name -like $pattern) { return $true }
    }
    return $false
}

Write-Host "🌸 桃华开始编译 ForgeSide …" -ForegroundColor Magenta

# 步骤 1：编译
Set-Location $ScriptRoot
dotnet build
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ 编译失败，同步中止" -ForegroundColor Red
    exit 1
}
Write-Host "✅ 编译成功`n" -ForegroundColor Green

# 步骤 2：同步
Write-Host "📦 开始同步 …" -ForegroundColor Cyan

$failedTargets = @()

foreach ($t in $Targets) {
    $dst  = $t.Path
    $name = Split-Path $dst -Leaf
    Write-Host "  → $name" -ForegroundColor Yellow

    try {
        if (-not (Test-Path $dst)) {
            New-Item -ItemType Directory -Path $dst -Force | Out-Null
        }

        if ($t.Full) {
            # 完整同步：DLL、runtimes 等全部带上，只把用户数据留在原地
            $programItems = Get-ChildItem -Path $BuildOut -Force |
                            Where-Object { -not (Test-IsUserData $_.Name) }
            foreach ($item in $programItems) {
                Copy-Item -Path $item.FullName -Destination $dst -Recurse -Force
            }
            Write-Host "    ✓ 完整同步（程序文件，跳过用户数据）" -ForegroundColor Green
        } else {
            # 精简同步：只复制核心文件
            foreach ($file in $CoreFiles) {
                Copy-Item -Path (Join-Path $BuildOut $file) -Destination (Join-Path $dst $file) -Force
                Write-Host "    ✓ $file" -ForegroundColor Green
            }
        }
    } catch {
        $failedTargets += $name
        Write-Host "    ⚠ 同步失败：$($_.Exception.Message)" -ForegroundColor Red
    }
}

if ($failedTargets.Count -gt 0) {
    Write-Host "`n❌ $($failedTargets -join '、') 没同步成功，别当它成了" -ForegroundColor Red
    exit 1
}

Write-Host "`n✨ 全部搞定！桃华的任务完成啦～" -ForegroundColor Magenta
