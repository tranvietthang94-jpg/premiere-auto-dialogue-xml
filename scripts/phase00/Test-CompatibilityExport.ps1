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

$xmlPath = [IO.Path]::GetFullPath($ExportedXml)
if (-not (Test-Path -LiteralPath $xmlPath -PathType Leaf)) {
    throw "Exported XML was not found: $xmlPath"
}

$settings = [Xml.XmlReaderSettings]::new()
$settings.DtdProcessing = [Xml.DtdProcessing]::Ignore
$settings.XmlResolver = $null
$reader = [Xml.XmlReader]::Create($xmlPath, $settings)
$document = [Xml.XmlDocument]::new()
$document.XmlResolver = $null
try {
    $document.Load($reader)
} finally {
    $reader.Dispose()
}

$sequence = $document.SelectSingleNode('/xmeml/sequence')
if ($null -eq $sequence) { throw 'XML has no direct sequence element.' }
$clips = @($sequence.SelectNodes('media/audio/track/clipitem'))
$expected = @(
    [pscustomobject]@{ Name = 'UNITY'; Enabled = 'TRUE'; Start = 0; End = 50; In = 0; Out = 50; GainDb = 0.0 },
    [pscustomobject]@{ Name = 'PLUS_6_DB'; Enabled = 'TRUE'; Start = 50; End = 100; In = 50; Out = 100; GainDb = 6.0 },
    [pscustomobject]@{ Name = 'PLUS_12_DB'; Enabled = 'TRUE'; Start = 100; End = 150; In = 100; Out = 150; GainDb = 12.0 },
    [pscustomobject]@{ Name = 'PLUS_18_DB_STACKED'; Enabled = 'TRUE'; Start = 150; End = 200; In = 150; Out = 200; GainDb = 18.0 },
    [pscustomobject]@{ Name = 'DISABLED'; Enabled = 'FALSE'; Start = 200; End = 250; In = 200; Out = 250; GainDb = 0.0 }
)

if ($clips.Count -ne $expected.Count) {
    $failures.Add("Expected $($expected.Count) Phase 00 clips, found $($clips.Count).")
}

$clipResults = @()
$clipCount = [Math]::Min($clips.Count, $expected.Count)
for ($index = 0; $index -lt $clipCount; $index++) {
    $clip = $clips[$index]
    $want = $expected[$index]
    $name = $clip.SelectSingleNode('name').InnerText
    $enabled = $clip.SelectSingleNode('enabled').InnerText.ToUpperInvariant()
    $start = [int]$clip.SelectSingleNode('start').InnerText
    $end = [int]$clip.SelectSingleNode('end').InnerText
    $sourceIn = [int]$clip.SelectSingleNode('in').InnerText
    $sourceOut = [int]$clip.SelectSingleNode('out').InnerText
    $ticksIn = [Int64]$clip.SelectSingleNode('pproTicksIn').InnerText
    $ticksOut = [Int64]$clip.SelectSingleNode('pproTicksOut').InnerText

    if ($name -ne "PHASE00_$($want.Name)") {
        $failures.Add("Clip $($index + 1) has an unexpected name: $name.")
    }
    if ($enabled -ne $want.Enabled) {
        $failures.Add("Clip $($index + 1) has an unexpected Enabled value: $enabled.")
    }
    if ($start -ne $want.Start -or $end -ne $want.End) {
        $failures.Add("Clip $($index + 1) has an unexpected timeline range: $start-$end.")
    }
    if ($sourceIn -ne $want.In -or $sourceOut -ne $want.Out) {
        $failures.Add("Clip $($index + 1) has an unexpected source range: $sourceIn-$sourceOut.")
    }
    if ($ticksIn -ne ([Int64]$want.In * $ticksPerFrame) -or $ticksOut -ne ([Int64]$want.Out * $ticksPerFrame)) {
        $failures.Add("Clip $($index + 1) has unexpected pproTicksIn/Out values.")
    }

    $gainDb = @($clip.SelectNodes("filter/effect[effectid='audiolevels']/parameter[parameterid='level']/value") | ForEach-Object {
        $linear = [double]::Parse($_.InnerText, $culture)
        20.0 * [Math]::Log10($linear)
    })
    $totalGainDb = ($gainDb | Measure-Object -Sum).Sum
    if ($null -eq $totalGainDb) { $totalGainDb = 0.0 }
    if ([Math]::Abs($totalGainDb - $want.GainDb) -gt 0.01) {
        $failures.Add("Clip $($index + 1) Audio Levels total is $([Math]::Round($totalGainDb, 3)) dB; expected $($want.GainDb) dB.")
    }

    $clipResults += [pscustomobject]@{
        Name = $name
        Enabled = $enabled
        TimelineRange = @($start, $end)
        SourceRange = @($sourceIn, $sourceOut)
        AudioLevelFiltersDb = @($gainDb | ForEach-Object { [Math]::Round($_, 3) })
        TotalGainDb = [Math]::Round($totalGainDb, 3)
        ExpectedGainDb = $want.GainDb
    }
}

function Measure-PeakDbfs {
    param(
        [Parameter(Mandatory)]
        [string]$FfmpegPath,
        [Parameter(Mandatory)]
        [string]$WavePath,
        [Parameter(Mandatory)]
        [int]$StartSeconds
    )

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
    if ($process.ExitCode -ne 0) {
        throw "FFmpeg failed while measuring the segment at $StartSeconds seconds: $standardError"
    }
    $match = [regex]::Matches("$standardOutput`n$standardError", 'max_volume:\s+(-?inf|[-\d.]+)\s+dB') | Select-Object -Last 1
    if ($null -eq $match) { throw "No peak was reported for the segment at $StartSeconds seconds." }
    $raw = $match.Groups[1].Value
    if ($raw -eq '-inf') { return [double]::NegativeInfinity }
    return [double]::Parse($raw, $culture)
}

$audioResults = @()
if (-not [string]::IsNullOrWhiteSpace($RenderedWav)) {
    $renderPath = [IO.Path]::GetFullPath($RenderedWav)
    if (-not (Test-Path -LiteralPath $renderPath -PathType Leaf)) {
        throw "Rendered WAV was not found: $renderPath"
    }
    $ffmpeg = Get-Command ffmpeg -ErrorAction Stop
    $expectedRelative = @(0.0, 6.0, 12.0, 18.0)
    $measured = @()
    for ($segment = 0; $segment -lt 5; $segment++) {
        $measured += Measure-PeakDbfs -FfmpegPath $ffmpeg.Source -WavePath $renderPath -StartSeconds ($segment * 2)
    }
    $baseline = $measured[0]
    for ($segment = 0; $segment -lt 4; $segment++) {
        $relative = $measured[$segment] - $baseline
        if ([Math]::Abs($relative - $expectedRelative[$segment]) -gt $ToleranceDb) {
            $failures.Add("Segment $($segment + 1) relative gain is $relative dB; expected $($expectedRelative[$segment]) dB.")
        }
        $audioResults += [pscustomobject]@{
            Segment = $segment + 1
            PeakDbfs = $measured[$segment]
            RelativeGainDb = [Math]::Round($relative, 3)
            ExpectedRelativeGainDb = $expectedRelative[$segment]
        }
    }
    if (-not [double]::IsNegativeInfinity($measured[4]) -and $measured[4] -gt -90.0) {
        $failures.Add("The Disabled segment has a peak of $($measured[4]) dBFS.")
    }
    $audioResults += [pscustomobject]@{
        Segment = 5
        PeakDbfs = $measured[4]
        RelativeGainDb = $null
        ExpectedRelativeGainDb = $null
    }
}

$result = [pscustomobject]@{
    Status = if ($failures.Count -eq 0) { 'phase00-compatible' } else { 'phase00-incompatible' }
    SequenceName = $sequence.SelectSingleNode('name').InnerText
    Xml = $clipResults
    Audio = $audioResults
    Failures = @($failures)
}
$result | ConvertTo-Json -Depth 8
if ($failures.Count -gt 0) { exit 1 }
