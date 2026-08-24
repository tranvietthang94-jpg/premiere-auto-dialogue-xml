[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CandidateXml,

    [Parameter(Mandatory)]
    [string]$ExportedXml,

    [Parameter(Mandatory)]
    [string]$BatchReport,

    [Parameter(Mandatory)]
    [string]$ReportPath,

    [double]$GainToleranceDb = 0.1
)

$ErrorActionPreference = 'Stop'
$effectName = 'Cross Fade ( 0dB)'
$effectId = 'KGAudioTransCrossFade0dB'

function Open-XmlDocument {
    param([string]$Path)

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Ignore
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    $document = [Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    try {
        $document.Load($reader)
    } finally {
        $reader.Dispose()
    }
    return $document
}

function Get-ComparableSequence {
    param([Xml.XmlDocument]$Document, [string]$Label)

    $direct = @($Document.SelectNodes('/xmeml/sequence'))
    if ($direct.Count -eq 1) { return $direct[0] }
    if ($direct.Count -gt 1) { throw "$Label XML có nhiều hơn một sequence trực tiếp." }
    $project = @($Document.SelectNodes('/xmeml/project/children//sequence[not(ancestor::sequence)]'))
    if ($project.Count -ne 1) { throw "$Label XML phải có đúng một top-level sequence." }
    return $project[0]
}

function Get-NodeText {
    param([Xml.XmlNode]$Node, [string]$XPath)

    $match = $Node.SelectSingleNode($XPath)
    if ($null -eq $match) { return '' }
    return $match.InnerText
}

function Assert-Transition {
    param(
        [Xml.XmlNode]$Track,
        [int]$TrackIndex,
        [long]$BoundaryFrame,
        [long]$FrameTicks,
        [string]$Label
    )

    $matches = @($Track.SelectNodes("transitionitem[start='$BoundaryFrame' and end='$BoundaryFrame']"))
    if ($matches.Count -ne 1) {
        throw "$Label A$($TrackIndex)/frame $BoundaryFrame không có đúng một transition."
    }
    $transition = $matches[0]
    $halfFrameTicks = [long]($FrameTicks / 2)
    $expectedTicksIn = [long]($BoundaryFrame * $FrameTicks - $halfFrameTicks)
    $expectedTicksOut = [long]($BoundaryFrame * $FrameTicks + $halfFrameTicks)
    if ((Get-NodeText $transition 'alignment') -ne 'center' -or
        (Get-NodeText $transition 'effect/name') -ne $effectName -or
        (Get-NodeText $transition 'effect/effectid') -ne $effectId -or
        [long](Get-NodeText $transition 'pproTicksIn') -ne $expectedTicksIn -or
        [long](Get-NodeText $transition 'pproTicksOut') -ne $expectedTicksOut -or
        [long](Get-NodeText $transition 'cutPointTicks') -ne $halfFrameTicks) {
        throw "$Label transition A$($TrackIndex)/frame $BoundaryFrame không khớp Constant Gain một frame."
    }
}

$candidatePath = [IO.Path]::GetFullPath($CandidateXml)
$exportedPath = [IO.Path]::GetFullPath($ExportedXml)
$batchPath = [IO.Path]::GetFullPath($BatchReport)
$resultPath = [IO.Path]::GetFullPath($ReportPath)
foreach ($inputPath in @($candidatePath, $exportedPath, $batchPath)) {
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) {
        throw "Không tìm thấy input: $inputPath"
    }
}
$resultDirectory = [IO.Path]::GetDirectoryName($resultPath)
if ([string]::IsNullOrWhiteSpace($resultDirectory) -or
    -not (Test-Path -LiteralPath $resultDirectory -PathType Container)) {
    throw 'Thư mục chứa report phải tồn tại.'
}
if (Test-Path -LiteralPath $resultPath) {
    throw "Report đã tồn tại; từ chối ghi đè: $resultPath"
}

$batch = Get-Content -Raw -LiteralPath $batchPath | ConvertFrom-Json -Depth 30
$candidateHash = (Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash
$batchHash = (Get-FileHash -LiteralPath $batchPath -Algorithm SHA256).Hash
if ($batch.candidate.outputXmlSha256 -ne $candidateHash -or
    $batch.outputXmlFileName -ne [IO.Path]::GetFileName($candidatePath)) {
    throw 'Batch report không thuộc candidate XML đầu vào.'
}
$boundaries = @($batch.candidate.boundaries)
if ($boundaries.Count -lt 1 -or
    $boundaries.Count -ne [int]$batch.plan.selectedCount -or
    $boundaries.Count -ne [int]$batch.candidate.transitionCount) {
    throw 'Batch report có transition count không nhất quán.'
}

$phase09Temp = Join-Path $resultDirectory ".phase15-phase09-$([Guid]::NewGuid().ToString('N')).json"
try {
    $phase09Script = Join-Path $PSScriptRoot '..\phase09\Test-Phase09RoundTrip.ps1'
    $pwshPath = (Get-Process -Id $PID).Path
    & $pwshPath -NoProfile -File $phase09Script `
        -InputXml $candidatePath `
        -ExportedXml $exportedPath `
        -ReportPath $phase09Temp `
        -GainToleranceDb $GainToleranceDb | Out-Null
    if ($LASTEXITCODE -notin @(0, 1) -or -not (Test-Path -LiteralPath $phase09Temp)) {
        throw "Phase 09 comparator không tạo được report hợp lệ; exit code $LASTEXITCODE."
    }
    $phase09 = Get-Content -Raw -LiteralPath $phase09Temp | ConvertFrom-Json -Depth 20

    $candidateDocument = Open-XmlDocument $candidatePath
    $exportedDocument = Open-XmlDocument $exportedPath
    $candidateSequence = Get-ComparableSequence $candidateDocument 'Candidate'
    $exportedSequence = Get-ComparableSequence $exportedDocument 'Premiere export'
    $candidateTracks = @($candidateSequence.SelectNodes('media/audio/track'))
    $exportedTracks = @($exportedSequence.SelectNodes('media/audio/track'))
    if ($candidateTracks.Count -ne $exportedTracks.Count) {
        throw 'Premiere export đổi số audio track.'
    }

    $timebase = [int](Get-NodeText $candidateSequence 'rate/timebase')
    if ($timebase -notin @(24, 25, 30)) { throw 'Candidate không dùng frame rate Phase 12.' }
    $frameTicks = [long]$batch.candidate.frameTicks
    $halfFrameTicks = [long]$batch.candidate.halfFrameTicks
    $expectedMismatch = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($boundary in $boundaries) {
        $TrackIndex = [int]$boundary.trackIndex
        if ($TrackIndex -lt 1 -or $TrackIndex -gt $candidateTracks.Count) {
            throw "Batch report trỏ track A$TrackIndex không tồn tại."
        }
        $frame = [long]$boundary.boundaryFrame
        $candidateTrack = $candidateTracks[$TrackIndex - 1]
        $exportedTrack = $exportedTracks[$TrackIndex - 1]
        Assert-Transition $candidateTrack $TrackIndex $frame $frameTicks 'Candidate'
        Assert-Transition $exportedTrack $TrackIndex $frame $frameTicks 'Premiere export'

        $candidateClips = @($candidateTrack.SelectNodes('clipitem'))
        $leftIndex = -1
        $rightIndex = -1
        for ($index = 0; $index -lt $candidateClips.Count; $index++) {
            $clipId = $candidateClips[$index].Attributes['id'].Value
            if ($clipId -eq $boundary.leftClipItemId) { $leftIndex = $index }
            if ($clipId -eq $boundary.rightClipItemId) { $rightIndex = $index }
        }
        if ($leftIndex -lt 0 -or $rightIndex -ne ($leftIndex + 1)) {
            throw "Batch boundary A$TrackIndex/frame $frame không khớp cặp clip liền nhau."
        }

        $clipNumberLeft = $leftIndex + 1
        $clipNumberRight = $rightIndex + 1
        $leftSourceTicks = [long](Get-NodeText $candidateClips[$leftIndex] 'pproTicksOut')
        $rightSourceTicks = [long](Get-NodeText $candidateClips[$rightIndex] 'pproTicksIn')
        if ($leftSourceTicks -ne $rightSourceTicks) {
            throw "Candidate A$TrackIndex/frame $frame không có source tick liên tục."
        }
        [void]$expectedMismatch.Add("[clip] track $TrackIndex clip $clipNumberLeft end: '$frame' -> '-1'.")
        [void]$expectedMismatch.Add("[clip] track $TrackIndex clip $clipNumberLeft pproTicksOut: '$leftSourceTicks' -> '$($leftSourceTicks + $halfFrameTicks)'.")
        [void]$expectedMismatch.Add("[clip] track $TrackIndex clip $clipNumberRight start: '$frame' -> '-1'.")
        [void]$expectedMismatch.Add("[clip] track $TrackIndex clip $clipNumberRight pproTicksIn: '$rightSourceTicks' -> '$($rightSourceTicks - $halfFrameTicks)'.")
    }

    $candidateTransitionCount = @($candidateSequence.SelectNodes('media/audio/track/transitionitem')).Count
    $exportedTransitionCount = @($exportedSequence.SelectNodes('media/audio/track/transitionitem')).Count
    if ($candidateTransitionCount -ne $boundaries.Count -or
        $exportedTransitionCount -ne $boundaries.Count) {
        throw 'Candidate hoặc Premiere export có transition thừa/thiếu.'
    }

    $mismatchProperties = @($phase09.mismatchCounts.PSObject.Properties)
    if ($mismatchProperties.Count -ne 1 -or
        $mismatchProperties[0].Name -ne 'clip' -or
        [int]$mismatchProperties[0].Value -ne $expectedMismatch.Count) {
        throw 'Premiere export có mismatch ngoài normalization transition đã khóa.'
    }
    $actualMismatch = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in @($phase09.mismatchExamples)) { [void]$actualMismatch.Add([string]$item) }
    if (-not $actualMismatch.SetEquals($expectedMismatch)) {
        throw 'Premiere export không khớp chính xác bốn normalization cho mỗi transition.'
    }

    $result = [ordered]@{
        SchemaVersion = '1.0'
        Status = 'phase15-multiboundary-premiere-roundtrip-compatible'
        CreatedAtUtc = [DateTimeOffset]::UtcNow
        Candidate = [ordered]@{
            FileName = [IO.Path]::GetFileName($candidatePath)
            Sha256 = $candidateHash
        }
        Exported = [ordered]@{
            FileName = [IO.Path]::GetFileName($exportedPath)
            Sha256 = (Get-FileHash -LiteralPath $exportedPath -Algorithm SHA256).Hash
        }
        BatchReport = [ordered]@{
            FileName = [IO.Path]::GetFileName($batchPath)
            Sha256 = $batchHash
            SelectionStreamSha256 = $batch.plan.selectionStreamSha256
        }
        TransitionCount = $boundaries.Count
        AllowedClipNormalizationCount = $expectedMismatch.Count
        Clips = $phase09.clips
        Markers = $phase09.markers
        EffectNormalization = $phase09.effectNormalization
    }
    $json = $result | ConvertTo-Json -Depth 10
    $temporaryPath = Join-Path $resultDirectory ".$([IO.Path]::GetFileName($resultPath)).$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporaryPath, $resultPath)
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
    $json
} finally {
    if (Test-Path -LiteralPath $phase09Temp) { Remove-Item -LiteralPath $phase09Temp -Force }
}
