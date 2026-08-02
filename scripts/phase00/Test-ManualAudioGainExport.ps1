[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ExportedXml,

    [Parameter(Mandatory)]
    [string]$RenderedWav,

    [Parameter(Mandatory)]
    [string]$BaselineWav,

    [double]$ExpectedGainDb = 3.0,
    [double]$ToleranceDb = 0.1
)

$ErrorActionPreference = 'Stop'
$culture = [Globalization.CultureInfo]::InvariantCulture
$failures = [Collections.Generic.List[string]]::new()

function Resolve-InputFile {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Label)
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "$Label was not found: $resolved"
    }
    return $resolved
}

function Measure-PeakDbfs {
    param(
        [Parameter(Mandatory)][string]$FfmpegPath,
        [Parameter(Mandatory)][string]$WavePath,
        [Parameter(Mandatory)][int]$StartSeconds
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
    if ($process.ExitCode -ne 0) { throw "FFmpeg failed at $StartSeconds seconds: $standardError" }
    $match = [regex]::Matches("$standardOutput`n$standardError", 'max_volume:\s+(-?inf|[-\d.]+)\s+dB') | Select-Object -Last 1
    if ($null -eq $match) { throw "No peak was reported at $StartSeconds seconds." }
    return [double]::Parse($match.Groups[1].Value, $culture)
}

$xmlPath = Resolve-InputFile -Path $ExportedXml -Label 'Exported XML'
$renderPath = Resolve-InputFile -Path $RenderedWav -Label 'Rendered WAV'
$baselinePath = Resolve-InputFile -Path $BaselineWav -Label 'Baseline WAV'

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
if ($clips.Count -ne 7) { $failures.Add("Expected 7 audio clips, found $($clips.Count).") }

$gainValue = $null
$audioLevelDb = $null
if ($clips.Count -gt 0) {
    $firstClip = $clips[0]
    $nameNode = $firstClip.SelectSingleNode('name')
    if ($null -eq $nameNode -or $nameNode.InnerText -ne 'PHASE00_UNITY_REFERENCE') {
        $failures.Add('The first clip is not PHASE00_UNITY_REFERENCE.')
    }

    $gainNodes = @($firstClip.SelectNodes("filter/effect[effectid='{61756678, 4761696e, 4b657947}']/parameter[parameterid='Gain(dB)']/value"))
    if ($gainNodes.Count -ne 1) {
        $failures.Add("Expected one independent Premiere Gain(dB) value, found $($gainNodes.Count).")
    } else {
        $gainValue = [double]::Parse($gainNodes[0].InnerText, $culture)
        if ([Math]::Abs($gainValue - $ExpectedGainDb) -gt $ToleranceDb) {
            $failures.Add("Premiere Gain(dB) is $gainValue instead of $ExpectedGainDb.")
        }
    }

    $levelNode = $firstClip.SelectSingleNode("filter/effect[effectid='audiolevels']/parameter[parameterid='level']/value")
    if ($null -eq $levelNode) {
        $failures.Add('The first clip has no independent Audio Levels value.')
    } else {
        $linear = [double]::Parse($levelNode.InnerText, $culture)
        $audioLevelDb = 20.0 * [Math]::Log10($linear)
        if ([Math]::Abs($audioLevelDb) -gt $ToleranceDb) {
            $failures.Add("The first clip Audio Levels value changed by $audioLevelDb dB.")
        }
    }
}

$ffmpeg = Get-Command ffmpeg -ErrorAction Stop
$segments = @()
for ($index = 0; $index -lt 7; $index++) {
    $baselinePeak = Measure-PeakDbfs -FfmpegPath $ffmpeg.Source -WavePath $baselinePath -StartSeconds ($index * 2)
    $renderedPeak = Measure-PeakDbfs -FfmpegPath $ffmpeg.Source -WavePath $renderPath -StartSeconds ($index * 2)
    $delta = $renderedPeak - $baselinePeak
    $expectedDelta = if ($index -eq 0) { $ExpectedGainDb } else { 0.0 }
    $pass = [Math]::Abs($delta - $expectedDelta) -le $ToleranceDb
    if (-not $pass) {
        $failures.Add("PCM segment $($index + 1) changed by $delta dB; expected $expectedDelta dB.")
    }
    $segments += [pscustomobject]@{
        Index = $index + 1
        BaselinePeakDbfs = $baselinePeak
        RenderedPeakDbfs = $renderedPeak
        DeltaDb = [Math]::Round($delta, 3)
        ExpectedDeltaDb = $expectedDelta
        Pass = $pass
    }
}

[pscustomobject]@{
    Status = if ($failures.Count -eq 0) { 'manual-audio-gain-compatible' } else { 'manual-audio-gain-incompatible' }
    SequenceName = $sequence.SelectSingleNode('name').InnerText
    GainEffectId = '{61756678, 4761696e, 4b657947}'
    GainDb = $gainValue
    AudioLevelDb = if ($null -eq $audioLevelDb) { $null } else { [Math]::Round($audioLevelDb, 3) }
    Segments = $segments
    Failures = @($failures)
} | ConvertTo-Json -Depth 8

if ($failures.Count -gt 0) { exit 1 }
