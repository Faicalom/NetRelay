$ErrorActionPreference = 'Continue'
Write-Host '=== NetRelay PC environment check ==='
Write-Host "Windows: $([System.Environment]::OSVersion.VersionString)"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host "Administrator: $isAdmin"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$tools = Join-Path $root 'tools\netmode'
$tun = Join-Path $tools 'tun2socks.exe'
$win = Join-Path $tools 'wintun.dll'

foreach ($f in @($tun,$win)) {
    if (Test-Path $f) {
        $h = (Get-FileHash -Algorithm SHA256 $f).Hash
        Write-Host "OK: $f"
        Write-Host "    SHA256: $h"
    } else {
        Write-Host "MISSING: $f"
    }
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) {
    Write-Host "dotnet: $($dotnet.Source)"
    dotnet --version
} else {
    Write-Host 'dotnet: NOT FOUND'
}

$adb = Get-Command adb -ErrorAction SilentlyContinue
if ($adb) {
    Write-Host "adb: $($adb.Source)"
    adb devices -l
} else {
    Write-Host 'adb: not on PATH (NetRelay can also discover Android SDK locations itself)'
}

Write-Host 'TCP listeners:'
Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
  Where-Object { $_.LocalPort -in 8000,8988,8990,8991,8992 } |
  Sort-Object LocalPort |
  Format-Table -AutoSize LocalAddress,LocalPort,OwningProcess


if ($adb) {
    Write-Host 'ADB forwards:'
    $forwards = adb forward --list
    $forwards | ForEach-Object { Write-Host "    $_" }
    if ($forwards -match 'tcp:8990') {
        Write-Warning 'tcp:8990 must be PC-local SOCKS5 in v2. Remove stale adb forward 8990 before testing.'
    }
}

Write-Host 'Network adapters matching NetRelay/Wintun:'
Get-NetAdapter -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -match 'NetRelay|Wintun' -or $_.InterfaceDescription -match 'NetRelay|Wintun' } |
  Format-Table -AutoSize Name,InterfaceDescription,Status,LinkSpeed

if ($dotnet) {
    Write-Host 'Building project...'
    Push-Location $root
    try {
        dotnet build .\NetRelay.csproj -c Debug
    } finally {
        Pop-Location
    }
}

Write-Host '=== Done ==='
