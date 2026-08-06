#Requires -Version 5.1
<#
  ParaDesk 回归自检：把所有可自动化的检查跑一遍。

  用法：
    .\selftest.ps1            # 全部检查
    .\selftest.ps1 -Quick     # 跳过耗时的录制类检查

  为什么要有这个脚本：录制相关的缺陷用肉眼极难发现——时长、文件大小、
  能否播放全都正常，画面内容却可能是错的。必须让机器去量。
#>
[CmdletBinding()]
param(
    [switch]$Quick,
    # spike 会真的建立一次子会话连接，可能需要人工输入一次凭据，
    # 因此默认不跑——无人值守时它只会超时，不代表功能有问题。
    [switch]$IncludeInteractive
)

$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $here 'src\ParaDesk.App\bin\Debug\net48\ParaDesk.exe'
$log = Join-Path $env:LOCALAPPDATA 'ParaDesk\paradesk.log'

if (-not (Test-Path $exe)) { throw "找不到程序，请先运行 .\build.ps1" }

$results = @()
function Run-Check([string]$name, [string[]]$argv, [int]$okCode, [int]$timeoutSec) {
    Write-Host "→ $name ..." -ForegroundColor Cyan
    $p = Start-Process $exe -ArgumentList $argv -PassThru
    if (-not $p.WaitForExit($timeoutSec * 1000)) {
        try { $p.Kill() } catch {}
        Write-Host "  超时" -ForegroundColor Red
        return [pscustomobject]@{ Name = $name; Result = '超时'; Ok = $false }
    }
    $ok = ($p.ExitCode -eq $okCode)
    Write-Host ("  退出码 {0}  {1}" -f $p.ExitCode, $(if ($ok) { '通过' } else { '未通过' })) `
        -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    return [pscustomobject]@{ Name = $name; Result = "exit=$($p.ExitCode)"; Ok = $ok }
}

# 运行中的实例会影响单实例检查与文件锁
Get-Process ParaDesk -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

$results += Run-Check '环境自检 (--probe)'        @('--probe')        0 30
# 本地化问题只在英文界面下显形，而日常都在中文下测，必须让机器查
$results += Run-Check '本地化字典 (--i18ntest)'   @('--i18ntest')     0 30
# 守护必须"该关的关、不该动的别动"——后半条比前半条更要紧
$results += Run-Check '子会话守护 (--dlgtest)'    @('--dlgtest')      0 60

if ($IncludeInteractive) {
    Write-Host '注意：显示能力验证会建立真实的子会话连接，可能需要你输入一次凭据。' -ForegroundColor Yellow
    $results += Run-Check '显示能力验证 (--spike)' @('--spike') 0 180
}

if (-not $Quick) {
    $results += Run-Check '录制与音频 (--rectest)'    @('--rectest', '12') 0 180
    $results += Run-Check '画面内容校验 (--contenttest)' @('--contenttest')   0 240
}

Write-Host ''
Write-Host '================ 汇总 ================' -ForegroundColor Cyan
$results | Format-Table Name, Result, Ok -AutoSize

$failed = @($results | Where-Object { -not $_.Ok })
if ($failed.Count -eq 0) {
    Write-Host '全部通过' -ForegroundColor Green
    Write-Host "详细输出见 $log"
    exit 0
}
Write-Host ("$($failed.Count) 项未通过") -ForegroundColor Red
Write-Host "详细输出见 $log"
exit 1
