[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallerRunDirectory,

    [Parameter(Mandatory)]
    [string]$CertificateDirectory,

    [string]$OutputRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDirectory '..'))
$runDirectory = [System.IO.Path]::GetFullPath($InstallerRunDirectory)
$certificateDirectoryFull = [System.IO.Path]::GetFullPath($CertificateDirectory)
$releaseSource = Join-Path $runDirectory 'release'
$installerManifestPath = Join-Path $releaseSource 'installer-manifest.json'
$installerTestReportPath = Join-Path $releaseSource 'installer-test-report.json'
$certificateManifestPath = Join-Path $certificateDirectoryFull 'certificate-manifest.json'
$certificatePath = Join-Path $certificateDirectoryFull 'PremiereAutoDialogueXml-Internal-CodeSigning.cer'
$installGuidePath = Join-Path $repositoryRoot 'docs\CAI_DAT_WINDOWS.md'
$signingGuidePath = Join-Path $repositoryRoot 'docs\INTERNAL_CODE_SIGNING.md'

foreach ($requiredPath in @(
    $installerManifestPath,
    $installerTestReportPath,
    $certificateManifestPath,
    $certificatePath,
    $installGuidePath,
    $signingGuidePath
)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required internal release input was not found: $requiredPath"
    }
}

$installerManifest = Get-Content -LiteralPath $installerManifestPath -Raw | ConvertFrom-Json
$testReport = Get-Content -LiteralPath $installerTestReportPath -Raw | ConvertFrom-Json
$certificateManifest = Get-Content -LiteralPath $certificateManifestPath -Raw | ConvertFrom-Json
$thumbprint = ([string]$certificateManifest.thumbprint).Replace(' ', '').ToUpperInvariant()

if (-not $installerManifest.signed -or -not $installerManifest.authenticodeTrusted -or
    ([string]$installerManifest.authenticodeStatus) -ne 'Valid') {
    throw 'Installer manifest does not prove a trusted Authenticode signature.'
}
if (([string]$installerManifest.signerThumbprint) -ne $thumbprint) {
    throw 'Installer and certificate manifests use different signer thumbprints.'
}
if (-not $testReport.passed -or
    ([string]$testReport.installerSignatureStatus) -ne 'Valid' -or
    ([string]$testReport.installedAppSignatureStatus) -ne 'Valid' -or
    ([string]$testReport.uninstallerSignatureStatus) -ne 'Valid') {
    throw 'Installer test report does not prove valid installer, app and uninstaller signatures.'
}
if ($certificateManifest.privateKeyExported) {
    throw 'Certificate manifest unexpectedly reports an exported private key.'
}
$actualCerSha256 = (Get-FileHash -LiteralPath $certificatePath -Algorithm SHA256).Hash
if ($actualCerSha256 -ne [string]$certificateManifest.cerSha256) {
    throw 'Public certificate checksum does not match its manifest.'
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $outputBase = Join-Path $repositoryRoot 'artifacts\internal-signed-release'
}
elseif ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    $outputBase = [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    $outputBase = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
}
[System.IO.Directory]::CreateDirectory($outputBase) | Out-Null
$runId = '{0}-{1}' -f [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss'), [Guid]::NewGuid().ToString('N')
$outputDirectory = Join-Path $outputBase "PremiereAutoDialogueXml-internal-signed-$runId"
$outputBaseFull = [System.IO.Path]::GetFullPath($outputBase).TrimEnd('\')
$outputDirectoryFull = [System.IO.Path]::GetFullPath($outputDirectory)
if (-not $outputDirectoryFull.StartsWith("$outputBaseFull\", [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Resolved internal release directory is outside the requested output root.'
}
if (Test-Path -LiteralPath $outputDirectoryFull) {
    throw 'Internal release directory already exists; this script does not overwrite.'
}

try {
    [System.IO.Directory]::CreateDirectory($outputDirectoryFull) | Out-Null
    $installerPath = Join-Path $releaseSource ([string]$installerManifest.installerFile)
    foreach ($source in @(
        $installerPath,
        $installerManifestPath,
        $installerTestReportPath,
        $certificatePath,
        $certificateManifestPath,
        $installGuidePath,
        $signingGuidePath
    )) {
        Copy-Item -LiteralPath $source -Destination $outputDirectoryFull
    }

    $files = @(Get-ChildItem -LiteralPath $outputDirectoryFull -File | Sort-Object Name | ForEach-Object {
        [ordered]@{
            name = $_.Name
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    $internalManifest = [ordered]@{
        schemaVersion = '1.0'
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        distribution = 'private-internal-only'
        appVersion = [string]$installerManifest.appVersion
        gitCommit = [string]$installerManifest.gitCommit
        signerSubject = [string]$installerManifest.signerSubject
        signerThumbprint = $thumbprint
        signerNotAfter = [string]$installerManifest.signerNotAfter
        timestamped = [bool]$installerManifest.timestamped
        privateKeyIncluded = $false
        installerTestPassed = [bool]$testReport.passed
        files = $files
    }
    $internalManifestPath = Join-Path $outputDirectoryFull 'internal-release-manifest.json'
    $internalManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $internalManifestPath -Encoding utf8

    $checksumLines = @(Get-ChildItem -LiteralPath $outputDirectoryFull -File | Sort-Object Name | ForEach-Object {
        '{0} *{1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.Name
    })
    $checksumPath = Join-Path $outputDirectoryFull 'SHA256SUMS-INTERNAL.txt'
    $checksumLines | Set-Content -LiteralPath $checksumPath -Encoding ascii

    [pscustomobject]@{
        OutputDirectory = $outputDirectoryFull
        InstallerPath = Join-Path $outputDirectoryFull ([string]$installerManifest.installerFile)
        InstallerSha256 = [string]$installerManifest.installerSha256
        CertificatePath = Join-Path $outputDirectoryFull ([System.IO.Path]::GetFileName($certificatePath))
        CertificateSha256 = $actualCerSha256
        SignerThumbprint = $thumbprint
        Files = @(Get-ChildItem -LiteralPath $outputDirectoryFull -File).Count
        PrivateKeyIncluded = $false
    }
}
catch {
    if (Test-Path -LiteralPath $outputDirectoryFull -PathType Container) {
        Remove-Item -LiteralPath $outputDirectoryFull -Recurse -Force
    }
    throw
}
