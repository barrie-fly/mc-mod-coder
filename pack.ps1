<#
.SYNOPSIS
    MC模组代码工作台 — 一键打包发布脚本

.DESCRIPTION
    自动完成完整打包流程：
      [1/5] 环境检查（dotnet SDK 探测、项目文件、zip 组件、运行中进程）
      [2/5] 参数验证（版本号格式、输出目录）
      [3/5] dotnet publish 发布（自包含版 / 框架依赖版，可选用）
      [4/5] 启动冒烟测试（自包含版模拟目标机环境验证"双击即用"）
      [5/5] zip 封包 + 完整性校验
    全程写入日志：publish\logs\pack_v<版本>_<时间戳>.log

.PARAMETER Version
    包版本号，默认 1.0.0。格式 Major.Minor.Patch[-后缀]，如 1.0.0、1.2.0-beta1

.PARAMETER Packages
    打包内容：All（默认，双包）/ SelfContained（仅自包含）/ FrameworkDependent（仅框架依赖）

.PARAMETER Configuration
    编译配置，默认 Release

.PARAMETER Runtime
    目标运行时标识（RID），默认 win-x64

.PARAMETER OutputDir
    输出根目录，默认 <脚本目录>\publish

.PARAMETER DotNetPath
    dotnet.exe 全路径。默认自动探测：先查 PATH，再查 D:\dotnet\dotnet.exe

.PARAMETER SkipZip
    跳过 zip 封包（仅保留目录形态产物）

.PARAMETER SkipSmokeTest
    跳过启动冒烟测试

.PARAMETER Force
    跳过所有交互确认，无人值守模式（CI 适用）

.EXAMPLE
    .\pack.ps1                                    # 默认：v1.0.0 双包 + 冒烟 + zip
.EXAMPLE
    .\pack.ps1 -Version 1.1.0 -Force              # 指定版本，无人值守
.EXAMPLE
    .\pack.ps1 -Packages SelfContained -SkipZip   # 仅发布自包含目录

.NOTES
    若执行策略禁止运行脚本，请使用：
      powershell -ExecutionPolicy Bypass -File .\pack.ps1
#>

param(
    [string]$Version = "1.0.0",
    [ValidateSet("All", "SelfContained", "FrameworkDependent")]
    [string]$Packages = "All",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "",
    [string]$DotNetPath = "",
    [switch]$SkipZip,
    [switch]$SkipSmokeTest,
    [switch]$Force,
    [int]$SmokeWaitSeconds = 8
)

$ErrorActionPreference = "Stop"
$script:Failed = $false

# ============================================================
# 工具函数
# ============================================================

# 写日志：带时间戳写入日志文件，并按级别着色输出到控制台
function Write-Log {
    param(
        [string]$Message,
        [ValidateSet("Info", "OK", "Warn", "Error")]
        [string]$Level = "Info",
        [switch]$LogOnly
    )
    $line = "[{0}][{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Level, $Message
    if ($script:LogFile) { Add-Content -Path $script:LogFile -Value $line -Encoding UTF8 }
    if (-not $LogOnly) {
        $color = switch ($Level) {
            "OK"    { "Green" }
            "Warn"  { "Yellow" }
            "Error" { "Red" }
            default { "Gray" }
        }
        Write-Host $line -ForegroundColor $color
    }
}

function Write-Step {
    param([string]$Text)
    Write-Host ""
    Write-Host ("======== {0} ========" -f $Text) -ForegroundColor Cyan
    Write-Log "---- $Text ----" -LogOnly
}

function Get-SizeMB {
    param([string]$Path)
    if (Test-Path $Path -PathType Leaf) { return [math]::Round((Get-Item $Path).Length / 1MB, 1) }
    if (Test-Path $Path) {
        $bytes = (Get-ChildItem $Path -Recurse -File | Measure-Object -Property Length -Sum).Sum
        return [math]::Round($bytes / 1MB, 1)
    }
    return 0
}

# ============================================================
# 初始化：路径解析 + 日志文件
# ============================================================

$ProjectRoot = $PSScriptRoot
if (-not $ProjectRoot) { $ProjectRoot = (Get-Location).Path }
$ProjFile = Join-Path $ProjectRoot "src\MCModCoder.App\MCModCoder.App.csproj"

if (-not $OutputDir) { $OutputDir = Join-Path $ProjectRoot "publish" }
$ScDir = Join-Path $OutputDir "self-contained"        # 自包含版目录
$FdDir = Join-Path $OutputDir "framework-dependent"   # 框架依赖版目录
$LogDir = Join-Path $OutputDir "logs"

try { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null } catch { }
$script:LogFile = Join-Path $LogDir ("pack_v{0}_{1}.log" -f $Version, (Get-Date -Format "yyyyMMdd_HHmmss"))

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║        MC模组代码工作台 — 自动打包 v$Version        ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Log "打包启动。配置：Packages=$Packages Config=$Configuration Runtime=$Runtime 输出=$OutputDir"

try {
    # ============================================================
    # [1/5] 环境检查
    # ============================================================
    Write-Step "[1/5] 环境检查"

    # --- 1a. 探测 dotnet.exe：参数指定 → PATH → 常见约定路径（D:\dotnet、用户目录 dotnet） ---
    if (-not $DotNetPath) {
        $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($cmd) { $DotNetPath = $cmd.Source }
        elseif (Test-Path "D:\dotnet\dotnet.exe") { $DotNetPath = "D:\dotnet\dotnet.exe" }
        elseif (Test-Path (Join-Path $env:USERPROFILE "dotnet\dotnet.exe")) { $DotNetPath = Join-Path $env:USERPROFILE "dotnet\dotnet.exe" }
    }
    if (-not $DotNetPath -or -not (Test-Path $DotNetPath)) {
        throw "未找到 dotnet.exe。请用 -DotNetPath 指定全路径（本机已知位置：D:\dotnet\dotnet.exe 或 %USERPROFILE%\dotnet\dotnet.exe）。"
    }
    $DotNetRoot = Split-Path $DotNetPath -Parent
    Write-Log "dotnet 路径：$DotNetPath" "OK"

    # --- 1b. 校验 .NET SDK（≥ 8 即可：高版本 SDK 可构建/发布 net8.0 目标，
    #          所需 targeting pack / runtime pack 会经 NuGet 还原，本机缓存已备 8.0.31） ---
    $sdks = & $DotNetPath --list-sdks 2>&1
    if ($LASTEXITCODE -ne 0) { throw "dotnet --list-sdks 执行失败，SDK 可能损坏。" }
    $sdkMajors = @(foreach ($s in @($sdks)) {
        if ($s -match "^(\d+)\.") { [int]$Matches[1] }
    })
    $hasSdk8Plus = @($sdkMajors | Where-Object { $_ -ge 8 }).Count -gt 0
    if (-not $hasSdk8Plus) {
        throw "未检测到 .NET 8 及以上 SDK（当前 SDK：$(@($sdks) -join '; ')）。请安装 .NET 8 SDK 或更高版本。"
    }
    Write-Log ".NET SDK 检测通过（≥ 8）：$(@($sdkMajors) -join ', ')" "OK"

    # --- 1c. 项目文件存在性 ---
    if (-not (Test-Path $ProjFile)) { throw "项目文件不存在：$ProjFile" }
    Write-Log "项目文件：$ProjFile" "OK"

    # --- 1d. zip 组件（Windows PowerShell 5.1 需显式加载） ---
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Write-Log "zip 打包组件加载成功" "OK"

    # --- 1e. 运行中的旧实例会占用发布文件，须先处理 ---
    $running = @(Get-Process -Name "MCModCoder" -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Write-Log "检测到 $($running.Count) 个 MCModCoder.exe 正在运行（PID: $(($running | ForEach-Object Id) -join ', ')），发布将因文件占用失败。" "Warn"
        $kill = $Force
        if (-not $kill) {
            try { $ans = Read-Host "是否结束这些进程？(Y/n)" } catch { $ans = "y" }
            $kill = ($ans -notmatch "^[nN]")
        }
        if ($kill) {
            $running | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 1
            Write-Log "已结束全部旧实例" "OK"
        } else {
            throw "用户选择保留运行中的实例，无法继续打包。"
        }
    } else {
        Write-Log "无占用文件的运行实例" "OK"
    }

    # ============================================================
    # [2/5] 参数验证
    # ============================================================
    Write-Step "[2/5] 参数验证"

    # --- 2a. 版本号格式 ---
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.\-]+)?$') {
        throw "版本号「$Version」格式不合法。应为 Major.Minor.Patch[-后缀]，例如 1.0.0 或 1.2.0-beta1。"
    }
    Write-Log "版本号：v$Version（格式合法）" "OK"

    # --- 2b. 输出目录（已有产物时确认覆盖） ---
    $existing = @()
    foreach ($d in @($ScDir, $FdDir)) {
        if ((Test-Path $d) -and @(Get-ChildItem $d -ErrorAction SilentlyContinue).Count -gt 0) { $existing += $d }
    }
    if ($existing.Count -gt 0 -and -not $Force) {
        Write-Log "输出目录已有旧产物：$($existing -join '；')" "Warn"
        try { $ans = Read-Host "将清空旧产物后重新发布，是否继续？(Y/n)" } catch { $ans = "y" }
        if ($ans -match "^[nN]") { Write-Log "用户取消打包。" "Warn"; exit 0 }
    }
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    Write-Log "输出目录：$OutputDir" "OK"

    # ============================================================
    # [3/5] 发布
    # ============================================================
    Write-Step "[3/5] dotnet publish 发布"

    function Invoke-Publish {
        param([string]$Mode)   # SelfContained / FrameworkDependent
        $outDir = if ($Mode -eq "SelfContained") { $ScDir } else { $FdDir }
        $selfContained = if ($Mode -eq "SelfContained") { "true" } else { "false" }

        if ($Packages -ne "All" -and $Packages -ne $Mode) { return }

        Write-Log "正在发布 $Mode（--self-contained $selfContained）→ $outDir ..."
        # 清空旧产物，避免残留文件混入本次包
        if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
        New-Item -ItemType Directory -Path $outDir -Force | Out-Null

        # 发布并完整记录输出到日志；控制台保持简洁
        $out = & $DotNetPath publish $ProjFile -c $Configuration -r $Runtime `
            --self-contained $selfContained -o $outDir --nologo -v q 2>&1
        $out | ForEach-Object { Add-Content -Path $script:LogFile -Value "    [dotnet] $_" -Encoding UTF8 }
        if ($LASTEXITCODE -ne 0) {
            # 失败时把关键错误行回显到控制台
            $errs = @($out | Where-Object { $_ -match "error|错误" } | Select-Object -First 10)
            if ($errs.Count -gt 0) { $errs | ForEach-Object { Write-Log $_ "Error" } }
            throw "$Mode 发布失败（dotnet 退出码 $LASTEXITCODE），详见日志：$script:LogFile"
        }
        $n = @(Get-ChildItem $outDir -Recurse -File).Count
        Write-Log ("{0} 发布完成：{1} 个文件，{2} MB" -f $Mode, $n, (Get-SizeMB $outDir)) "OK"
    }

    Invoke-Publish "SelfContained"
    Invoke-Publish "FrameworkDependent"

    # ============================================================
    # [4/5] 启动冒烟测试
    # ============================================================
    if (-not $SkipSmokeTest) {
        Write-Step "[4/5] 启动冒烟测试（等待 $SmokeWaitSeconds 秒验证进程存活）"

        function Invoke-SmokeTest {
            param([string]$ExePath, [bool]$NeedRuntimeRoot, [string]$Mode)

            if (-not (Test-Path $ExePath)) { throw "冒烟测试失败：未找到 $ExePath" }

            # 自包含版故意清掉 DOTNET_ROOT，模拟目标机"双击即用"；
            # 框架依赖版必须设置 DOTNET_ROOT 才能定位 hostfxr（本机运行时在 D:\dotnet）
            $oldRoot = $env:DOTNET_ROOT
            if ($NeedRuntimeRoot) { $env:DOTNET_ROOT = $DotNetRoot }
            else { Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue }
            try {
                # --- [a] UI 冒烟：--smoke-ui 逐个实例化全部页面与主窗口后自动退出。
                #     必须做这一步：全局异常兜底会把启动期异常"恢复运行"，
                #     仅靠进程存活检查发现不了页面级 BAML/资源错误（历史事故：坏图标 0x88982F60）。
                $uiResult = Join-Path $LogDir ("uismoke_{0}.txt" -f $Mode)
                $ui = Start-Process -FilePath $ExePath -ArgumentList "--smoke-ui", "`"$uiResult`"" -PassThru -Wait
                $uiOut = if (Test-Path $uiResult) { Get-Content $uiResult -Raw -Encoding UTF8 } else { "" }
                if ($ui.ExitCode -ne 0 -or $uiOut -match "FAIL|冒烟流程自身异常") {
                    foreach ($l in ($uiOut -split "`n")) { Write-Log $l.TrimEnd() "Error" -LogOnly }
                    throw "$Mode UI 冒烟测试失败：存在实例化失败的页面，详见日志：$script:LogFile"
                }
                Write-Log "$Mode UI 冒烟测试通过（全部页面与主窗口实例化成功）" "OK"

                # --- [b] 存活冒烟：正常启动等待 N 秒验证进程不退出 ---
                # 启动前记录崩溃日志时间戳，结束后检查是否新增（新增即视为启动期异常）
                $errDir = Join-Path $env:APPDATA "MCModCoder\logs"
                $before = @{}
                if (Test-Path $errDir) {
                    Get-ChildItem $errDir -Filter "error-*.log" -ErrorAction SilentlyContinue |
                        ForEach-Object { $before[$_.Name] = $_.LastWriteTimeUtc.Ticks }
                }

                $p = Start-Process -FilePath $ExePath -PassThru
                Start-Sleep -Seconds $SmokeWaitSeconds
                if ($p.HasExited) {
                    throw "$Mode 冒烟测试失败：启动后退出（退出码 $($p.ExitCode)）。0x80008083 通常表示缺少 .NET 运行时。"
                }
                Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                Write-Log "$Mode 冒烟测试通过（进程存活 $SmokeWaitSeconds 秒，PID $($p.Id)）" "OK"

                Start-Sleep -Seconds 1   # 等待崩溃日志落盘
                if (Test-Path $errDir) {
                    $newErrs = @(Get-ChildItem $errDir -Filter "error-*.log" -ErrorAction SilentlyContinue | Where-Object {
                        (-not $before.ContainsKey($_.Name)) -or ($_.LastWriteTimeUtc.Ticks -gt $before[$_.Name])
                    })
                    if ($newErrs.Count -gt 0) {
                        $tail = ($newErrs | ForEach-Object { Get-Content $_.FullName -Tail 30 }) -join "`n"
                        Write-Log $tail "Error" -LogOnly
                        throw "$Mode 冒烟测试失败：启动期间新增崩溃日志（$($newErrs.Name -join ', ')），详见日志：$script:LogFile"
                    }
                }
            } finally {
                # 恢复调用方原本的 DOTNET_ROOT，避免污染当前会话
                if ($null -ne $oldRoot) { $env:DOTNET_ROOT = $oldRoot }
                else { Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue }
            }
        }

        if (Test-Path (Join-Path $ScDir "MCModCoder.exe")) {
            Invoke-SmokeTest (Join-Path $ScDir "MCModCoder.exe") $false "自包含版"
        }
        if (Test-Path (Join-Path $FdDir "MCModCoder.exe")) {
            Invoke-SmokeTest (Join-Path $FdDir "MCModCoder.exe") $true "框架依赖版"
        }
    } else {
        Write-Log "已跳过冒烟测试（-SkipSmokeTest）" "Warn"
    }

    # ============================================================
    # [5/5] zip 封包
    # ============================================================
    if (-not $SkipZip) {
        Write-Step "[5/5] zip 封包"

        function New-ZipPackage {
            param([string]$SourceDir, [string]$ZipPath, [string]$Label)
            if (-not (Test-Path $SourceDir)) { throw "封包失败：源目录不存在 $SourceDir" }

            # 同名旧包：交互确认（无人值守模式下直接覆盖）
            if ((Test-Path $ZipPath) -and -not $Force) {
                try { $ans = Read-Host "已存在 $(Split-Path $ZipPath -Leaf)，覆盖？(Y/n)" } catch { $ans = "y" }
                if ($ans -match "^[nN]") { Write-Log "跳过 $Label 封包。" "Warn"; return }
            }
            if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }

            Write-Log "正在封包 $Label → $ZipPath ..."
            [System.IO.Compression.ZipFile]::CreateFromDirectory(
                $SourceDir, $ZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

            # 完整性校验：重新打开 zip 并统计条目
            $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
            $entries = $zip.Entries.Count
            $zip.Dispose()
            if ($entries -le 0) { throw "$Label zip 完整性校验失败（0 条目）。" }
            Write-Log ("{0} 封包完成：{1} 条目，{2} MB" -f $Label, $entries, (Get-SizeMB $ZipPath)) "OK"
        }

        New-ZipPackage $ScDir (Join-Path $OutputDir "MCModCoder-$Version-$Runtime-selfcontained.zip") "自包含版"
        New-ZipPackage $FdDir (Join-Path $OutputDir "MCModCoder-$Version-$Runtime.zip") "框架依赖版"
    } else {
        Write-Log "已跳过 zip 封包（-SkipZip）" "Warn"
    }

    # ============================================================
    # 结果汇总
    # ============================================================
    Write-Host ""
    Write-Host "██████████████████████████████████████████████████" -ForegroundColor Green
    Write-Host "██  打包成功！版本 v$Version" -ForegroundColor Green
    Write-Host "██████████████████████████████████████████████████" -ForegroundColor Green
    Write-Log "打包成功。" "OK"

    Write-Host ""
    Write-Host "产物清单：" -ForegroundColor White
    foreach ($item in @(
        @{ Path = $ScDir; Label = "自包含版目录（双击即用，无需安装运行时）" },
        @{ Path = $FdDir; Label = "框架依赖版目录（需目标机安装 .NET 8 Desktop Runtime）" },
        @{ Path = Join-Path $OutputDir "MCModCoder-$Version-$Runtime-selfcontained.zip"; Label = "自包含版 zip 包" },
        @{ Path = Join-Path $OutputDir "MCModCoder-$Version-$Runtime.zip"; Label = "框架依赖版 zip 包" }
    )) {
        if (Test-Path $item.Path) {
            Write-Host ("  • {0}" -f $item.Label) -ForegroundColor White
            Write-Host ("    {0}  （{1} MB）" -f $item.Path, (Get-SizeMB $item.Path)) -ForegroundColor Gray
        }
    }
    Write-Host ""
    Write-Host "打包日志：$script:LogFile" -ForegroundColor Gray
    exit 0
}
catch {
    $script:Failed = $true
    Write-Host ""
    Write-Host "██████████████████████████████████████████████████" -ForegroundColor Red
    Write-Host "██  打包失败" -ForegroundColor Red
    Write-Host "██████████████████████████████████████████████████" -ForegroundColor Red
    Write-Log $_.Exception.Message "Error"
    if ($_.Exception.StackTrace) { Write-Log $_.Exception.StackTrace "Error" -LogOnly }
    Write-Host ""
    Write-Host "错误详情已写入日志：$script:LogFile" -ForegroundColor Yellow
    exit 1
}
