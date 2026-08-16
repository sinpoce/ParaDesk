#Requires -Version 5.1
<#
  ParaDesk 发布脚本：产出可分发的绿色版压缩包与文件清单。

  用法：
    .\publish.ps1                 # 打包 Release 版到 dist\
    .\publish.ps1 -Version 1.1.0  # 指定版本号

  产出两份：
    dist\ParaDesk-<ver>.zip          绿色版，解压即用
    dist\ParaDesk-<ver>-Setup.exe    安装包（需要 Inno Setup，缺了就只出 zip）

  自动更新（Velopack）等确定分发渠道后再接。
#>
[CmdletBinding()]
param(
    [string]$Version = '',
    # 只想快速出 zip 时用
    [switch]$NoInstaller
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $here 'src\ParaDesk.App\ParaDesk.App.csproj'
$outRoot = Join-Path $here 'dist'

if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = 'D:\DevCache\nuget' }
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet).Source }

# 运行中的实例会锁住 exe
$running = Get-Process ParaDesk -ErrorAction SilentlyContinue
if ($running) { $running | Stop-Process -Force; Start-Sleep -Milliseconds 800 }

Write-Host '构建 Release ...' -ForegroundColor Cyan
$args = @($proj, '-c', 'Release', '-v', 'minimal', '-nologo')
if ($Version) { $args += "-p:Version=$Version" }
& $dotnet build @args
if ($LASTEXITCODE -ne 0) { throw "构建失败 ($LASTEXITCODE)" }

$binDir = Join-Path $here 'src\ParaDesk.App\bin\Release\net48'
$exe = Join-Path $binDir 'ParaDesk.exe'
if (-not (Test-Path $exe)) { throw "找不到产物: $exe" }

$ver = (Get-Item $exe).VersionInfo.FileVersion
if (-not $ver) { $ver = '1.0.0.0' }
Write-Host "版本: $ver" -ForegroundColor Green

$stage = Join-Path $outRoot "ParaDesk-$ver"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# 只带运行必需文件，不带 pdb 与开发期产物
$include = @('ParaDesk.exe', 'ParaDesk.exe.config', 'Wpf.Ui.dll')
foreach ($f in $include) {
    $src = Join-Path $binDir $f
    if (Test-Path $src) { Copy-Item $src $stage }
    else { Write-Warning "缺少 $f" }
}

# 附带一份说明，避免用户拿到 zip 不知道前置条件
$readme = @"
ParaDesk 分身桌面  $ver

在闲置的显示器上开出第二个 Windows 桌面，拥有独立的鼠标键盘，
与主桌面互不干扰；同时共用同一账户与全部文件。

运行要求
  - Windows 10 1903 或更高 / Windows 11
  - 专业版及以上（家庭版不含子会话功能）
  - .NET Framework 4.8（Win10 1903+ 系统自带）

首次使用
  1. 运行 ParaDesk.exe，按引导完成一次性配置（需要一次管理员授权）
  2. 配置后重启电脑一次
  3. 选择显示器，点“启动桌面”

常用快捷键
  Ctrl+Alt+D  显示 / 收起分身桌面
  Ctrl+Alt+V  切换“仅查看”
  Ctrl+Alt+R  开始 / 停止录制

出问题时：主界面 →“诊断”→“查看日志”，或把
%LOCALAPPDATA%\ParaDesk\paradesk.log 发给开发者。

许可：MIT License。允许个人和商业使用、修改与再分发；
      复制或分发时请保留版权及许可声明。
"@
Set-Content -Path (Join-Path $stage '使用说明.txt') -Value $readme -Encoding UTF8

$zip = Join-Path $outRoot "ParaDesk-$ver.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)

# 校验值，方便分发时核对完整性
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Set-Content -Path (Join-Path $outRoot "ParaDesk-$ver.sha256") -Value "$hash  ParaDesk-$ver.zip" -Encoding ASCII

Write-Host ''
Write-Host "输出目录 : $stage" -ForegroundColor Green
Write-Host "压缩包   : $zip" -ForegroundColor Green
Write-Host ("大小     : {0:N2} MB" -f ((Get-Item $zip).Length / 1MB))
Write-Host "SHA256   : $hash"

# ---------------- 安装包 ----------------

# ISCC 不装在固定位置：winget 装的、便携版、别的工具带的缓存副本都可能。
# 优先挑带简体中文语言包的那份——Inno 6 默认不含 ChineseSimplified.isl。
function Find-ISCC {
    $candidates = @(
        "$env:APPDATA\JackpotWorld\release_tools\cache\Inno7\ISCC.exe",
        "$env:APPDATA\JackpotWorld\release_tools\cache\Inno\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

if ($NoInstaller) {
    Write-Host ''
    Write-Host '已跳过安装包（-NoInstaller）。' -ForegroundColor DarkGray
}
else {
    $iscc = Find-ISCC
    if (-not $iscc) {
        Write-Host ''
        Write-Warning '未找到 Inno Setup（ISCC.exe），只产出了 zip。'
        Write-Host '  安装：winget install JRSoftware.InnoSetup.7' -ForegroundColor DarkGray
    }
    else {
        Write-Host ''
        Write-Host "构建安装包 ... ($iscc)" -ForegroundColor Cyan
        $iss = Join-Path $here 'installer\ParaDesk.iss'
        & $iscc "/DAppVersion=$ver" "/DSourceDir=$stage" "/DOutputDir=$outRoot" $iss | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "安装包构建失败 ($LASTEXITCODE)" }

        $setup = Join-Path $outRoot "ParaDesk-$ver-Setup.exe"
        if (-not (Test-Path $setup)) { throw "找不到安装包产物: $setup" }

        $setupHash = (Get-FileHash $setup -Algorithm SHA256).Hash
        Set-Content -Path (Join-Path $outRoot "ParaDesk-$ver-Setup.sha256") `
            -Value "$setupHash  ParaDesk-$ver-Setup.exe" -Encoding ASCII

        Write-Host "安装包   : $setup" -ForegroundColor Green
        Write-Host ("大小     : {0:N2} MB" -f ((Get-Item $setup).Length / 1MB))
        Write-Host "SHA256   : $setupHash"
    }
}

Write-Host ''
Write-Host '提示：分发前应对 ParaDesk.exe 与安装包做代码签名，否则用户会看到 SmartScreen 警告。' -ForegroundColor Yellow
