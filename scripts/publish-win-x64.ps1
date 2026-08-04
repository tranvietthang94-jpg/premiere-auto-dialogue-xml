[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputRoot = '',
    [string]$SigningCertificateThumbprint = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDirectory '..'))
$projectPath = Join-Path $repositoryRoot 'src\PremiereAutoDialogueXml.App\PremiereAutoDialogueXml.App.csproj'
$profilePath = Join-Path $repositoryRoot 'src\PremiereAutoDialogueXml.App\Properties\PublishProfiles\win-x64.pubxml'
$modelPath = Join-Path $repositoryRoot 'models\silero-vad\v6.2.1\silero_vad.onnx'
$expectedModelSha256 = '1A153A22F4509E292A94E67D6F9B85E8DEB25B4988682B7E174C65279D8788E3'
$localDotnet = Join-Path $repositoryRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet -PathType Leaf) { $localDotnet } else { 'dotnet' }
$signingEnabled = -not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)
$normalizedSignerThumbprint = $SigningCertificateThumbprint.Replace(' ', '').ToUpperInvariant()

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
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $outputBase = Join-Path $repositoryRoot 'artifacts\publish\win-x64'
}
elseif ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    $outputBase = [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    $outputBase = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
}

if (-not (Test-Path -LiteralPath $profilePath -PathType Leaf)) {
    throw "The win-x64 publish profile was not found."
}
if (-not (Test-Path -LiteralPath $modelPath -PathType Leaf)) {
    throw "The pinned Silero VAD model was not found."
}
$actualModelSha256 = (Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash
if (-not $actualModelSha256.Equals($expectedModelSha256, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The pinned Silero VAD model checksum does not match the approved value."
}

[System.IO.Directory]::CreateDirectory($outputBase) | Out-Null
$runId = '{0}-{1}' -f [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss'), [Guid]::NewGuid().ToString('N')
$packageName = "PremiereAutoDialogueXml-win-x64-$runId"
$publishDirectory = Join-Path $outputBase $packageName
$zipPath = "$publishDirectory.zip"
if ((Test-Path -LiteralPath $publishDirectory) -or (Test-Path -LiteralPath $zipPath)) {
    throw "The publish artifact already exists; this script does not overwrite."
}

$outputBaseFull = [System.IO.Path]::GetFullPath($outputBase).TrimEnd('\')
$publishDirectoryFull = [System.IO.Path]::GetFullPath($publishDirectory)
$requiredPrefix = "$outputBaseFull\"
if (-not $publishDirectoryFull.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Resolved publish directory is outside the requested output root."
}

try {
    & $dotnet restore $projectPath --runtime win-x64
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore win-x64 failed with exit code $LASTEXITCODE."
    }

    & $dotnet publish $projectPath `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        -p:PublishProfile=$profilePath `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        --output $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish win-x64 failed with exit code $LASTEXITCODE."
    }

    $requiredFiles = @(
        'PremiereAutoDialogueXml.exe',
        'PremiereAutoDialogueXml.dll',
        'PremiereAutoDialogueXml.Audio.dll',
        'PremiereAutoDialogueXml.Core.dll',
        'PremiereAutoDialogueXml.Output.dll',
        'PremiereAutoDialogueXml.deps.json',
        'PremiereAutoDialogueXml.runtimeconfig.json',
        'hostfxr.dll',
        'hostpolicy.dll',
        'coreclr.dll',
        'clrjit.dll',
        'System.Private.CoreLib.dll',
        'PresentationCore.dll',
        'PresentationFramework.dll',
        'WindowsBase.dll',
        'Microsoft.ML.OnnxRuntime.dll',
        'onnxruntime.dll',
        'onnxruntime_providers_shared.dll',
        'THIRD-PARTY-NOTICES.txt',
        'licenses\Silero-VAD-MIT.txt'
    )
    foreach ($relativePath in $requiredFiles) {
        $candidate = Join-Path $publishDirectory $relativePath
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "Publish is missing required file: $relativePath"
        }
    }

    $onnxNative = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File -Filter 'onnxruntime.dll')
    if ($onnxNative.Count -ne 1) {
        throw "Publish must contain exactly one native win-x64 onnxruntime.dll; found $($onnxNative.Count)."
    }

    $pythonFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File |
        Where-Object { $_.Name -match '^python(?:\d+)?\.(?:exe|dll)$' })
    if ($pythonFiles.Count -ne 0) {
        throw "Publish must not contain a Python runtime."
    }

    $debugSymbols = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File -Filter '*.pdb')
    if ($debugSymbols.Count -ne 0) {
        throw "Publish must not contain PDB files with local source path metadata."
    }

    $runtimeConfigPath = Join-Path $publishDirectory 'PremiereAutoDialogueXml.runtimeconfig.json'
    $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
    $includedFrameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
    if ($includedFrameworks.Count -lt 2) {
        throw "runtimeconfig does not prove a self-contained .NET and WindowsDesktop package."
    }

    $appSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $publishDirectory 'PremiereAutoDialogueXml.exe')
    if ($signingEnabled) {
        $appExecutablePath = Join-Path $publishDirectory 'PremiereAutoDialogueXml.exe'
        $appSignature = Set-AuthenticodeSignature `
            -LiteralPath $appExecutablePath `
            -Certificate $signingCertificate `
            -HashAlgorithm SHA256 `
            -IncludeChain All
        if ($null -eq $appSignature.SignerCertificate -or
            -not $appSignature.SignerCertificate.Thumbprint.Equals($normalizedSignerThumbprint, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The app executable signature does not match the requested certificate.'
        }
    }

    $payloadFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            [ordered]@{
                path = $_.FullName.Substring($publishDirectory.Length).TrimStart('\').Replace('\', '/')
                bytes = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        })
    $manifest = [ordered]@{
        schemaVersion = '1.0'
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        packageName = $packageName
        runtimeIdentifier = 'win-x64'
        selfContained = $true
        singleFile = $false
        installer = $false
        modelVersion = '6.2.1'
        modelSha256 = $expectedModelSha256
        codeSigning = [ordered]@{
            signed = $signingEnabled
            signerSubject = if ($signingEnabled) { [string]$signingCertificate.Subject } else { $null }
            signerThumbprint = if ($signingEnabled) { $normalizedSignerThumbprint } else { $null }
            authenticodeStatus = [string]$appSignature.Status
            timestamped = $false
        }
        files = $payloadFiles
    }
    $manifestPath = Join-Path $publishDirectory 'publish-manifest.json'
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8

    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal

    [pscustomobject]@{
        PublishDirectory = $publishDirectory
        ZipPath = $zipPath
        PayloadFiles = $payloadFiles.Count
        ZipBytes = (Get-Item -LiteralPath $zipPath).Length
        InstallerCreated = $false
    }
}
catch {
    if (Test-Path -LiteralPath $zipPath -PathType Leaf) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    if (Test-Path -LiteralPath $publishDirectoryFull -PathType Container) {
        Remove-Item -LiteralPath $publishDirectoryFull -Recurse -Force
    }
    throw
}
