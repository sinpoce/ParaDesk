#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Exe,
    [ValidateSet('', 'arm64', 'x64')]
    [string]$ExpectArch = ''
)

$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path -LiteralPath $Exe).ProviderPath
$tmp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$out = Join-Path $tmp 'paradesk-stdout.txt'
$err = Join-Path $tmp 'paradesk-stderr.txt'

function Invoke-Check([string]$Name, [string[]]$Arguments, [int[]]$OkCodes) {
    $p = Start-Process -FilePath $Exe -ArgumentList $Arguments -Wait -PassThru -NoNewWindow `
        -RedirectStandardOutput $out -RedirectStandardError $err
    $code = $p.ExitCode
    Write-Host "::group::$Name (exit $code)"
    if (Test-Path -LiteralPath $out) { Get-Content -LiteralPath $out -Encoding utf8 | Write-Host }
    if (Test-Path -LiteralPath $err) { Get-Content -LiteralPath $err -Encoding utf8 | Write-Host }
    Write-Host '::endgroup::'
    if ($OkCodes -contains $code) {
        Write-Host "PASS  $Name (exit $code)"
        return $true
    }
    Write-Host "::error::$Name exited with $code (expected $($OkCodes -join ' or '))"
    return $false
}

Write-Host "Testing $Exe"
$failed = 0
if (-not (Invoke-Check 'i18n dictionary (--i18ntest)' @('--i18ntest') @(0))) { $failed++ }
if (-not (Invoke-Check 'pure logic (--logictest)' @('--logictest') @(0))) { $failed++ }
if (-not (Invoke-Check 'environment probe (--probe --json)' @('--probe', '--json') @(0, 2))) { $failed++ }
else {
    $probe = Get-Content -LiteralPath $out -Raw -Encoding utf8 | ConvertFrom-Json
    Write-Host ("Architecture: process={0} os={1} emulated={2}" -f $probe.processArchitecture, $probe.osArchitecture, $probe.emulated)
    if ($ExpectArch -and ($probe.processArchitecture -ne $ExpectArch -or $probe.emulated)) {
        Write-Host "::error::expected to run natively as $ExpectArch"
        $failed++
    }
}
if (-not (Invoke-Check 'help (--help)' @('--help') @(0))) { $failed++ }

if ($failed -gt 0) { throw "$failed self-test(s) failed" }
