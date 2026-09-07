[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InnoCompiler,
    [Parameter(Mandatory)][string]$SignTool,
    [Parameter(Mandatory)][string]$VcVars64,
    [Parameter(Mandatory)][string]$WebView2Installer,
    [string]$Thumbprint = 'D7B5736B8CF01BFE6E90301F74BE8022013418F5'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$validation = Join-Path $projectRoot 'FINAL_SETUP_VALIDATION'
$release = Join-Path $validation 'Release'
$support = Join-Path $validation 'Support'
$candidate = Join-Path $validation 'NetRelay-Windows-1.0.0-Setup-PublicCandidate.exe'
if (Test-Path -LiteralPath $candidate) {
    throw 'Candidate already exists. Preserve any binary sent for acceptance; choose a new isolated validation directory before rebuilding.'
}
foreach ($tool in @($InnoCompiler,$SignTool,$VcVars64,$WebView2Installer)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Missing build input: $tool" }
}
$expectedApk = 'ACD3DD5E5DCE2AC27CF9EF1DE213A366D88E5B99BDA5D3C03A447AF27F85798C'
$apk = Join-Path $projectRoot 'windows\android\NetRelay-Android-1.0.0-release.apk'
if ((Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash -ne $expectedApk) {
    throw 'Accepted APK hash mismatch. Android must not be rebuilt by this script.'
}
# Pinned, audited Microsoft offline x64 redistributable, not an online bootstrapper.
if ((Get-FileHash -LiteralPath $WebView2Installer -Algorithm SHA256).Hash -ne
    '987A9D8B3107E84F9B53B4A077D28AE4814FC3D964D5A55C559E7334BBF24D61') {
    throw 'WebView2 redistributable changed: audit its architecture, signature and offline payload before updating the pin.'
}
function Assert-Exit([string]$Stage) { if ($LASTEXITCODE -ne 0) { throw "$Stage failed: $LASTEXITCODE" } }
New-Item -ItemType Directory -Path $support -Force | Out-Null
Push-Location (Join-Path $projectRoot 'windows\ui')
try { & npm.cmd run build; Assert-Exit 'Frontend' } finally { Pop-Location }
Push-Location $projectRoot
try {
    & dotnet.exe publish windows/NetRelay.csproj -c Release -r win-x64 --self-contained true --no-restore -o $release
    Assert-Exit 'Self-contained publish'
    & cmd.exe /d /c ('"' + $VcVars64 + '" && cl /nologo /W4 /WX /MT /O2 /EHsc installer\WebView2Probe.cpp /FoFINAL_SETUP_VALIDATION\Support\WebView2Probe.obj /FeFINAL_SETUP_VALIDATION\Support\WebView2Probe.exe /link ole32.lib /DYNAMICBASE /NXCOMPAT')
    Assert-Exit 'Static native prerequisite probe'
    & $SignTool sign /fd SHA256 /sha1 $Thumbprint (Join-Path $release 'NetRelay.exe') (Join-Path $support 'WebView2Probe.exe')
    Assert-Exit 'Payload signing'
    & $InnoCompiler ('/DPublishRoot=' + $release) ('/DWebView2Installer=' + $WebView2Installer) ('/DPrerequisiteProbe=' + (Join-Path $support 'WebView2Probe.exe')) ('/O' + $validation) /FNetRelay-Windows-1.0.0-Setup-PublicCandidate installer/NetRelay.iss
    Assert-Exit 'Setup'
    & $SignTool sign /fd SHA256 /sha1 $Thumbprint $candidate
    Assert-Exit 'Setup signing'
    Get-FileHash -LiteralPath $candidate -Algorithm SHA256
    Write-Host 'NETRELAY_PUBLIC_SETUP=BLOCKED pending normal-user acceptance. APPS untouched.'
} finally { Pop-Location }
