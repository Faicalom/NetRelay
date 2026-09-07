[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$trackedFiles = @(git -C $repositoryRoot ls-files)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to enumerate tracked files.'
}

$errors = [Collections.Generic.List[string]]::new()
$forbiddenBinaryExtensions = @(
    '.apk', '.aab', '.exe', '.dll', '.msi', '.zip', '.7z', '.rar',
    '.pfx', '.p12', '.jks', '.keystore', '.pem', '.key', '.cer'
)
$generatedPathPattern = '(^|/)(build|bin|obj|node_modules|dist|FINAL_RELEASE|PRIVATE|diagnostics?)(/|$)'
$textExtensions = @(
    '.cs', '.csproj', '.xaml', '.config', '.kt', '.kts', '.xml', '.properties',
    '.toml', '.md', '.ps1', '.iss', '.ts', '.tsx', '.css', '.html', '.json',
    '.yml', '.yaml', '.gitignore', '.gitattributes'
)

# Construct retired identifiers at runtime so this guard does not flag itself.
$retiredName = 'a' + 'ether'
$retiredProduct = $retiredName + 'link'
$retiredBrandPattern = "(?i)$retiredProduct|$retiredName"
$privateKeyMarker = 'BEGIN ' + '(RSA |EC |OPENSSH )?' + 'PRIVATE KEY'
$incompleteMarkerPattern = '(?im)\b(' + 'TO' + 'DO|FIX' + 'ME)\b'

foreach ($relativePath in $trackedFiles) {
    $normalizedPath = $relativePath.Replace('\', '/')
    $extension = [IO.Path]::GetExtension($relativePath).ToLowerInvariant()

    if ($forbiddenBinaryExtensions -contains $extension) {
        $errors.Add("Forbidden binary or credential file: $relativePath")
    }
    if ($normalizedPath -match $generatedPathPattern) {
        $errors.Add("Generated or private path is tracked: $relativePath")
    }
    if ($normalizedPath -match $retiredBrandPattern -or $normalizedPath -match '(?i)(^|/)tv box(/|$)') {
        $errors.Add("Retired product path is tracked: $relativePath")
    }

    $fullPath = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        continue
    }
    if ($relativePath -eq 'scripts/Test-ReleaseHygiene.ps1' -or
        ($textExtensions -notcontains $extension -and [IO.Path]::GetFileName($relativePath) -ne 'LICENSE')) {
        continue
    }

    $content = [IO.File]::ReadAllText($fullPath)
    if ($content -match $retiredBrandPattern) {
        $errors.Add("Retired product identifier in: $relativePath")
    }
    if ($content -match '(?i)C:\\Users\\[^\\\r\n]+' -or $content -match $privateKeyMarker) {
        $errors.Add("Personal path or private key material in: $relativePath")
    }
    if ($extension -in @('.cs', '.kt', '.kts', '.ts', '.tsx', '.ps1') -and
        $content -match $incompleteMarkerPattern) {
        $errors.Add("Incomplete implementation marker in: $relativePath")
    }
}

if ($errors.Count -gt 0) {
    $errors | Sort-Object -Unique | ForEach-Object { Write-Error $_ }
    throw "Release hygiene failed with $($errors.Count) finding(s)."
}

Write-Host 'NETRELAY_RELEASE_HYGIENE=PASS' -ForegroundColor Green
