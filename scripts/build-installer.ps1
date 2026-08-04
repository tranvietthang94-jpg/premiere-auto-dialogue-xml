[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputRoot = '',
    [string]$IsccPath = '',
    [string]$AppVersion = '0.1.0',
    [string]$SigningCertificateThumbprint = '',
    [string]$SignToolPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDirectory '..'))
$publishScript = Join-Path $scriptDirectory 'publish-win-x64.ps1'
$installerScript = Join-Path $repositoryRoot 'installer\PremiereAutoDialogueXml.iss'

if ($AppVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
    throw 'AppVersion must contain three or four numeric components.'
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $outputBase = Join-Path $repositoryRoot 'artifacts\installer'
}
elseif ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    $outputBase = [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    $outputBase = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
}

function Find-Iscc {
    param([string]$RequestedPath)

    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidates.Add([System.IO.Path]::GetFullPath($RequestedPath))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:INNO_SETUP_ISCC)) {
        $candidates.Add([System.IO.Path]::GetFullPath($env:INNO_SETUP_ISCC))
    }

    $registryPaths = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 7_is1',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 7_is1',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 7_is1'
    )
    foreach ($registryPath in $registryPaths) {
        $install = Get-ItemProperty -LiteralPath $registryPath -ErrorAction SilentlyContinue
        if ($null -ne $install -and -not [string]::IsNullOrWhiteSpace($install.InstallLocation)) {
            $candidates.Add((Join-Path $install.InstallLocation 'ISCC.exe'))
        }
    }

    $standardRoots = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe')
    )
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $standardRoots += Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'
    }
    foreach ($candidate in $standardRoots) {
        $candidates.Add($candidate)
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    throw 'Inno Setup 7 ISCC.exe was not found. Install JRSoftware.InnoSetup.7 or pass -IsccPath.'
}

function Get-InnoVersion {
    param([string]$CompilerPath)

    $compilerDirectory = [System.IO.Path]::GetDirectoryName($CompilerPath).TrimEnd('\')
    foreach ($registryPath in @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 7_is1',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 7_is1',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 7_is1'
    )) {
        $install = Get-ItemProperty -LiteralPath $registryPath -ErrorAction SilentlyContinue
        if ($null -eq $install -or [string]::IsNullOrWhiteSpace($install.InstallLocation)) {
            continue
        }
        $registeredDirectory = [System.IO.Path]::GetFullPath($install.InstallLocation).TrimEnd('\')
        if ($registeredDirectory.Equals($compilerDirectory, [StringComparison]::OrdinalIgnoreCase)) {
            return [string]$install.DisplayVersion
        }
    }

    return 'unknown'
}

function Find-SignTool {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = [System.IO.Path]::GetFullPath($RequestedPath)
        if (Test-Path -LiteralPath $resolved -PathType Leaf) {
            return $resolved
        }
        throw 'The requested SignTool.exe was not found.'
    }

    $roots = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'),
        (Join-Path $env:ProgramFiles 'Windows Kits\10\bin')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Container) }
    $candidates = @($roots | ForEach-Object {
        Get-ChildItem -LiteralPath $_ -Recurse -File -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'x64' }
    } | Sort-Object FullName -Descending)
    if ($candidates.Count -eq 0) {
        throw 'Windows SDK x64 SignTool.exe was not found.'
    }
    return $candidates[0].FullName
}

if (-not (Test-Path -LiteralPath $publishScript -PathType Leaf)) {
    throw 'The self-contained publish script was not found.'
}
if (-not (Test-Path -LiteralPath $installerScript -PathType Leaf)) {
    throw 'The Inno Setup source was not found.'
}

$compilerPath = Find-Iscc -RequestedPath $IsccPath
$compilerVersion = Get-InnoVersion -CompilerPath $compilerPath
$signingEnabled = -not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)
$normalizedSignerThumbprint = $SigningCertificateThumbprint.Replace(' ', '').ToUpperInvariant()
$signingCertificate = $null
$resolvedSignToolPath = $null
if ($signingEnabled) {
    $signingCertificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$normalizedSignerThumbprint" -ErrorAction SilentlyContinue
    if ($null -eq $signingCertificate -or -not $signingCertificate.HasPrivateKey) {
        throw 'The requested current-user code-signing certificate and private key were not found.'
    }
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    if (@($signingCertificate.EnhancedKeyUsageList | Where-Object { [string]$_.ObjectId -eq $codeSigningOid }).Count -eq 0) {
        throw 'The requested certificate is not authorized for code signing.'
    }
    $now = Get-Date
    if ($now -lt $signingCertificate.NotBefore -or $now -gt $signingCertificate.NotAfter) {
        throw 'The requested code-signing certificate is not currently valid.'
    }
    $resolvedSignToolPath = Find-SignTool -RequestedPath $SignToolPath
}
[System.IO.Directory]::CreateDirectory($outputBase) | Out-Null

$runId = '{0}-{1}' -f [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss'), [Guid]::NewGuid().ToString('N')
$runDirectory = Join-Path $outputBase "PremiereAutoDialogueXml-installer-$runId"
$payloadRoot = Join-Path $runDirectory 'payload'
$outputDirectory = Join-Path $runDirectory 'release'
$manifestPath = Join-Path $outputDirectory 'installer-manifest.json'
$checksumPath = Join-Path $outputDirectory 'SHA256SUMS.txt'

$outputBaseFull = [System.IO.Path]::GetFullPath($outputBase).TrimEnd('\')
$runDirectoryFull = [System.IO.Path]::GetFullPath($runDirectory)
if (-not $runDirectoryFull.StartsWith("$outputBaseFull\", [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Resolved installer run directory is outside the requested output root.'
}
if (Test-Path -LiteralPath $runDirectoryFull) {
    throw 'The installer run directory already exists; this script does not overwrite.'
}

try {
    [System.IO.Directory]::CreateDirectory($payloadRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

    $publishArguments = @{
        Configuration = $Configuration
        OutputRoot = $payloadRoot
    }
    if ($signingEnabled) {
        $publishArguments.SigningCertificateThumbprint = $normalizedSignerThumbprint
    }
    & $publishScript @publishArguments | Out-Host

    $publishDirectories = @(Get-ChildItem -LiteralPath $payloadRoot -Directory)
    $publishZips = @(Get-ChildItem -LiteralPath $payloadRoot -File -Filter '*.zip')
    if ($publishDirectories.Count -ne 1 -or $publishZips.Count -ne 1) {
        throw 'The publish stage did not create exactly one payload directory and one ZIP.'
    }

    $publishDirectory = $publishDirectories[0].FullName
    $publishManifestPath = Join-Path $publishDirectory 'publish-manifest.json'
    if (-not (Test-Path -LiteralPath $publishManifestPath -PathType Leaf)) {
        throw 'The payload publish manifest was not found.'
    }
    $publishManifest = Get-Content -LiteralPath $publishManifestPath -Raw | ConvertFrom-Json
    if (-not $publishManifest.selfContained -or $publishManifest.runtimeIdentifier -ne 'win-x64') {
        throw 'The installer payload is not the approved self-contained win-x64 publish.'
    }

    $outputBaseFilename = "PremiereAutoDialogueXml-Setup-$AppVersion-win-x64"
    $compilerArguments = [System.Collections.Generic.List[string]]::new()
    @(
        '/Qp',
        "/DSourceDirectory=$publishDirectory",
        "/DOutputDirectory=$outputDirectory",
        "/DOutputBaseFilename=$outputBaseFilename",
        "/DAppVersion=$AppVersion"
    ) | ForEach-Object { $compilerArguments.Add($_) }
    if ($signingEnabled) {
        $signCommand = '$q{0}$q sign /sha1 {1} /fd SHA256 /d $qPremiere Auto Dialogue XML$q $f' -f $resolvedSignToolPath, $normalizedSignerThumbprint
        $compilerArguments.Add('/DSignToolName=padxinternal')
        $compilerArguments.Add("/Spadxinternal=$signCommand")
    }
    $compilerArguments.Add($installerScript)
    & $compilerPath @compilerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
    }

    $installerPath = Join-Path $outputDirectory "$outputBaseFilename.exe"
    if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        throw 'Inno Setup did not create the expected installer.'
    }

    $installerItem = Get-Item -LiteralPath $installerPath
    $reader = $null
    $stream = [System.IO.File]::OpenRead($installerPath)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw 'Installer does not have a valid PE header.'
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or $peOffset -gt ($stream.Length - 6)) {
            throw 'Installer PE header offset is invalid.'
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw 'Installer PE signature is invalid.'
        }
        $peMachine = $reader.ReadUInt16()
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        else {
            $stream.Dispose()
        }
    }
    if ($peMachine -ne 0x8664) {
        throw ('Installer must be an x64 executable; PE machine was 0x{0:X4}.' -f $peMachine)
    }

    $installerSha256 = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
    $signature = Get-AuthenticodeSignature -LiteralPath $installerPath
    $actualSignerThumbprint = if ($null -ne $signature.SignerCertificate) { [string]$signature.SignerCertificate.Thumbprint } else { $null }
    if ($signingEnabled -and (
        [string]::IsNullOrWhiteSpace($actualSignerThumbprint) -or
        -not $actualSignerThumbprint.Equals($normalizedSignerThumbprint, [StringComparison]::OrdinalIgnoreCase))) {
        throw 'The installer signature does not match the requested certificate.'
    }
    $gitCommit = 'unknown'
    $gitResult = & git -C $repositoryRoot rev-parse HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($gitResult)) {
        $gitCommit = [string]$gitResult
    }

    $installerManifest = [ordered]@{
        schemaVersion = '1.0'
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        appName = 'Premiere Auto Dialogue XML'
        appVersion = $AppVersion
        gitCommit = $gitCommit.Trim()
        target = 'win-x64'
        minimumWindowsVersion = '10.0'
        installerPeMachine = ('0x{0:X4}' -f $peMachine)
        installScope = 'current-user'
        requiresAdministrator = $false
        selfContained = $true
        installerFramework = 'Inno Setup'
        installerFrameworkVersion = $compilerVersion
        installerFile = $installerItem.Name
        installerBytes = $installerItem.Length
        installerSha256 = $installerSha256
        authenticodeStatus = [string]$signature.Status
        signed = (-not [string]::IsNullOrWhiteSpace($actualSignerThumbprint))
        authenticodeTrusted = ($signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid)
        signerSubject = if ($null -ne $signature.SignerCertificate) { [string]$signature.SignerCertificate.Subject } else { $null }
        signerThumbprint = $actualSignerThumbprint
        signerNotAfter = if ($null -ne $signature.SignerCertificate) { $signature.SignerCertificate.NotAfter.ToUniversalTime().ToString('O') } else { $null }
        timestamped = ($null -ne $signature.TimeStamperCertificate)
        payloadManifestSha256 = (Get-FileHash -LiteralPath $publishManifestPath -Algorithm SHA256).Hash
        payloadZipSha256 = (Get-FileHash -LiteralPath $publishZips[0].FullName -Algorithm SHA256).Hash
        payloadFiles = @($publishManifest.files).Count
        modelVersion = [string]$publishManifest.modelVersion
        modelSha256 = [string]$publishManifest.modelSha256
        licenseNoticeIncluded = $true
        pythonRuntimeIncluded = $false
    }
    $installerManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    "$installerSha256 *$($installerItem.Name)" | Set-Content -LiteralPath $checksumPath -Encoding ascii

    [pscustomobject]@{
        RunDirectory = $runDirectoryFull
        InstallerPath = $installerPath
        InstallerManifestPath = $manifestPath
        ChecksumPath = $checksumPath
        PayloadManifestPath = $publishManifestPath
        InstallerBytes = $installerItem.Length
        InstallerSha256 = $installerSha256
        Signed = $installerManifest.signed
        AuthenticodeStatus = $installerManifest.authenticodeStatus
        SignerThumbprint = $installerManifest.signerThumbprint
        CompilerVersion = $compilerVersion
    }
}
catch {
    if (Test-Path -LiteralPath $runDirectoryFull -PathType Container) {
        Remove-Item -LiteralPath $runDirectoryFull -Recurse -Force
    }
    throw
}
