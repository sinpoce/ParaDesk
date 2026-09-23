#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$Quick,
    # spike 会真的建立一次子会话连接，可能需要人工输入一次凭据，
    # 因此默认不跑——无人值守时它只会超时，不代表功能有问题。
    [switch]$IncludeInteractive,
    [switch]$Release,
    [string]$Exe = '',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\common.ps1')

if ($Exe) {
    if ($Release) { Write-Warning '同时给了 -Exe 与 -Release，以 -Exe 为准。' }
    if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw "找不到指定的程序: $Exe" }
    $Exe = (Resolve-Path -LiteralPath $Exe).ProviderPath
}
else {
    $conf = if ($Release) { 'Release' } else { 'Debug' }
    $Exe = Get-ParaDeskExe -Configuration $conf
    if (-not (Test-Path -LiteralPath $Exe)) {
        $buildHint = if ($Release) { '.\build.ps1 -Release' } else { '.\build.ps1' }
        throw "找不到程序 $Exe ，请先运行 $buildHint"
    }
}
$log = Join-Path $env:LOCALAPPDATA 'ParaDesk\paradesk.log'
Write-Host "被测程序: $Exe" -ForegroundColor DarkGray

$exeDir = Split-Path -Parent $Exe
$running = @(Get-ParaDeskProcess -OutputDir $exeDir)
if ($running.Count -gt 0) {
    if ($Force) {
        Stop-ParaDeskIfRunning -OutputDir $exeDir -Force
    }
    else {
        $ids = (@($running | ForEach-Object {
                    if ($_.IsChildAgent) { '{0}（分身桌面守护）' -f $_.Id } else { [string]$_.Id }
                })) -join ', '
        Write-Host ("注意：被测程序正在运行（PID {0}）。自检不需要停止它，照常进行；需要干净环境时加 -Force。" -f $ids) `
            -ForegroundColor Yellow
        if (@($running | Where-Object { $_.IsChildAgent }).Count -gt 0) {
            Write-Host '  -Force 也会结束分身桌面守护，这次分身桌面里的保持唤醒与错误框拦截要到它下次登录才恢复。' `
                -ForegroundColor DarkYellow
        }
    }
}

function Write-Indented([string]$Text, [string]$Color = 'Gray') {
    if (-not $Text) { return }
    foreach ($line in ($Text.TrimEnd() -split "`r?`n")) {
        Write-Host ('    ' + $line) -ForegroundColor $Color
    }
}

$results = New-Object System.Collections.Generic.List[object]

function Invoke-Check([string]$Name, [string[]]$Arguments, [int]$TimeoutSec) {
    Write-Host "→ $Name ..." -ForegroundColor Cyan
    $status = ''
    $detail = ''
    $output = ''
    try {
        $r = Invoke-ParaDeskExe -Exe $Exe -ArgumentList $Arguments -TimeoutSec $TimeoutSec
        $output = $r.Output
        if ($r.TimedOut) { $status = 'timeout'; $detail = "超时（$TimeoutSec 秒）" }
        elseif ($r.ExitCode -eq 0) { $status = 'pass'; $detail = 'exit=0' }
        elseif ($r.ExitCode -eq 2) { $status = 'skip'; $detail = 'exit=2' }
        else { $status = 'fail'; $detail = "exit=$($r.ExitCode)" }
    }
    catch {
        $status = 'error'
        $detail = '无法运行: ' + $_.Exception.Message
    }

    switch ($status) {
        'pass' {
            Write-Host "  通过 ($detail)" -ForegroundColor Green
        }
        'skip' {
            Write-Host "  跳过：环境不支持或未就绪 ($detail)" -ForegroundColor Yellow
            Write-Indented $output 'DarkYellow'
        }
        default {
            Write-Host "  未通过 ($detail)" -ForegroundColor Red
            if ($output) {
                Write-Host '  输出:' -ForegroundColor Red
                Write-Indented $output
            }
            $tail = @()
            try { $tail = @(Get-ParaDeskLogTail -Lines 30) }
            catch { Write-Host "  读取 paradesk.log 失败: $($_.Exception.Message)" -ForegroundColor DarkGray }
            if ($tail.Count -gt 0) {
                Write-Host "  paradesk.log 最后 $($tail.Count) 行:" -ForegroundColor Red
                Write-Indented ($tail -join "`n") 'DarkGray'
            }
        }
    }

    $label = switch ($status) {
        'pass' { '通过' }
        'skip' { '跳过' }
        'timeout' { '超时' }
        'error' { '错误' }
        default { '失败' }
    }
    $results.Add([pscustomobject]@{ Name = $Name; Result = $detail; Status = $label; Failed = ($status -ne 'pass' -and $status -ne 'skip') })
}

Invoke-Check '环境自检 (--probe)'        @('--probe')        30
Invoke-Check '纯逻辑 (--logictest)'      @('--logictest')    60
# 本地化问题只在英文界面下显形，而日常都在中文下测，必须让机器查
Invoke-Check '本地化字典 (--i18ntest)'   @('--i18ntest')     30
# 守护必须"该关的关、不该动的别动"——后半条比前半条更要紧
Invoke-Check '子会话守护 (--dlgtest)'    @('--dlgtest')      60

if ($IncludeInteractive) {
    Write-Host '注意：显示能力验证会建立真实的子会话连接，可能需要你输入一次凭据。' -ForegroundColor Yellow
    Invoke-Check '显示能力验证 (--spike)' @('--spike') 180
}

if (-not $Quick) {
    Invoke-Check '录制与音频 (--rectest)'       @('--rectest', '12') 180
    Invoke-Check '画面内容校验 (--contenttest)' @('--contenttest')   240
}

Write-Host ''
Write-Host '================ 汇总 ================' -ForegroundColor Cyan
$results | Format-Table Name, Result, Status -AutoSize | Out-Host

$failed = @($results | Where-Object { $_.Failed })
$skipped = @($results | Where-Object { $_.Status -eq '跳过' })
if ($failed.Count -eq 0) {
    if ($skipped.Count -gt 0) {
        Write-Host ("全部通过（{0} 项因环境不支持或未就绪而跳过）" -f $skipped.Count) -ForegroundColor Green
    }
    else {
        Write-Host '全部通过' -ForegroundColor Green
    }
    Write-Host "详细日志见 $log"
    exit 0
}
Write-Host ("{0} 项未通过" -f $failed.Count) -ForegroundColor Red
Write-Host "详细日志见 $log"
exit 1
