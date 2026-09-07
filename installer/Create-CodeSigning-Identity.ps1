[CmdletBinding()]
param(
    [switch]$PreflightOnly)

$ErrorActionPreference = 'Stop'

# Codex runs under PowerShell 7 and extends PSModulePath with its own modules.
# This helper must use the inbox Windows PowerShell 5.1 security/PKI modules.
$windowsModuleRoot = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\Modules'
$userModuleRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'WindowsPowerShell\Modules'
$programModuleRoot = Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules'
$env:PSModulePath = @($userModuleRoot, $programModuleRoot, $windowsModuleRoot) -join ';'

Import-Module (Join-Path $windowsModuleRoot 'Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1')
Import-Module (Join-Path $windowsModuleRoot 'PKI\PKI.psd1')

if (-not (Get-PSDrive -Name Cert -ErrorAction SilentlyContinue)) {
    New-PSDrive -Name Cert -PSProvider Certificate -Root '\' -Scope Script | Out-Null
}
if (-not (Get-PSDrive -Name Cert -ErrorAction SilentlyContinue)) {
    throw 'The Windows certificate provider could not be initialized.'
}
if (-not (Get-Command New-SelfSignedCertificate -ErrorAction SilentlyContinue)) {
    throw 'The Windows code-signing certificate cmdlet is unavailable.'
}
if ($PreflightOnly) {
    Write-Host 'SIGNING_IDENTITY_PREFLIGHT=PASS' -ForegroundColor Green
    Write-Host ('PROCESS_64BIT=' + [Environment]::Is64BitProcess)
    Write-Host ('CERT_PROVIDER=' + (Get-PSDrive -Name Cert).Provider.Name)
    return
}

$projectDirectory = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$privateDirectory = Join-Path $projectDirectory 'PRIVATE\WINDOWS_SIGNING'
$publicDirectory = Join-Path $projectDirectory 'FINAL_RELEASE\Security'
$pfxPath = Join-Path $privateDirectory 'NetRelay-CodeSigning-Identity.pfx'
$publicCertificatePath = Join-Path $publicDirectory 'NetRelay-CodeSigning-Public.cer'
$thumbprintPath = Join-Path $privateDirectory 'NetRelay-CodeSigning-Thumbprint.txt'
$subject = 'CN=Net Relay Release Signing'

function SecureStringsEqual {
    param(
        [Parameter(Mandatory)][Security.SecureString]$First,
        [Parameter(Mandatory)][Security.SecureString]$Second)

    $firstPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($First)
    $secondPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Second)
    try {
        $firstPlain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($firstPointer)
        $secondPlain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($secondPointer)
        return -not [string]::IsNullOrWhiteSpace($firstPlain) -and $firstPlain -ceq $secondPlain
    }
    finally {
        $firstPlain = $null
        $secondPlain = $null
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($firstPointer)
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secondPointer)
    }
}

if (Test-Path -LiteralPath $pfxPath) {
    throw 'A permanent Net Relay signing identity backup already exists. Refusing to replace it.'
}

Write-Host 'Net Relay permanent self-signed Authenticode identity' -ForegroundColor Cyan
Write-Host 'This password protects the local PFX backup only. It is never requested by Setup.'
$password = Read-Host 'PFX backup password' -AsSecureString
$confirmation = Read-Host 'Confirm PFX backup password' -AsSecureString
if (-not (SecureStringsEqual -First $password -Second $confirmation)) {
    throw 'The passwords are empty or do not match.'
}

New-Item -ItemType Directory -Force -Path $privateDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $publicDirectory | Out-Null

$certificate = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $subject `
    -FriendlyName 'Net Relay Release Signing' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears(5)

try {
    Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $password -ChainOption EndEntityCertOnly | Out-Null
    Export-Certificate -Cert $certificate -FilePath $publicCertificatePath -Type CERT | Out-Null
    [IO.File]::WriteAllText($thumbprintPath, $certificate.Thumbprint, (New-Object Text.UTF8Encoding($false)))

    Write-Host 'SIGNING_IDENTITY_CREATED=YES' -ForegroundColor Green
    Write-Host ('THUMBPRINT=' + $certificate.Thumbprint)
    Write-Host ('PUBLIC_CERTIFICATE=' + $publicCertificatePath)
    Write-Host 'PUBLIC_TRUST=SELF_SIGNED_NOT_PUBLICLY_TRUSTED' -ForegroundColor Yellow
}
catch {
    Remove-Item -LiteralPath (Join-Path 'Cert:\CurrentUser\My' $certificate.Thumbprint) -Force -ErrorAction SilentlyContinue
    throw
}
