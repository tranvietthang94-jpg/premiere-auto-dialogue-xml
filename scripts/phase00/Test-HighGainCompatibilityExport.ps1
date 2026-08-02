[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ExportedXml,

    [string]$RenderedWav,

    [double]$ToleranceDb = 0.1
)

$ErrorActionPreference = 'Stop'
$culture = [Globalization.CultureInfo]::InvariantCulture
$ticksPerFrame = [Int64]10160640000
$failures = [Collections.Generic.List[string]]::new()
$expected = @(
    [pscustomobject]@{ Name = 'UNITY_REFERENCE'; GainDb = 0.0; Kind = 'reference' },
    [pscustomobject]@{ Name = 'PLUS_12_REFERENCE'; GainDb = 12.0; Kind = 'reference' },
    [pscustomobject]@{ Name = 'PLUS_15_SINGLE_EXTENDED'; GainDb = 15.0; Kind = 'observation' },
    [pscustomobject]@{ Name = 'PLUS_18_SINGLE_EXTENDED'; GainDb = 18.0; Kind = 'candidate' },
    [pscustomobject]@{ Name = 'PLUS_18_SINGLE_LEGACY_MAX'; GainDb = 18.0; Kind = 'candidate' },
    [pscustomobject]@{ Name = 'PLUS_18_STACKED_12_6'; GainDb = 18.0; Kind = 'candidate' },
    [pscustomobject]@{ Name = 'PLUS_18_STACKED_6_12'; GainDb = 18.0; Kind = 'candidate' }
)

$xmlPath = [IO.Path]::GetFullPath($ExportedXml)
if (-not (Test-Path -LiteralPath $xmlPath -PathType Leaf)) { throw "Exported XML was not found: $xmlPath" }
$settings = [Xml.XmlReaderSettings]::new()
$settings.DtdProcessing = [Xml.DtdProcessing]::Ignore
$settings.XmlResolver = $null
$reader = [Xml.XmlReader]::Create($xmlPath, $settings)
$document = [Xml.XmlDocument]::new()
$document.XmlResolver = $null
try { $document.Load($reader) } finally { $reader.Dispose() }

$sequence = $document.SelectSingleNode('/xmeml/sequence')
if ($null -eq $sequence) { throw 'XML has no direct sequence element.' }
$clips = @($sequence.SelectNodes('media/audio/track/clipitem'))
if ($clips.Count -ne $expected.Count) {
    $failures.Add("Expected $($expected.Count) high-gain clips, found $($clips.Count).")
}

$caseResults = @()
$clipCount = [Math]::Min($clips.Count, $expected.Count)
for ($index = 0; $index -lt $clipCount; $index++) {
    $clip = $clips[$index]
    $want = $expected[$index]
    $start = [int]$clip.SelectSingleNode('start').InnerText
    $end = [int]$clip.SelectSingleNode('end').InnerText
    $sourceIn = [int]$clip.SelectSingleNode('in').InnerText
    $sourceOut = [int]$clip.SelectSingleNode('out').InnerText
    $ticksIn = [Int64]$clip.SelectSingleNode('pproTicksIn').InnerText
    $ticksOut = [Int64]$clip.SelectSingleNode('pproTicksOut').InnerText
    $expectedStart = $index * 50
    $expectedEnd = $expectedStart + 50
    $name = $clip.SelectSingleNode('name').InnerText
    if ($name -ne "PHASE00_$($want.Name)") { $failures.Add("Clip $($index + 1) has an unexpected name: $name.") }
    if ($start -ne $expectedStart -or $end -ne $expectedEnd) { $failures.Add("Clip $($index + 1) has an unexpected timeline range: $start-$end.") }
    if ($sourceIn -ne $expectedStart -or $sourceOut -ne $expectedEnd) { $failures.Add("Clip $($index + 1) has an unexpected source range: $sourceIn-$sourceOut.") }
    if ($ticksIn -ne ([Int64]$expectedStart * $ticksPerFrame) -or $ticksOut -ne ([Int64]$expectedEnd * $ticksPerFrame)) { $failures.Add("Clip $($index + 1) has unexpected pproTicksIn/Out values.") }

    $filterGainDb = @($clip.SelectNodes("filter/effect[effectid='audiolevels']/parameter[parameterid='level']/value") | ForEach-Object {
        $linear = [double]::Parse($_.InnerText, $culture)
        20.0 * [Math]::Log10($linear)
    })
    $totalXmlGainDb = ($filterGainDb | Measure-Object -Sum).Sum
    if ($null -eq $totalXmlGainDb) { $totalXmlGainDb = 0.0 }
    $caseResults += [pscustomobject]@{
        Name = $want.Name
        Kind = $want.Kind
        ExpectedGainDb = $want.GainDb
        XmlFiltersDb = @($filterGainDb | ForEach-Object { [Math]::Round($_, 3) })
        XmlTotalGainDb = [Math]::Round($totalXmlGainDb, 3)
        XmlPass = [Math]::Abs($totalXmlGainDb - $want.GainDb) -le $ToleranceDb
        PcmPeakDbfs = $null
        PcmRelativeGainDb = $null
        PcmPass = $null
    }
}

function Measure-PeakDbfs {
    param([string]$FfmpegPath, [string]$WavePath, [int]$StartSeconds)
    $processInfo = [Diagnostics.ProcessStartInfo]::new()
    $processInfo.FileName = $FfmpegPath
    $processInfo.Arguments = "-hide_banner -nostats -ss $StartSeconds -t 2 -i `"$WavePath`" -af volumedetect -f null NUL"
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $processInfo
    if (-not $process.Start()) { throw 'FFmpeg could not be started.' }
    $standardOutput = $process.StandardOutput.ReadToEnd()
    $standardError = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "FFmpeg failed at $StartSeconds seconds: $standardError" }
    $match = [regex]::Matches("$standardOutput`n$standardError", 'max_volume:\s+(-?inf|[-\d.]+)\s+dB') | Select-Object -Last 1
    if ($null -eq $match) { throw "No peak was reported at $StartSeconds seconds." }
    $raw = $match.Groups[1].Value
    if ($raw -eq '-inf') { return [double]::NegativeInfinity }
    return [double]::Parse($raw, $culture)
}

$hasRenderedAudio = -not [string]::IsNullOrWhiteSpace($RenderedWav)
if ($hasRenderedAudio) {
    $renderPath = [IO.Path]::GetFullPath($RenderedWav)
    if (-not (Test-Path -LiteralPath $renderPath -PathType Leaf)) { throw "Rendered WAV was not found: $renderPath" }
    $ffmpeg = Get-Command ffmpeg -ErrorAction Stop
    $measured = @()
    for ($index = 0; $index -lt $caseResults.Count; $index++) {
        $measured += Measure-PeakDbfs -FfmpegPath $ffmpeg.Source -WavePath $renderPath -StartSeconds ($index * 2)
    }
    $baseline = $measured[0]
    for ($index = 0; $index -lt $caseResults.Count; $index++) {
        $relative = $measured[$index] - $baseline
        $caseResults[$index].PcmPeakDbfs = $measured[$index]
        $caseResults[$index].PcmRelativeGainDb = [Math]::Round($relative, 3)
        $caseResults[$index].PcmPass = [Math]::Abs($relative - $caseResults[$index].ExpectedGainDb) -le $ToleranceDb
    }
}

if (-not $hasRenderedAudio) {
    foreach ($case in $caseResults) {
        if (-not $case.XmlPass) { $failures.Add("Input case $($case.Name) does not encode its intended XML gain.") }
    }
    $status = if ($failures.Count -eq 0) { 'high-gain-input-valid' } else { 'high-gain-input-invalid' }
    $compatibleCandidates = @()
} else {
    foreach ($case in @($caseResults | Where-Object { $_.Kind -eq 'reference' })) {
        if (-not $case.XmlPass -or -not $case.PcmPass) { $failures.Add("Reference case $($case.Name) did not survive round-trip.") }
    }
    $compatibleCandidates = @($caseResults | Where-Object { $_.Kind -eq 'candidate' -and $_.XmlPass -and $_.PcmPass } | ForEach-Object { $_.Name })
    if ($compatibleCandidates.Count -eq 0) { $failures.Add('No +18 dB XML encoding survived both XML and PCM round-trip.') }
    $status = if ($failures.Count -eq 0) { 'high-gain-compatible' } else { 'high-gain-incompatible' }
}

[pscustomobject]@{
    Status = $status
    SequenceName = $sequence.SelectSingleNode('name').InnerText
    Cases = $caseResults
    CompatibleCandidates = $compatibleCandidates
    Failures = @($failures)
} | ConvertTo-Json -Depth 8
if ($failures.Count -gt 0) { exit 1 }
