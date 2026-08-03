[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDirectory '..'))
$projectPath = Join-Path $repositoryRoot 'src\PremiereAutoDialogueXml.App\PremiereAutoDialogueXml.App.csproj'
$profilePath = Join-Path $repositoryRoot 'src\PremiereAutoDialogueXml.App\Properties\PublishProfiles\win-x64.pubxml'
$localDotnet = Join-Path $repositoryRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet -PathType Leaf) { $localDotnet } else { 'dotnet' }

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
        'PremiereAutoDialogueXml.runtimeconfig.json',
        'Microsoft.ML.OnnxRuntime.dll',
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
        modelSha256 = '1A153A22F4509E292A94E67D6F9B85E8DEB25B4988682B7E174C65279D8788E3'
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
