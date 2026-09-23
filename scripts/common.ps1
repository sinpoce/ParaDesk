#Requires -Version 5.1

$ParaDeskRoot = Split-Path -Parent $PSScriptRoot
$ParaDeskProject = Join-Path $ParaDeskRoot 'src\ParaDesk.App\ParaDesk.App.csproj'

function Resolve-Dotnet {
    [CmdletBinding()]
    param()

    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($pf in @($env:ProgramW6432, $env:ProgramFiles)) {
        if ($pf) { $candidates.Add((Join-Path $pf 'dotnet\dotnet.exe')) }
    }
    if ($env:DOTNET_ROOT) { $candidates.Add((Join-Path $env:DOTNET_ROOT 'dotnet.exe')) }
    foreach ($cmd in @(Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue)) {
        if ($cmd.Path) { $candidates.Add($cmd.Path) }
    }

    $withoutSdk = $null
    foreach ($c in $candidates) {
        if (-not (Test-Path -LiteralPath $c -PathType Leaf)) { continue }
        $sdkDir = Join-Path (Split-Path -Parent $c) 'sdk'
        if ((Test-Path -LiteralPath $sdkDir -PathType Container) -and
            @(Get-ChildItem -LiteralPath $sdkDir -Directory -ErrorAction SilentlyContinue).Count -gt 0) {
            return $c
        }
        if (-not $withoutSdk) { $withoutSdk = $c }
    }

    $hint = "  安装 .NET SDK（8 或更高版本）：`n" +
            "    winget install Microsoft.DotNet.SDK.8`n" +
            "  或从 https://dotnet.microsoft.com/download 下载。装好后重新打开终端再运行。"
    if ($withoutSdk) {
        throw ("找到了 {0}，但它只有运行时、没有 SDK，无法编译。`n{1}" -f $withoutSdk, $hint)
    }
    throw ("找不到 dotnet.exe（.NET SDK）。`n{0}" -f $hint)
}

function Get-ParaDeskOutputDir {
    [CmdletBinding()]
    param(
        [ValidateSet('Debug', 'Release')]
        [string]$Configuration = 'Debug',
        [ValidateSet('AnyCPU', 'ARM64')]
        [string]$Platform = 'AnyCPU'
    )
    if ($Platform -eq 'ARM64') {
        return (Join-Path $ParaDeskRoot ('src\ParaDesk.App\bin\ARM64\{0}\net48' -f $Configuration))
    }
    return (Join-Path $ParaDeskRoot ('src\ParaDesk.App\bin\{0}\net48' -f $Configuration))
}

function Get-ParaDeskExe {
    [CmdletBinding()]
    param(
        [ValidateSet('Debug', 'Release')]
        [string]$Configuration = 'Debug',
        [ValidateSet('AnyCPU', 'ARM64')]
        [string]$Platform = 'AnyCPU'
    )
    return (Join-Path (Get-ParaDeskOutputDir -Configuration $Configuration -Platform $Platform) 'ParaDesk.exe')
}

function Get-ParaDeskBuildArgs {
    [CmdletBinding()]
    param(
        [ValidateSet('AnyCPU', 'ARM64')]
        [string]$Platform = 'AnyCPU'
    )
    if ($Platform -eq 'ARM64') { return @('-p:Platform=ARM64') }
    return @()
}

function Get-ParaDeskProcess {
    [CmdletBinding()]
    param(
        [string]$OutputDir
    )

    $prefix = $null
    if ($OutputDir) { $prefix = [IO.Path]::GetFullPath($OutputDir).TrimEnd('\') + '\' }

    $procs = @(Get-Process -Name ParaDesk -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return }

    $wmi = @{}
    try {
        foreach ($w in @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'ParaDesk.exe'" -ErrorAction Stop)) {
            $wmi[[int]$w.ProcessId] = $w
        }
    }
    catch { Write-Verbose ('读取 ParaDesk 进程的命令行失败：{0}' -f $_.Exception.Message) }

    $mySession = (Get-Process -Id $PID).SessionId

    foreach ($p in $procs) {
        $w = $wmi[[int]$p.Id]
        $path = $null
        try { $path = $p.Path } catch { $path = $null }
        if (-not $path -and $w) { $path = $w.ExecutablePath }
        if ($prefix) {
            if (-not $path) { continue }
            if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
        }

        $cmd = $null
        if ($w) { $cmd = $w.CommandLine }
        if ($cmd) {
            $isAgent = $cmd -match '(?i)(^|\s)--childagent(\s|$)'
        }
        else {
            $isAgent = ($p.SessionId -ne $mySession)
        }

        [pscustomobject]@{
            Id           = $p.Id
            SessionId    = $p.SessionId
            Path         = $path
            CommandLine  = $cmd
            IsChildAgent = [bool]$isAgent
            Process      = $p
        }
    }
}

function Stop-ParaDeskIfRunning {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$OutputDir,
        [switch]$Force
    )

    $procs = @(Get-ParaDeskProcess -OutputDir $OutputDir)
    if ($procs.Count -eq 0) { return }

    $mains = @($procs | Where-Object { -not $_.IsChildAgent })
    $agents = @($procs | Where-Object { $_.IsChildAgent })
    $agentConsequence = '守护被结束后，这次分身桌面里的"保持唤醒"与错误框拦截随即失效（"登录后自动运行"也不会重跑），要等分身桌面下次登录才恢复。'

    $desc = (@($procs | ForEach-Object {
                if ($_.IsChildAgent) { 'PID {0}，会话 {1}，分身桌面守护' -f $_.Id, $_.SessionId }
                else { 'PID {0}，会话 {1}' -f $_.Id, $_.SessionId }
            })) -join '；'
    if (-not $Force) {
        $lines = New-Object System.Collections.Generic.List[string]
        $lines.Add(('ParaDesk 正在运行（{0}），锁着这里的程序，加 -Force 强制停止。' -f $desc))
        $lines.Add(('  程序位于 {0}' -f $OutputDir))
        if ($mains.Count -gt 0) {
            $lines.Add('  主程序：可能有 agent 在分身桌面里工作。强制停止会断开分身桌面的画面（子会话本身保留），正在进行的录制可能损坏。')
            $lines.Add('    也可以先从托盘菜单退出 ParaDesk 再重试。')
        }
        if ($agents.Count -gt 0) {
            $lines.Add('  分身桌面守护：它随分身桌面登录启动、一直运行到分身桌面注销，从托盘退出 ParaDesk 停不掉它。')
            $lines.Add('    不用 -Force 的办法是注销分身桌面：在分身桌面里注销，或打开 ParaDesk 点"关闭桌面"（ParaDesk.exe --close）后再从托盘退出；')
            $lines.Add('    这会结束分身桌面里的所有程序。')
            $lines.Add('    加 -Force：' + $agentConsequence)
            $lines.Add('    守护用的是这份程序，是因为最近运行的是它（程序启动时会把启动项改成指向自己）；')
            $lines.Add('    再运行一次你平时用的那份 ParaDesk（例如安装版）即可改回，从分身桌面下次登录起生效。')
        }
        throw ($lines -join "`n")
    }

    Write-Host ('停止运行中的 ParaDesk（{0}）...' -f $desc) -ForegroundColor Yellow
    foreach ($p in $procs) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction Stop }
        catch { Write-Warning ('停止 PID {0} 失败：{1}' -f $p.Id, $_.Exception.Message) }
    }
    foreach ($p in $procs) {
        try { [void]$p.Process.WaitForExit(5000) } catch { }
    }
    $left = @(Get-ParaDeskProcess -OutputDir $OutputDir)
    if ($left.Count -gt 0) {
        throw ('仍有 ParaDesk 进程没有退出（PID {0}），请手动结束后重试。' -f ((@($left | ForEach-Object { $_.Id })) -join ', '))
    }
    if ($agents.Count -gt 0) {
        Write-Warning ('已结束分身桌面守护（PID {0}）。{1}' -f ((@($agents | ForEach-Object { $_.Id })) -join ', '), $agentConsequence)
    }
    Start-Sleep -Milliseconds 300
}

function Read-ParaDeskText {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return '' }
    $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
    $fs = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
    try {
        $reader = New-Object IO.StreamReader($fs, (New-Object Text.UTF8Encoding($false)), $true)
        try { return $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $fs.Dispose() }
}

function Invoke-ParaDeskExe {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Exe,
        [string[]]$ArgumentList = @(),
        [int]$TimeoutSec = 120
    )

    $tmpOut = [IO.Path]::GetTempFileName()
    $tmpErr = [IO.Path]::GetTempFileName()
    try {
        $spArgs = @{
            FilePath               = $Exe
            NoNewWindow            = $true
            PassThru               = $true
            RedirectStandardOutput = $tmpOut
            RedirectStandardError  = $tmpErr
        }
        $quoted = @($ArgumentList | Where-Object { $null -ne $_ } | ForEach-Object {
                if ($_ -eq '' -or $_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
            })
        if ($quoted.Count -gt 0) { $spArgs['ArgumentList'] = $quoted }

        $p = Start-Process @spArgs
        $null = $p.Handle

        $timedOut = -not $p.WaitForExit([Math]::Max(1, $TimeoutSec) * 1000)
        $code = $null
        if ($timedOut) {
            try { $p.Kill() } catch { }
            try { [void]$p.WaitForExit(5000) } catch { }
        }
        else {
            $p.WaitForExit()
            $code = $p.ExitCode
        }

        $out = Read-ParaDeskText -Path $tmpOut
        $err = Read-ParaDeskText -Path $tmpErr
        if ($err) {
            if ($out -and -not $out.EndsWith("`n")) { $out += "`n" }
            $out += $err
        }
        return [pscustomobject]@{
            ExitCode = $code
            TimedOut = $timedOut
            Output   = $out
        }
    }
    finally {
        Remove-Item -LiteralPath $tmpOut, $tmpErr -Force -ErrorAction SilentlyContinue
    }
}

function Get-ParaDeskLogTail {
    [CmdletBinding()]
    param(
        [int]$Lines = 30
    )
    $log = Join-Path $env:LOCALAPPDATA 'ParaDesk\paradesk.log'
    $text = Read-ParaDeskText -Path $log
    if (-not $text) { return @() }
    $all = @($text.TrimEnd("`r", "`n") -split "`r?`n")
    return @($all | Select-Object -Last $Lines)
}
