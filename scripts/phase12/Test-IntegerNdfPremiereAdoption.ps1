[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceXml,

    [Parameter(Mandatory)]
    [string]$CandidateXml,

    [Parameter(Mandatory)]
    [string]$AuditJson,

    [Parameter(Mandatory)]
    [string]$RenderedTrackWav,

    [Parameter(Mandatory)]
    [string]$PremiereExportedXml,

    [Parameter(Mandatory)]
    [string]$EvidenceDirectory,

    [ValidateRange(1, 999)]
    [int]$TrackIndex = 1
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$dotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
$verifyPcm = Join-Path $repoRoot 'tools\PremiereAutoDialogueXml.VerifyPcm\bin\Release\net10.0\PremiereAutoDialogueXml.VerifyPcm.dll'
$roundTripValidator = Join-Path $repoRoot 'scripts\phase09\Test-Phase09RoundTrip.ps1'

foreach ($tool in @($dotnet, $verifyPcm, $roundTripValidator)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) {
        throw "Required validation tool was not found: $tool"
    }
}

$sourcePath = [IO.Path]::GetFullPath($SourceXml)
$candidatePath = [IO.Path]::GetFullPath($CandidateXml)
$auditPath = [IO.Path]::GetFullPath($AuditJson)
$renderedPath = [IO.Path]::GetFullPath($RenderedTrackWav)
$exportedPath = [IO.Path]::GetFullPath($PremiereExportedXml)
foreach ($path in @($sourcePath, $candidatePath, $auditPath, $renderedPath, $exportedPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Evidence input was not found: $path"
    }
}
if ([StringComparer]::OrdinalIgnoreCase.Equals($candidatePath, $exportedPath)) {
    throw 'Candidate XML and Premiere-exported XML must be different immutable files.'
}

$evidenceRoot = [IO.Path]::GetFullPath($EvidenceDirectory)
[IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null
$audit = Get-Content -Raw -LiteralPath $auditPath | ConvertFrom-Json
if ($audit.schemaVersion -ne '1.8' -or $null -eq $audit.sequenceTiming) {
    throw 'Phase 12 adoption requires audit schema 1.8 with sequenceTiming.'
}

$timing = $audit.sequenceTiming
$frameRate = [int]$timing.frameRate
if ($frameRate -notin @(24, 30) -or
    [bool]$timing.ntsc -or
    [int]$timing.audioSampleRate -ne 48000 -or
    48000 % $frameRate -ne 0 -or
    [int]$timing.samplesPerFrame -ne 48000 / $frameRate -or
    $timing.frameGridPolicy -ne 'phase12-integer-ndf-exact-frame-grid-v1') {
    throw 'Audit timing is outside the Phase 12 Premiere adoption contract.'
}

$sourceSha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
$candidateSha256 = (Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash
$auditSha256 = (Get-FileHash -LiteralPath $auditPath -Algorithm SHA256).Hash
if (-not [StringComparer]::OrdinalIgnoreCase.Equals($sourceSha256, [string]$audit.sourceXmlSha256)) {
    throw 'Source XML SHA-256 does not match the audit.'
}
if (-not [StringComparer]::OrdinalIgnoreCase.Equals($candidateSha256, [string]$audit.outputXmlSha256)) {
    throw 'Candidate XML SHA-256 does not match the audit.'
}

function Open-XmlDocument([string]$Path) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Ignore
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    $document = [Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    try { $document.Load($reader) } finally { $reader.Dispose() }
    return $document
}

$candidateDocument = Open-XmlDocument $candidatePath
$candidateSequence = $candidateDocument.SelectSingleNode('/xmeml/sequence')
if ($null -eq $candidateSequence) {
    throw 'Candidate XML must contain one direct sequence.'
}
$sequenceTimebase = [int]$candidateSequence.SelectSingleNode('rate/timebase').InnerText
$sequenceNtsc = $candidateSequence.SelectSingleNode('rate/ntsc').InnerText
$clipRates = @($candidateSequence.SelectNodes('media/audio/track/clipitem/rate'))
if ($sequenceTimebase -ne $frameRate -or $sequenceNtsc -ne 'FALSE' -or $clipRates.Count -eq 0 -or
    @($clipRates | Where-Object {
            [int]$_.SelectSingleNode('timebase').InnerText -ne $frameRate -or
            $_.SelectSingleNode('ntsc').InnerText -ne 'FALSE'
        }).Count -gt 0) {
    throw 'Candidate XML sequence/clip rate metadata does not match the audit NDF grid.'
}

$pcmReportPath = Join-Path $evidenceRoot "phase12-${frameRate}fps-pcm-track${TrackIndex}.json"
$roundTripReportPath = Join-Path $evidenceRoot "phase12-${frameRate}fps-roundtrip.json"
$summaryPath = Join-Path $evidenceRoot "phase12-${frameRate}fps-adoption-report.json"
foreach ($target in @($pcmReportPath, $roundTripReportPath, $summaryPath)) {
    if (Test-Path -LiteralPath $target) {
        throw "Evidence report already exists; refusing to overwrite: $target"
    }
}

& $dotnet $verifyPcm $auditPath $TrackIndex $renderedPath $pcmReportPath $sourcePath | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "PCM validation failed with exit code $LASTEXITCODE."
}

& $roundTripValidator `
    -InputXml $candidatePath `
    -ExportedXml $exportedPath `
    -ReportPath $roundTripReportPath | Out-Null

$pcm = Get-Content -Raw -LiteralPath $pcmReportPath | ConvertFrom-Json
$roundTrip = Get-Content -Raw -LiteralPath $roundTripReportPath | ConvertFrom-Json
if (-not [bool]$pcm.validation.allWithinTolerance -or
    $roundTrip.status -ne 'phase09-roundtrip-compatible') {
    throw 'Premiere PCM or XML round-trip gate did not pass.'
}

$summary = [ordered]@{
    SchemaVersion = '1.0'
    Status = 'phase12-integer-ndf-premiere-adopted'
    FrameRate = $frameRate
    Ntsc = $false
    AudioSampleRate = 48000
    SamplesPerFrame = [int]$timing.samplesPerFrame
    TrackIndex = $TrackIndex
    Source = [ordered]@{ FileName = Split-Path -Leaf $sourcePath; Sha256 = $sourceSha256 }
    Candidate = [ordered]@{ FileName = Split-Path -Leaf $candidatePath; Sha256 = $candidateSha256 }
    Audit = [ordered]@{ FileName = Split-Path -Leaf $auditPath; Sha256 = $auditSha256 }
    RenderedPcm = [ordered]@{ FileName = Split-Path -Leaf $renderedPath; Sha256 = (Get-FileHash -LiteralPath $renderedPath -Algorithm SHA256).Hash }
    PremiereExportedXml = [ordered]@{ FileName = Split-Path -Leaf $exportedPath; Sha256 = (Get-FileHash -LiteralPath $exportedPath -Algorithm SHA256).Hash }
    Pcm = [ordered]@{
        ReportFileName = Split-Path -Leaf $pcmReportPath
        PhraseCount = [int]$pcm.validation.phraseCount
        PassedPhraseCount = [int]$pcm.validation.passedPhraseCount
        FailedPhraseCount = [int]$pcm.validation.failedPhraseCount
        UncappedPhraseCount = [int]$pcm.validation.uncappedPhraseCount
        TargetPassedPhraseCount = [int]$pcm.validation.targetPassedPhraseCount
        AllWithinTolerance = [bool]$pcm.validation.allWithinTolerance
    }
    RoundTrip = [ordered]@{
        ReportFileName = Split-Path -Leaf $roundTripReportPath
        Status = $roundTrip.status
        ComparedClipCount = [int]$roundTrip.clips.compared
        EnabledClipCount = [int]$roundTrip.clips.exportedEnabled
        DisabledClipCount = [int]$roundTrip.clips.exportedDisabled
        MarkerCount = [int]$roundTrip.markers.exported
        MaximumGainDeltaDb = [double]$roundTrip.clips.maximumGainDeltaDb
    }
}

[IO.File]::WriteAllText(
    $summaryPath,
    ($summary | ConvertTo-Json -Depth 7),
    [Text.UTF8Encoding]::new($false))
$summary | ConvertTo-Json -Depth 7
