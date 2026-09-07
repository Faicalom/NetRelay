[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipDependencies
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

# Gradle's Windows batch launcher cannot reliably encode every non-ASCII path.
# Re-enter through an ASCII-only temporary junction when needed.
if ($repositoryRoot -match '[^\u0000-\u007F]' -and $env:NETRELAY_BUILD_ASCII_RELAY -ne '1') {
    $relayPath = Join-Path ([IO.Path]::GetTempPath()) ('netrelay-build-' + [guid]::NewGuid().ToString('N'))
    $relayItem = New-Item -ItemType Junction -Path $relayPath -Target $repositoryRoot
    $forwardParameters = @{
        SkipTests = [bool]$SkipTests
        SkipDependencies = [bool]$SkipDependencies
    }
    try {
        $env:NETRELAY_BUILD_ASCII_RELAY = '1'
        & (Join-Path $relayPath 'scripts\Build-All.ps1') @forwardParameters
        if ($LASTEXITCODE -ne 0) {
            throw "Relayed build failed with exit code $LASTEXITCODE"
        }
        return
    }
    finally {
        Remove-Item Env:NETRELAY_BUILD_ASCII_RELAY -ErrorAction SilentlyContinue
        $resolvedRelay = [IO.Path]::GetFullPath($relayPath)
        $systemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if ($resolvedRelay.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase) -and
            $relayItem.LinkType -eq 'Junction') {
            $relayItem.Delete()
        }
    }
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [string]$WorkingDirectory
    )

    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$FilePath failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }
}

$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$npm = (Get-Command npm.cmd -ErrorAction Stop).Source

if ([string]::IsNullOrWhiteSpace($env:ANDROID_HOME) -and [string]::IsNullOrWhiteSpace($env:ANDROID_SDK_ROOT)) {
    $defaultAndroidSdk = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Android\Sdk'
    if (Test-Path -LiteralPath $defaultAndroidSdk) {
        $env:ANDROID_HOME = $defaultAndroidSdk
        $env:ANDROID_SDK_ROOT = $defaultAndroidSdk
    }
}

if (-not $SkipDependencies) {
    & (Join-Path $PSScriptRoot 'Prepare-Dependencies.ps1')
}

$uiRoot = Join-Path $repositoryRoot 'windows\ui'
Invoke-Checked $npm @('ci') $uiRoot
Invoke-Checked $npm @('run', 'build') $uiRoot

$androidRoot = Join-Path $repositoryRoot 'android'
$gradle = Join-Path $androidRoot 'gradlew.bat'
$gradleTasks = if ($SkipTests) { @('assembleDebug', '--no-daemon') } else { @('testDebugUnitTest', 'assembleDebug', '--no-daemon') }
Invoke-Checked $gradle $gradleTasks $androidRoot

$androidOutput = Join-Path $androidRoot 'app\build\outputs\apk\debug\app-debug.apk'
if (-not (Test-Path -LiteralPath $androidOutput)) {
    throw "Android build output was not found: $androidOutput"
}
$windowsAndroid = Join-Path $repositoryRoot 'windows\android'
New-Item -ItemType Directory -Path $windowsAndroid -Force | Out-Null
Copy-Item $androidOutput (Join-Path $windowsAndroid 'NetRelay-Android-1.0.0-release.apk') -Force

Invoke-Checked $dotnet @('build', 'windows\NetRelay.csproj', '-c', 'Release') $repositoryRoot

if (-not $SkipTests) {
    $testProjects = @(
        'logic_tests\WlanActiveScanLogicTests\WlanActiveScanLogicTests.csproj',
        'logic_tests\WfdEndpointStateLogicTests\WfdEndpointStateLogicTests.csproj',
        'logic_tests\Ael2FrameIntegrityTests\Ael2FrameIntegrityTests.csproj',
        'windows\tests\Ael2BackpressureHarness\Ael2BackpressureHarness.csproj',
        'windows\tests\ProductUiHarness\ProductUiHarness.csproj'
    )
    foreach ($testProject in $testProjects) {
        Invoke-Checked $dotnet @('run', '--project', $testProject, '-c', 'Release') $repositoryRoot
    }
}

Write-Host 'NETRELAY_BUILD=PASS' -ForegroundColor Green
