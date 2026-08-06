#Requires -Version 5.1
<#
  ParaDesk 构建脚本。
  用法：
    .\build.ps1              # Debug 构建
    .\build.ps1 -Release     # Release 构建
    .\build.ps1 -Run         # 构建后运行
    .\build.ps1 -Probe       # 构建后跑只读环境自检
#>
[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$Run,
    [switch]$Probe
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $here 'src\ParaDesk.App\ParaDesk.App.csproj'
$conf = if ($Release) { 'Release' } else { 'Debug' }

# NuGet 缓存放 D 盘，避免撑爆系统盘
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = 'D:\DevCache\nuget' }

$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet).Source }

# 运行中的实例会锁住输出的 exe，先停掉再编译
$running = Get-Process ParaDesk -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "停止运行中的 ParaDesk ($($running.Id -join ', ')) ..." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

Write-Host "构建 $conf ..." -ForegroundColor Cyan
& $dotnet build $proj -c $conf -v minimal -nologo
if ($LASTEXITCODE -ne 0) { throw "构建失败 (exit $LASTEXITCODE)" }

$exe = Join-Path $here "src\ParaDesk.App\bin\$conf\net48\ParaDesk.exe"
Write-Host "输出: $exe" -ForegroundColor Green

if ($Probe) {
    Write-Host "`n环境自检:" -ForegroundColor Cyan
    & $exe --probe
    Write-Host "`n(自检报告同时写入 $env:LOCALAPPDATA\ParaDesk\probe.log)"
}
elseif ($Run) {
    Start-Process $exe
    Write-Host "已启动。" -ForegroundColor Green
}
