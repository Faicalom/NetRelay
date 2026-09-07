[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolsRoot = Join-Path $repositoryRoot 'windows\tools'
$adbTarget = Join-Path $toolsRoot 'adb'
$netModeTarget = Join-Path $toolsRoot 'netmode'

New-Item -ItemType Directory -Path $adbTarget, $netModeTarget -Force | Out-Null

function Find-PlatformTools {
    $candidates = @(
        (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Android\Sdk\platform-tools')
    )
    foreach ($sdkRoot in @($env:ANDROID_SDK_ROOT, $env:ANDROID_HOME)) {
        if (-not [string]::IsNullOrWhiteSpace($sdkRoot)) {
            $candidates += Join-Path $sdkRoot 'platform-tools'
        }
    }

    $adbCommand = Get-Command adb.exe -ErrorAction SilentlyContinue
    if ($adbCommand) {
        $candidates += Split-Path -Parent $adbCommand.Source
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if ((Test-Path (Join-Path $candidate 'adb.exe')) -and
            (Test-Path (Join-Path $candidate 'AdbWinApi.dll')) -and
            (Test-Path (Join-Path $candidate 'AdbWinUsbApi.dll'))) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }

    throw 'Android SDK Platform-Tools were not found. Install them with Android Studio and set ANDROID_SDK_ROOT or ANDROID_HOME.'
}

$platformTools = Find-PlatformTools
Copy-Item (Join-Path $platformTools 'adb.exe') $adbTarget -Force
Copy-Item (Join-Path $platformTools 'AdbWinApi.dll') $adbTarget -Force
Copy-Item (Join-Path $platformTools 'AdbWinUsbApi.dll') $adbTarget -Force

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('netrelay-dependencies-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

try {
    $tun2SocksArchive = Join-Path $temporaryRoot 'tun2socks.zip'
    $tun2SocksExtract = Join-Path $temporaryRoot 'tun2socks'
    Invoke-WebRequest `
        -Uri 'https://github.com/xjasonlyu/tun2socks/releases/download/v2.6.0/tun2socks-windows-amd64.zip' `
        -OutFile $tun2SocksArchive `
        -UseBasicParsing
    Expand-Archive -LiteralPath $tun2SocksArchive -DestinationPath $tun2SocksExtract
    $tun2SocksBinary = Get-ChildItem $tun2SocksExtract -Recurse -File |
        Where-Object Name -eq 'tun2socks-windows-amd64.exe' |
        Select-Object -First 1
    if (-not $tun2SocksBinary) {
        throw 'The tun2socks Windows AMD64 binary was not present in the official archive.'
    }
    $tun2SocksHash = (Get-FileHash $tun2SocksBinary.FullName -Algorithm SHA256).Hash
    if ($tun2SocksHash -ne 'A1F8AC84852ED9A9C7A50949CD0290B6F1594E118BF053416B9B55B7CD7AE414') {
        throw "Unexpected tun2socks binary hash: $tun2SocksHash"
    }
    Copy-Item $tun2SocksBinary.FullName (Join-Path $netModeTarget 'tun2socks.exe') -Force

    $wintunArchive = Join-Path $temporaryRoot 'wintun.zip'
    $wintunExtract = Join-Path $temporaryRoot 'wintun'
    Invoke-WebRequest `
        -Uri 'https://www.wintun.net/builds/wintun-0.14.1.zip' `
        -OutFile $wintunArchive `
        -UseBasicParsing
    $wintunArchiveHash = (Get-FileHash $wintunArchive -Algorithm SHA256).Hash
    if ($wintunArchiveHash -ne '07C256185D6EE3652E09FA55C0B673E2624B565E02C4B9091C79CA7D2F24EF51') {
        throw "Unexpected Wintun archive hash: $wintunArchiveHash"
    }
    Expand-Archive -LiteralPath $wintunArchive -DestinationPath $wintunExtract
    $wintunBinary = Join-Path $wintunExtract 'wintun\bin\amd64\wintun.dll'
    if (-not (Test-Path -LiteralPath $wintunBinary)) {
        throw 'The Wintun AMD64 DLL was not present in the official archive.'
    }
    Copy-Item $wintunBinary (Join-Path $netModeTarget 'wintun.dll') -Force
}
finally {
    $resolvedTemp = [IO.Path]::GetFullPath($temporaryRoot)
    $systemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedTemp.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTemp).StartsWith('netrelay-dependencies-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'NETRELAY_DEPENDENCIES_READY=YES' -ForegroundColor Green
Write-Host "ADB_SOURCE=$platformTools"
Write-Host 'TUN2SOCKS_VERSION=2.6.0'
Write-Host 'WINTUN_VERSION=0.14.1'
