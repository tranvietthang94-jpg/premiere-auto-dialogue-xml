[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InputXml,

    [Parameter(Mandatory)]
    [string]$ExportedXml,

    [string]$ReportPath,

    [double]$GainToleranceDb = 0.1,

    [switch]$AllowPremiereSequenceDepthNormalization
)

$ErrorActionPreference = 'Stop'
$culture = [Globalization.CultureInfo]::InvariantCulture
$gainFilterId = '{61756678, 4761696e, 4b657947}'
$mismatchCounts = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
$mismatchExamples = [Collections.Generic.List[string]]::new()
$allowedNormalizations = [Collections.Generic.List[string]]::new()

function Add-Mismatch {
    param([string]$Category, [string]$Message)

    if ($mismatchCounts.ContainsKey($Category)) {
        $mismatchCounts[$Category]++
    } else {
        $mismatchCounts[$Category] = 1
    }
    if ($mismatchExamples.Count -lt 50) {
        $mismatchExamples.Add("[$Category] $Message")
    }
}

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
    param(
        [Xml.XmlDocument]$Document,
        [string]$Label
    )

    $directSequences = @($Document.SelectNodes('/xmeml/sequence'))
    if ($directSequences.Count -eq 1) {
        return [pscustomobject]@{
            Node = $directSequences[0]
            XPath = '/xmeml/sequence'
        }
    }
    if ($directSequences.Count -gt 1) {
        throw "$Label XML contains more than one direct sequence under xmeml."
    }

    $projectSequences = @($Document.SelectNodes(
            '/xmeml/project/children//sequence[not(ancestor::sequence)]'))
    if ($projectSequences.Count -ne 1) {
        throw "$Label XML must contain exactly one comparable top-level sequence."
    }

    return [pscustomobject]@{
        Node = $projectSequences[0]
        XPath = '/xmeml/project/children//sequence[not(ancestor::sequence)]'
    }
}

function Get-NodeText {
    param([Xml.XmlNode]$Node, [string]$XPath)

    $match = $Node.SelectSingleNode($XPath)
    if ($null -eq $match) { return '' }
    return $match.InnerText
}

function Get-FileMap {
    param([Xml.XmlDocument]$Document)

    $map = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($fileNode in @($Document.SelectNodes('//file[@id]'))) {
        $id = $fileNode.Attributes['id'].Value
        $pathUrl = Get-NodeText $fileNode 'pathurl'
        $name = Get-NodeText $fileNode 'name'
        if (-not $map.ContainsKey($id) -or -not [string]::IsNullOrWhiteSpace($pathUrl)) {
            $map[$id] = [pscustomobject]@{ Name = $name; PathUrl = $pathUrl }
        }
    }
    return $map
}

function Normalize-PathUrl {
    param([string]$PathUrl)

    if ([string]::IsNullOrWhiteSpace($PathUrl)) { return '' }
    $decoded = [Uri]::UnescapeDataString($PathUrl).Replace('\', '/').TrimEnd('/')
    return $decoded.ToUpperInvariant()
}

function Get-ClipFile {
    param([Xml.XmlNode]$Clip, $FileMap)

    $fileNode = $Clip.SelectSingleNode('file')
    if ($null -eq $fileNode) {
        return [pscustomobject]@{ Name = ''; PathUrl = '' }
    }

    $name = Get-NodeText $fileNode 'name'
    $pathUrl = Get-NodeText $fileNode 'pathurl'
    $idAttribute = $fileNode.Attributes['id']
    if ($null -ne $idAttribute -and $FileMap.ContainsKey($idAttribute.Value)) {
        $mapped = $FileMap[$idAttribute.Value]
        if ([string]::IsNullOrWhiteSpace($name)) { $name = $mapped.Name }
        if ([string]::IsNullOrWhiteSpace($pathUrl)) { $pathUrl = $mapped.PathUrl }
    }
    return [pscustomobject]@{ Name = $name; PathUrl = Normalize-PathUrl $pathUrl }
}

function Get-TotalGainDb {
    param([Xml.XmlNode]$Clip)

    $totalDb = 0.0
    foreach ($valueNode in @($Clip.SelectNodes("filter/effect[effectid='audiolevels']/parameter[parameterid='level']/value"))) {
        $factor = [double]::Parse($valueNode.InnerText, $culture)
        if ($factor -le 0.0) { throw "Audio Levels factor must be positive: $factor" }
        $totalDb += 20.0 * [Math]::Log10($factor)
    }
    foreach ($valueNode in @($Clip.SelectNodes("filter/effect[effectid='$gainFilterId']/parameter[parameterid='Gain(dB)']/value"))) {
        $factor = [double]::Parse($valueNode.InnerText, $culture)
        if ($factor -le 0.0) { throw "Gain filter factor must be positive: $factor" }
        $totalDb += 20.0 * [Math]::Log10($factor)
    }
    return $totalDb
}

function Get-MarkerSignature {
    param([Xml.XmlNode]$Marker)

    return @(
        Get-NodeText $Marker 'name'
        Get-NodeText $Marker 'comment'
        Get-NodeText $Marker 'in'
        Get-NodeText $Marker 'out'
    ) -join [char]0x1f
}

function Compare-TextField {
    param(
        [string]$Category,
        [string]$Location,
        [Xml.XmlNode]$InputNode,
        [Xml.XmlNode]$ExportedNode,
        [string]$XPath
    )

    $inputValue = Get-NodeText $InputNode $XPath
    $exportedValue = Get-NodeText $ExportedNode $XPath
    if (-not ([StringComparer]::Ordinal).Equals($inputValue, $exportedValue)) {
        Add-Mismatch $Category "$Location ${XPath}: '$inputValue' -> '$exportedValue'."
    }
}

$inputPath = [IO.Path]::GetFullPath($InputXml)
$exportedPath = [IO.Path]::GetFullPath($ExportedXml)
foreach ($path in @($inputPath, $exportedPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "XML was not found: $path"
    }
}
if (([StringComparer]::OrdinalIgnoreCase).Equals($inputPath, $exportedPath)) {
    throw 'Input XML and Premiere-exported XML must be different files.'
}
if ($GainToleranceDb -le 0.0) {
    throw 'GainToleranceDb must be greater than zero.'
}

$inputDocument = Open-XmlDocument $inputPath
$exportedDocument = Open-XmlDocument $exportedPath
$inputSelection = Get-ComparableSequence $inputDocument 'Input'
$exportedSelection = Get-ComparableSequence $exportedDocument 'Premiere-exported'
$inputSequence = $inputSelection.Node
$exportedSequence = $exportedSelection.Node

foreach ($field in @('name', 'duration', 'rate/timebase', 'rate/ntsc', 'media/audio/format/samplecharacteristics/samplerate', 'media/audio/format/samplecharacteristics/channelcount')) {
    Compare-TextField 'sequence' 'sequence' $inputSequence $exportedSequence $field
}
$sequenceDepthXPath = 'media/audio/format/samplecharacteristics/depth'
$inputSequenceDepth = Get-NodeText $inputSequence $sequenceDepthXPath
$exportedSequenceDepth = Get-NodeText $exportedSequence $sequenceDepthXPath
if (-not ([StringComparer]::Ordinal).Equals($inputSequenceDepth, $exportedSequenceDepth)) {
    if ($AllowPremiereSequenceDepthNormalization -and
        $inputSequenceDepth -eq '24' -and
        $exportedSequenceDepth -eq '16') {
        $allowedNormalizations.Add("sequence-depth-24-to-16")
    } else {
        Add-Mismatch 'sequence' "sequence ${sequenceDepthXPath}: '$inputSequenceDepth' -> '$exportedSequenceDepth'."
    }
}

$inputTracks = @($inputSequence.SelectNodes('media/audio/track'))
$exportedTracks = @($exportedSequence.SelectNodes('media/audio/track'))
if ($inputTracks.Count -ne $exportedTracks.Count) {
    Add-Mismatch 'track-count' "Track count: $($inputTracks.Count) -> $($exportedTracks.Count)."
}

$inputFileMap = Get-FileMap $inputDocument
$exportedFileMap = Get-FileMap $exportedDocument
$inputClipCount = 0
$exportedClipCount = 0
$inputEnabledCount = 0
$exportedEnabledCount = 0
$inputDisabledCount = 0
$exportedDisabledCount = 0
$maxGainDeltaDb = 0.0
$comparedClipCount = 0
$trackCount = [Math]::Min($inputTracks.Count, $exportedTracks.Count)
for ($trackIndex = 0; $trackIndex -lt $trackCount; $trackIndex++) {
    $inputTrack = $inputTracks[$trackIndex]
    $exportedTrack = $exportedTracks[$trackIndex]
    $location = "track $($trackIndex + 1)"
    foreach ($field in @('enabled', 'locked', 'outputchannelindex')) {
        Compare-TextField 'track' $location $inputTrack $exportedTrack $field
    }
    foreach ($attributeName in @('currentExplodedTrackIndex', 'totalExplodedTrackCount', 'premiereTrackType')) {
        $inputAttribute = $inputTrack.Attributes[$attributeName]
        $exportedAttribute = $exportedTrack.Attributes[$attributeName]
        $inputValue = if ($null -eq $inputAttribute) { '' } else { $inputAttribute.Value }
        $exportedValue = if ($null -eq $exportedAttribute) { '' } else { $exportedAttribute.Value }
        if (-not ([StringComparer]::Ordinal).Equals($inputValue, $exportedValue)) {
            Add-Mismatch 'track' "$location @${attributeName}: '$inputValue' -> '$exportedValue'."
        }
    }

    $inputClips = @($inputTrack.SelectNodes('clipitem'))
    $exportedClips = @($exportedTrack.SelectNodes('clipitem'))
    $inputClipCount += $inputClips.Count
    $exportedClipCount += $exportedClips.Count
    if ($inputClips.Count -ne $exportedClips.Count) {
        Add-Mismatch 'clip-count' "$location clip count: $($inputClips.Count) -> $($exportedClips.Count)."
    }

    $clipCount = [Math]::Min($inputClips.Count, $exportedClips.Count)
    for ($clipIndex = 0; $clipIndex -lt $clipCount; $clipIndex++) {
        $inputClip = $inputClips[$clipIndex]
        $exportedClip = $exportedClips[$clipIndex]
        $clipLocation = "$location clip $($clipIndex + 1)"
        foreach ($field in @('name', 'enabled', 'start', 'end', 'in', 'out', 'pproTicksIn', 'pproTicksOut', 'rate/timebase', 'rate/ntsc', 'sourcetrack/mediatype', 'sourcetrack/trackindex')) {
            Compare-TextField 'clip' $clipLocation $inputClip $exportedClip $field
        }

        $inputFile = Get-ClipFile $inputClip $inputFileMap
        $exportedFile = Get-ClipFile $exportedClip $exportedFileMap
        if (-not ([StringComparer]::Ordinal).Equals($inputFile.Name, $exportedFile.Name)) {
            Add-Mismatch 'media' "$clipLocation file name: '$($inputFile.Name)' -> '$($exportedFile.Name)'."
        }
        if (-not ([StringComparer]::OrdinalIgnoreCase).Equals($inputFile.PathUrl, $exportedFile.PathUrl)) {
            Add-Mismatch 'media' "$clipLocation pathurl changed."
        }

        $inputGainDb = Get-TotalGainDb $inputClip
        $exportedGainDb = Get-TotalGainDb $exportedClip
        $gainDeltaDb = [Math]::Abs($inputGainDb - $exportedGainDb)
        $maxGainDeltaDb = [Math]::Max($maxGainDeltaDb, $gainDeltaDb)
        if ($gainDeltaDb -gt $GainToleranceDb) {
            Add-Mismatch 'gain' "$clipLocation gain $([Math]::Round($inputGainDb, 6)) dB -> $([Math]::Round($exportedGainDb, 6)) dB; delta $([Math]::Round($gainDeltaDb, 6)) dB."
        }

        if ((Get-NodeText $inputClip 'enabled').ToUpperInvariant() -eq 'TRUE') { $inputEnabledCount++ } else { $inputDisabledCount++ }
        if ((Get-NodeText $exportedClip 'enabled').ToUpperInvariant() -eq 'TRUE') { $exportedEnabledCount++ } else { $exportedDisabledCount++ }
        $comparedClipCount++
    }
}

$inputMarkers = @($inputSequence.SelectNodes('marker'))
$exportedMarkers = @($exportedSequence.SelectNodes('marker'))
if ($inputMarkers.Count -ne $exportedMarkers.Count) {
    Add-Mismatch 'marker-count' "Marker count: $($inputMarkers.Count) -> $($exportedMarkers.Count)."
}
$inputMarkerCounts = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
$exportedMarkerCounts = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
foreach ($marker in $inputMarkers) {
    $signature = Get-MarkerSignature $marker
    if ($inputMarkerCounts.ContainsKey($signature)) { $inputMarkerCounts[$signature]++ } else { $inputMarkerCounts[$signature] = 1 }
}
foreach ($marker in $exportedMarkers) {
    $signature = Get-MarkerSignature $marker
    if ($exportedMarkerCounts.ContainsKey($signature)) { $exportedMarkerCounts[$signature]++ } else { $exportedMarkerCounts[$signature] = 1 }
}
foreach ($signature in @($inputMarkerCounts.Keys + $exportedMarkerCounts.Keys | Sort-Object -Unique)) {
    $inputCount = if ($inputMarkerCounts.ContainsKey($signature)) { $inputMarkerCounts[$signature] } else { 0 }
    $exportedCount = if ($exportedMarkerCounts.ContainsKey($signature)) { $exportedMarkerCounts[$signature] } else { 0 }
    if ($inputCount -ne $exportedCount) {
        $parts = $signature -split [char]0x1f
        Add-Mismatch 'marker' "Marker '$($parts[0])' at $($parts[2])-$($parts[3]) count: $inputCount -> $exportedCount."
    }
}
$markerOrderChanged = $false
$markerCount = [Math]::Min($inputMarkers.Count, $exportedMarkers.Count)
for ($markerIndex = 0; $markerIndex -lt $markerCount; $markerIndex++) {
    if (-not ([StringComparer]::Ordinal).Equals(
            (Get-MarkerSignature $inputMarkers[$markerIndex]),
            (Get-MarkerSignature $exportedMarkers[$markerIndex]))) {
        $markerOrderChanged = $true
        break
    }
}

$inputAudioLevelFilters = @($inputSequence.SelectNodes("media/audio/track/clipitem/filter/effect[effectid='audiolevels']")).Count
$exportedAudioLevelFilters = @($exportedSequence.SelectNodes("media/audio/track/clipitem/filter/effect[effectid='audiolevels']")).Count
$inputGainFilters = @($inputSequence.SelectNodes("media/audio/track/clipitem/filter/effect[effectid='$gainFilterId']")).Count
$exportedGainFilters = @($exportedSequence.SelectNodes("media/audio/track/clipitem/filter/effect[effectid='$gainFilterId']")).Count
$status = if ($mismatchCounts.Count -eq 0) { 'phase09-roundtrip-compatible' } else { 'phase09-roundtrip-incompatible' }
$result = [ordered]@{
    SchemaVersion = if ($AllowPremiereSequenceDepthNormalization) { '1.2' } else { '1.1' }
    Status = $status
    GainToleranceDb = $GainToleranceDb
    Input = [ordered]@{
        FileName = [IO.Path]::GetFileName($inputPath)
        Sha256 = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash
        Bytes = (Get-Item -LiteralPath $inputPath).Length
        SequenceXPath = $inputSelection.XPath
    }
    Exported = [ordered]@{
        FileName = [IO.Path]::GetFileName($exportedPath)
        Sha256 = (Get-FileHash -LiteralPath $exportedPath -Algorithm SHA256).Hash
        Bytes = (Get-Item -LiteralPath $exportedPath).Length
        SequenceXPath = $exportedSelection.XPath
    }
    Sequence = [ordered]@{
        Name = Get-NodeText $exportedSequence 'name'
        DurationFrames = [long](Get-NodeText $exportedSequence 'duration')
        AudioTracks = $exportedTracks.Count
    }
    Clips = [ordered]@{
        Input = $inputClipCount
        Exported = $exportedClipCount
        Compared = $comparedClipCount
        InputEnabled = $inputEnabledCount
        ExportedEnabled = $exportedEnabledCount
        InputDisabled = $inputDisabledCount
        ExportedDisabled = $exportedDisabledCount
        MaximumGainDeltaDb = $maxGainDeltaDb
    }
    Markers = [ordered]@{
        Input = $inputMarkers.Count
        Exported = $exportedMarkers.Count
        OrderChangedByPremiere = $markerOrderChanged
    }
    EffectNormalization = [ordered]@{
        InputAudioLevelFilters = $inputAudioLevelFilters
        ExportedAudioLevelFilters = $exportedAudioLevelFilters
        InputGainFilters = $inputGainFilters
        ExportedGainFilters = $exportedGainFilters
    }
    MismatchCounts = $mismatchCounts
    MismatchExamples = @($mismatchExamples)
}
if ($AllowPremiereSequenceDepthNormalization) {
    $result.AllowedNormalizations = @($allowedNormalizations)
}
$json = $result | ConvertTo-Json -Depth 8

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $resolvedReportPath = [IO.Path]::GetFullPath($ReportPath)
    $reportDirectory = [IO.Path]::GetDirectoryName($resolvedReportPath)
    if ([string]::IsNullOrWhiteSpace($reportDirectory) -or -not (Test-Path -LiteralPath $reportDirectory -PathType Container)) {
        throw 'The report parent directory must exist.'
    }
    if (Test-Path -LiteralPath $resolvedReportPath) {
        throw "Report already exists; refusing to overwrite it: $resolvedReportPath"
    }
    $temporaryPath = Join-Path $reportDirectory ".$(Split-Path -Leaf $resolvedReportPath).$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporaryPath, $resolvedReportPath)
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
}

$json
if ($mismatchCounts.Count -gt 0) { exit 1 }
