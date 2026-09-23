#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Version = '',
    # 只想快速出 zip 时用
    [switch]$NoInstaller,
    [string]$IsccPath = '',
    [switch]$SelfTest,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\common.ps1')

$outRoot = Join-Path $ParaDeskRoot 'dist'

function Get-ProjectVersion {
    [xml]$xml = Read-ParaDeskText -Path $ParaDeskProject
    foreach ($pg in @($xml.Project.PropertyGroup)) {
        $v = $pg.Version
        if ($v -is [System.Xml.XmlElement]) { $v = $v.InnerText }
        if ($v) { return ([string]$v).Trim() }
    }
    return ''
}

$ver = $Version.Trim()
if (-not $ver) {
    $ver = Get-ProjectVersion
    if (-not $ver) { throw "csproj 里没有 <Version>，请用 -Version 指定版本号: $ParaDeskProject" }
}
if ($ver -match '^[vV]\d') { $ver = $ver.Substring(1) }
if ($ver -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw ("版本号格式不对: '$ver'（应为 1.2.3 这样的三段数字，不带前导零。" +
           "程序只报告三段版本号，四段式或 -beta 这类后缀会让更新检查误判）")
}

$dotnet = Resolve-Dotnet
$binDir = Get-ParaDeskOutputDir -Configuration Release
$exe = Get-ParaDeskExe -Configuration Release
$stage = Join-Path $outRoot "ParaDesk-$ver"

Stop-ParaDeskIfRunning -OutputDir $binDir -Force:$Force
Stop-ParaDeskIfRunning -OutputDir $stage -Force:$Force

Write-Host "构建 Release $ver ..." -ForegroundColor Cyan
$buildArgs = @($ParaDeskProject, '-c', 'Release', '-v', 'minimal', '-nologo', '--no-incremental', "-p:Version=$ver")
& $dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) { throw "构建失败 ($LASTEXITCODE)" }
if (-not (Test-Path -LiteralPath $exe)) { throw "找不到产物: $exe" }

$asmVersion = [Reflection.AssemblyName]::GetAssemblyName($exe).Version
$asmVer = if ($asmVersion) { $asmVersion.ToString(3) } else { '' }
if ($asmVer -ne $ver) {
    throw "产物的程序集版本是 '$asmVer'，与要打包的 '$ver' 不一致（版本号多半没有传给构建），已停止打包。"
}
Write-Host "版本: $ver" -ForegroundColor Green

if ($SelfTest) {
    Write-Host ''
    Write-Host '打包前自检（selftest -Quick，Release 产物）...' -ForegroundColor Cyan
    & (Join-Path $ParaDeskRoot 'selftest.ps1') -Exe $exe -Quick
    if ($LASTEXITCODE -ne 0) { throw "自检未通过 (exit $LASTEXITCODE)，已停止打包。" }
}

if (Test-Path -LiteralPath $stage) {
    try { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction Stop }
    catch {
        throw (("删除上一次的发布目录失败，里面有文件被占用（资源管理器预览窗格、杀毒软件、当前目录在里面的终端等）：{0}`n" +
                "  关闭占用它的程序后重试。原始错误：{1}") -f $stage, $_.Exception.Message)
    }
}
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$includeExt = @('.exe', '.dll', '.config')
$excludeExt = @('.pdb', '.xml')
$binFull = (Resolve-Path -LiteralPath $binDir).ProviderPath.TrimEnd('\')
$packaged = New-Object System.Collections.Generic.List[string]
$excluded = New-Object System.Collections.Generic.List[string]
$notPackaged = New-Object System.Collections.Generic.List[string]

foreach ($f in @(Get-ChildItem -LiteralPath $binFull -Recurse -File)) {
    $rel = $f.FullName.Substring($binFull.Length + 1)
    $ext = $f.Extension.ToLowerInvariant()
    if ($includeExt -contains $ext) {
        $dest = Join-Path $stage $rel
        $destDir = Split-Path -Parent $dest
        if (-not (Test-Path -LiteralPath $destDir)) { New-Item -ItemType Directory -Force -Path $destDir | Out-Null }
        Copy-Item -LiteralPath $f.FullName -Destination $dest
        $packaged.Add($rel)
    }
    elseif ($excludeExt -contains $ext) { $excluded.Add($rel) }
    else { $notPackaged.Add($rel) }
}

foreach ($must in @('ParaDesk.exe', 'Wpf.Ui.dll')) {
    if ($packaged -notcontains $must) { throw "输出目录里缺少 $must，无法打包: $binFull" }
}
if ($packaged -notcontains 'ParaDesk.exe.config') { Write-Warning '输出目录里没有 ParaDesk.exe.config（绑定重定向），发布包可能无法加载 WinRT 组件。' }

Write-Host ("打包文件 {0} 个：{1}" -f $packaged.Count, ($packaged -join ', ')) -ForegroundColor DarkGray
if ($excluded.Count -gt 0) {
    Write-Host ("按规则不打包：{0}" -f ($excluded -join ', ')) -ForegroundColor DarkGray
}
if ($notPackaged.Count -gt 0) {
    Write-Warning ("输出目录里有、但没有打包的文件：{0}`n  如果程序运行需要它们，请在 publish.ps1 的 includeExt 里补上扩展名。" -f ($notPackaged -join ', '))
}

Copy-Item -LiteralPath (Join-Path $ParaDeskRoot 'LICENSE') -Destination (Join-Path $stage 'LICENSE.txt')

function Get-DefaultHotkeys {
    $fallback = [ordered]@{
        toggleDesktop   = 'Ctrl+Alt+D'
        toggleViewOnly  = 'Ctrl+Alt+V'
        toggleRecording = 'Ctrl+Alt+R'
    }
    $src = Join-Path $ParaDeskRoot 'src\ParaDesk.App\Core\Settings.cs'
    $text = ''
    try { $text = Read-ParaDeskText -Path $src } catch { $text = '' }

    $result = [ordered]@{}
    foreach ($action in @($fallback.Keys)) {
        $pattern = 'Action\s*=\s*"' + $action + '"\s*,\s*Modifiers\s*=\s*([0-9\s|]+?)\s*,\s*Key\s*=\s*\(int\)''([A-Z0-9])''\s*,\s*Enabled\s*=\s*true'
        $m = [regex]::Match($text, $pattern)
        if (-not $m.Success) {
            Write-Warning "没能从 Settings.cs 读出 $action 的默认热键，说明里用 $($fallback[$action])。"
            $result[$action] = $fallback[$action]
            continue
        }
        $mods = 0
        foreach ($part in ($m.Groups[1].Value -split '\|')) {
            $t = $part.Trim()
            if ($t) { $mods = $mods -bor [int]$t }
        }
        $names = New-Object System.Collections.Generic.List[string]
        if ($mods -band 2) { $names.Add('Ctrl') }
        if ($mods -band 1) { $names.Add('Alt') }
        if ($mods -band 4) { $names.Add('Shift') }
        if ($mods -band 8) { $names.Add('Win') }
        $names.Add($m.Groups[2].Value)
        $result[$action] = $names -join '+'
    }
    return $result
}

function Write-DocFile([string]$Path, [string]$Text) {
    $crlf = ($Text.TrimEnd() -replace "`r?`n", "`r`n") + "`r`n"
    [IO.File]::WriteAllText($Path, $crlf, (New-Object Text.UTF8Encoding($true)))
}

$hk = Get-DefaultHotkeys
$hkDesktop = $hk['toggleDesktop'].PadRight(12)
$hkViewOnly = $hk['toggleViewOnly'].PadRight(12)
$hkRecording = $hk['toggleRecording'].PadRight(12)

$readmeEn = @"
ParaDesk $ver

A second Windows desktop on your spare monitor, with its own mouse and
keyboard, sharing your account and all your files. Let an AI coding agent
work over there while you keep using your own desktop.

Requirements
  - Windows 10 1903 or later / Windows 11
  - Pro, Enterprise or Education (Home has no child-session support)
  - .NET Framework 4.8 (ships with Windows 10 1903 and later)

First use
  1. Run ParaDesk.exe and press "Start desktop". The one-time system
     configuration asks for a single administrator approval.
  2. Restart the computer once after that first configuration.
  3. Pick a monitor and press "Start desktop".

Default hotkeys
  $hkDesktop Show / hide the parallel desktop
  $hkViewOnly Toggle view only
  $hkRecording Start / stop recording
  More actions (screenshot, pause recording, ...) can be bound on the
  Hotkeys page; they are unbound by default.

Command line (also works from inside the parallel desktop)
  ParaDesk.exe --status [--json]              current state
  ParaDesk.exe --start [--profile <name>] [--wait]
  ParaDesk.exe --detach | --close | --show | --quit
  ParaDesk.exe --notify [--title <text>] [--body <text>] [--level info|warn|error] [--sound]
  ParaDesk.exe --screenshot [<path>]
  ParaDesk.exe --record start|stop|pause|resume|toggle
  ParaDesk.exe --view-only on|off|toggle
  ParaDesk.exe --probe [--json]               read-only environment check
  ParaDesk.exe --diagbundle [<path>]          diagnostic zip for bug reports
  ParaDesk.exe --help                         every command and option

  Exit codes: 0 success, 1 failed, 2 not ready / not supported,
              3 ParaDesk is not running, 64 wrong arguments.
  ParaDesk.exe is a window program: in PowerShell, pipe or capture its
  output (or use Start-Process -Wait -PassThru) to get output and exit code.

If something goes wrong
  Main window -> Diagnostics -> "Export diagnostic bundle", then attach the
  zip to an issue at https://github.com/sinpoce/ParaDesk/issues
  Log file: %LOCALAPPDATA%\ParaDesk\paradesk.log

Licence: MIT License (see LICENSE.txt). Personal and commercial use,
modification and redistribution are allowed; keep the copyright and
licence notice with copies.
"@

$readmeZh = @"
ParaDesk 分身桌面  $ver

在闲置的显示器上开出第二个 Windows 桌面，拥有独立的鼠标键盘，
与主桌面互不干扰；同时共用同一账户与全部文件。
适合让 AI 编程 agent 在那边干活，你在这边照常用电脑。

运行要求
  - Windows 10 1903 或更高 / Windows 11
  - 专业版、企业版或教育版（家庭版不含子会话功能）
  - .NET Framework 4.8（Win10 1903+ 系统自带）

首次使用
  1. 运行 ParaDesk.exe，点“启动桌面”，按提示完成一次性系统配置（需要一次管理员授权）
  2. 配置后重启电脑一次
  3. 选择显示器，点“启动桌面”

默认快捷键
  $hkDesktop 显示 / 收起分身桌面
  $hkViewOnly 切换“仅查看”
  $hkRecording 开始 / 停止录制
  截图、暂停录制等更多动作可在“热键”页自行绑定（默认不占用）。

命令行（在分身桌面里也能用）
  ParaDesk.exe --status [--json]              查看当前状态
  ParaDesk.exe --start [--profile <方案名>] [--wait]
  ParaDesk.exe --detach | --close | --show | --quit
  ParaDesk.exe --notify [--title <标题>] [--body <正文>] [--level info|warn|error] [--sound]
  ParaDesk.exe --screenshot [<路径>]
  ParaDesk.exe --record start|stop|pause|resume|toggle
  ParaDesk.exe --view-only on|off|toggle
  ParaDesk.exe --probe [--json]               只读环境自检
  ParaDesk.exe --diagbundle [<路径>]          生成诊断包（报告问题时附上）
  ParaDesk.exe --help                         全部命令与参数

  退出码：0 成功，1 执行失败，2 环境未就绪或不支持，
          3 ParaDesk 没有在运行，64 参数写错。
  ParaDesk.exe 是窗口程序：在 PowerShell 里要接管道、赋值给变量，
  或用 Start-Process -Wait -PassThru，才能拿到输出与退出码。

出问题时
  主界面 →“诊断”→“导出诊断包”，把生成的 zip 附在
  https://github.com/sinpoce/ParaDesk/issues 的问题里。
  日志文件：%LOCALAPPDATA%\ParaDesk\paradesk.log

许可：MIT License（见 LICENSE.txt）。允许个人和商业使用、修改与再分发；
      复制或分发时请保留版权及许可声明。
"@

Write-DocFile -Path (Join-Path $stage 'README.txt') -Text $readmeEn
Write-DocFile -Path (Join-Path $stage '使用说明.txt') -Text $readmeZh

$zip = Join-Path $outRoot "ParaDesk-$ver.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$stageFull = (Resolve-Path -LiteralPath $stage).ProviderPath.TrimEnd('\')
$zipStream = [IO.File]::Open($zip, [IO.FileMode]::CreateNew)
try {
    $archive = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in @(Get-ChildItem -LiteralPath $stageFull -Recurse -File)) {
            $entryName = $f.FullName.Substring($stageFull.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $f.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $archive.Dispose() }
}
finally { $zipStream.Dispose() }

# 校验值，方便分发时核对完整性
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
Set-Content -LiteralPath (Join-Path $outRoot "ParaDesk-$ver.sha256") -Value "$hash  ParaDesk-$ver.zip" -Encoding ASCII

Write-Host ''
Write-Host "输出目录 : $stage" -ForegroundColor Green
Write-Host "压缩包   : $zip" -ForegroundColor Green
Write-Host ("大小     : {0:N2} MB" -f ((Get-Item -LiteralPath $zip).Length / 1MB))
Write-Host "SHA256   : $hash"

# ---------------- 安装包 ----------------

function Find-ISCC([string]$Explicit) {
    if ($Explicit) {
        if (Test-Path -LiteralPath $Explicit -PathType Leaf) { return (Resolve-Path -LiteralPath $Explicit).ProviderPath }
        $inDir = Join-Path $Explicit 'ISCC.exe'
        if (Test-Path -LiteralPath $inDir -PathType Leaf) { return (Resolve-Path -LiteralPath $inDir).ProviderPath }
        throw "-IsccPath 指定的 ISCC.exe 不存在: $Explicit"
    }
    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($cmd in @(Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue)) {
        if ($cmd.Path) { $candidates.Add($cmd.Path) }
    }
    $roots = @(${env:ProgramFiles(x86)}, $env:ProgramW6432, $env:ProgramFiles)
    if ($env:LOCALAPPDATA) { $roots += (Join-Path $env:LOCALAPPDATA 'Programs') }
    foreach ($root in $roots) {
        if (-not $root) { continue }
        foreach ($name in @('Inno Setup 7', 'Inno Setup 6')) { $candidates.Add((Join-Path $root "$name\ISCC.exe")) }
    }
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c -PathType Leaf) { return $c }
    }
    return $null
}

if ($NoInstaller) {
    Write-Host ''
    Write-Host '已跳过安装包（-NoInstaller）。' -ForegroundColor DarkGray
}
else {
    $iscc = Find-ISCC $IsccPath
    if (-not $iscc) {
        Write-Host ''
        Write-Warning '未找到 Inno Setup（ISCC.exe），只产出了 zip。'
        Write-Host '  安装：winget install JRSoftware.InnoSetup（需要 6.3 或更高版本），或用 -IsccPath 指定 ISCC.exe。' -ForegroundColor DarkGray
    }
    else {
        Write-Host ''
        Write-Host "构建安装包 ... ($iscc)" -ForegroundColor Cyan
        $iss = Join-Path $ParaDeskRoot 'installer\ParaDesk.iss'
        $isccArgs = @("/DAppVersion=$ver", "/DSourceDir=$stage", "/DOutputDir=$outRoot")

        $isccDir = Split-Path -Parent $iscc
        $zhIsl = Join-Path $isccDir 'Languages\ChineseSimplified.isl'
        if (Test-Path -LiteralPath $zhIsl -PathType Leaf) {
            $isccArgs += '/DWithChinese'
            Write-Host '  安装界面：英文 + 简体中文' -ForegroundColor DarkGray
        }
        else {
            Write-Warning "这份 Inno Setup 没有 Languages\ChineseSimplified.isl，只打英文界面的安装包。"
            Write-Host "  要中文安装界面：从 https://jrsoftware.org/files/istrans/ 下载 Chinese Simplified 的 ChineseSimplified.isl，" -ForegroundColor DarkGray
            Write-Host "  放到 $isccDir\Languages\ 下后重新运行。" -ForegroundColor DarkGray
        }
        $isccArgs += $iss

        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $isccOutput = @(& $iscc @isccArgs 2>&1 | ForEach-Object { "$_" })
            $isccCode = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $prevEap }
        if ($isccCode -ne 0) {
            $isccOutput | ForEach-Object { Write-Host "  $_" }
            throw "安装包构建失败 ($isccCode)"
        }

        $setup = Join-Path $outRoot "ParaDesk-$ver-Setup.exe"
        if (-not (Test-Path -LiteralPath $setup)) { throw "找不到安装包产物: $setup" }

        $setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
        Set-Content -LiteralPath (Join-Path $outRoot "ParaDesk-$ver-Setup.sha256") `
            -Value "$setupHash  ParaDesk-$ver-Setup.exe" -Encoding ASCII

        Write-Host "安装包   : $setup" -ForegroundColor Green
        Write-Host ("大小     : {0:N2} MB" -f ((Get-Item -LiteralPath $setup).Length / 1MB))
        Write-Host "SHA256   : $setupHash"
    }
}

Write-Host ''
Write-Host '提示：分发前应对 ParaDesk.exe 与安装包做代码签名，否则用户会看到 SmartScreen 警告。' -ForegroundColor Yellow
