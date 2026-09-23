#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$Run,
    [switch]$Probe,
    [switch]$Force,
    [switch]$Arm64
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\common.ps1')

$conf = if ($Release) { 'Release' } else { 'Debug' }
$dotnet = Resolve-Dotnet
$platform = if ($Arm64) { 'ARM64' } else { 'AnyCPU' }
$outDir = Get-ParaDeskOutputDir -Configuration $conf -Platform $platform
$exe = Get-ParaDeskExe -Configuration $conf -Platform $platform

Stop-ParaDeskIfRunning -OutputDir $outDir -Force:$Force

Write-Host "构建 $conf ($platform) ..." -ForegroundColor Cyan
$platformArgs = @(Get-ParaDeskBuildArgs -Platform $platform)
& $dotnet build $ParaDeskProject -c $conf -v minimal -nologo @platformArgs
if ($LASTEXITCODE -ne 0) { throw "构建失败 (exit $LASTEXITCODE)" }
if (-not (Test-Path -LiteralPath $exe)) { throw "构建成功但找不到产物: $exe" }

Write-Host "输出: $exe" -ForegroundColor Green

if ($Probe) {
    Write-Host "`n环境自检 (--probe):" -ForegroundColor Cyan
    $r = Invoke-ParaDeskExe -Exe $exe -ArgumentList @('--probe') -TimeoutSec 60
    if ($r.Output) { Write-Host $r.Output.TrimEnd() }
    if ($r.TimedOut) {
        Write-Host '自检超时（60 秒）。' -ForegroundColor Red
    }
    elseif ($r.ExitCode -eq 0) {
        Write-Host "`n就绪，可以启动分身桌面。(exit 0)" -ForegroundColor Green
    }
    elseif ($r.ExitCode -eq 2) {
        Write-Host "`n环境未就绪，按上面的 NextAction 处理。(exit 2)" -ForegroundColor Yellow
    }
    else {
        Write-Host "`n自检出错 (exit $($r.ExitCode))。" -ForegroundColor Red
    }
    Write-Host "(报告同时写入 $env:LOCALAPPDATA\ParaDesk\probe.log)" -ForegroundColor DarkGray
}
elseif ($Run) {
    $mySession = (Get-Process -Id $PID).SessionId
    $others = @(Get-ParaDeskProcess | Where-Object { $_.Path -and $_.SessionId -eq $mySession })
    if ($others.Count -gt 0) {
        $msg = '另一个 ParaDesk 正在运行（{0}）。单实例机制下，新构建的程序启动后只会把它唤到前台；要运行新构建，请先从托盘菜单退出它。' -f $others[0].Path
        Write-Warning $msg
    }
    Start-Process -FilePath $exe
    Write-Host "已启动。" -ForegroundColor Green
}
