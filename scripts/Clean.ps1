[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$generatedPaths = @(
    'android\.gradle',
    'android\build',
    'android\app\build',
    'windows\bin',
    'windows\obj',
    'windows\tools',
    'windows\android',
    'windows\ui\node_modules',
    'windows\ui\dist',
    'logic_tests\Ael2FrameIntegrityTests\bin',
    'logic_tests\Ael2FrameIntegrityTests\obj',
    'logic_tests\WfdEndpointStateLogicTests\bin',
    'logic_tests\WfdEndpointStateLogicTests\obj',
    'logic_tests\WlanActiveScanLogicTests\bin',
    'logic_tests\WlanActiveScanLogicTests\obj',
    'windows\tests\Ael2BackpressureHarness\bin',
    'windows\tests\Ael2BackpressureHarness\obj',
    'windows\tests\ProductUiHarness\bin',
    'windows\tests\ProductUiHarness\obj',
    'installer\prerequisites',
    'FINAL_RELEASE'
)

foreach ($relativePath in $generatedPaths) {
    $target = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $relativePath))
    if (-not $target.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe generated path: $target"
    }
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
        Write-Host "Removed $relativePath"
    }
}

Get-ChildItem (Join-Path $repositoryRoot 'windows\ui') -Filter '*.tsbuildinfo' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

Write-Host 'NETRELAY_CLEAN=PASS' -ForegroundColor Green
