[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ExportedXml,

    [string]$RenderedWav,

    [double]$ToleranceDb = 0.1
)

$ErrorActionPreference = 'Stop'
$xmlPath = [IO.Path]::GetFullPath($ExportedXml)
if (-not (Test-Path -LiteralPath $xmlPath -PathType Leaf)) {
    throw "Không tìm thấy XML đã export: $xmlPath"
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
if ($null -eq $sequence) { throw 'XML không có sequence trực tiếp.' }
$clips = @($sequence.SelectNodes('media/audio/track/clipitem'))
if ($clips.Count -ne 5) { throw "Cần đúng 5 clip Phase 00, nhận được $($clips.Count)." }

$expected = @(
    [pscustomobject]@{ Name = 'UNITY'; Enabled = 'TRUE'; In = 0; Out = 50; Gains = @() },
    [pscustomobject]@{ Name = 'PLUS_6_DB'; Enabled = 'TRUE'; In = 50; Out = 100; Gains = @(6.0) },
    [pscustomobject]@{ Name = 'PLUS_12_DB'; Enabled = 'TRUE'; In = 100; Out = 150; Gains = @(12.0) },
    [pscustomobject]@{ Name = 'PLUS_18_DB_STACKED'; Enabled = 'TRUE'; In = 150; Out = 200; Gains = @(9.0, 9.0) },
    [pscustomobject]@{ Name = 'DISABLED'; Enabled = 'FALSE'; In = 200; Out = 250; Gains = @() }
)

$clipResults = @()
for ($index = 0; $index -lt $clips.Count; $index++) {
    $clip = $clips[$index]
    $want = $expected[$index]
    $enabled = $clip.SelectSingleNode('enabled').InnerText.ToUpperInvariant()
    $sourceIn = [int]$clip.SelectSingleNode('in').InnerText
    $sourceOut = [int]$clip.SelectSingleNode('out').InnerText
    if ($enabled -ne $want.Enabled) { throw "Clip $($index + 1) sai trạng thái Enabled." }
    if ($sourceIn -ne $want.In -or $sourceOut -ne $want.Out) { throw "Clip $($index + 1) sai source trim." }

    $gainDb = @($clip.SelectNodes("filter/effect[effectid='audiolevels']/parameter[parameterid='level']/value") | ForEach-Object {
        $linear = [double]::Parse($_.InnerText, [Globalization.CultureInfo]::InvariantCulture)
        20.0 * [Math]::Log10($linear)
    })
    if ($gainDb.Count -ne $want.Gains.Count) { throw "Clip $($index + 1) sai số Audio Levels." }
    for ($gainIndex = 0; $gainIndex -lt $gainDb.Count; $gainIndex++) {
        if ([Math]::Abs($gainDb[$gainIndex] - $want.Gains[$gainIndex]) -gt 0.01) {
            throw "Clip $($index + 1) sai Audio Levels tại filter $($gainIndex + 1)."
        }
    }
    $clipResults += [pscustomobject]@{
        Name = $clip.SelectSingleNode('name').InnerText
        Enabled = $enabled
        SourceIn = $sourceIn
        SourceOut = $sourceOut
        GainDb = @($gainDb | ForEach-Object { [Math]::Round($_, 3) })
    }
}

$audioResults = @()
if (-not [string]::IsNullOrWhiteSpace($RenderedWav)) {
    $renderPath = [IO.Path]::GetFullPath($RenderedWav)
    if (-not (Test-Path -LiteralPath $renderPath -PathType Leaf)) {
        throw "Không tìm thấy WAV đã render: $renderPath"
    }
    $ffmpeg = Get-Command ffmpeg -ErrorAction Stop
    $expectedRelative = @(0.0, 6.0, 12.0, 18.0)
    $measured = @()
    for ($segment = 0; $segment -lt 5; $segment++) {
        $start = $segment * 2
        $output = & $ffmpeg.Source -hide_banner -nostats -ss $start -t 2 -i $renderPath -af volumedetect -f null NUL 2>&1
        $line = $output | Select-String -Pattern 'max_volume:\s+(-?inf|[-\d.]+)\s+dB' | Select-Object -Last 1
        if ($null -eq $line) { throw "Không đo được peak segment $($segment + 1)." }
        $raw = $line.Matches[0].Groups[1].Value
        $peak = if ($raw -eq '-inf') { [double]::NegativeInfinity } else { [double]::Parse($raw, [Globalization.CultureInfo]::InvariantCulture) }
        $measured += $peak
    }
    $baseline = $measured[0]
    for ($segment = 0; $segment -lt 4; $segment++) {
        $relative = $measured[$segment] - $baseline
        if ([Math]::Abs($relative - $expectedRelative[$segment]) -gt $ToleranceDb) {
            throw "Segment $($segment + 1) lệch gain tương đối: đo $relative dB, cần $($expectedRelative[$segment]) dB."
        }
        $audioResults += [pscustomobject]@{ Segment = $segment + 1; PeakDbfs = $measured[$segment]; RelativeGainDb = [Math]::Round($relative, 3) }
    }
    if (-not [double]::IsNegativeInfinity($measured[4]) -and $measured[4] -gt -90.0) {
        throw "Segment Disabled vẫn có peak $($measured[4]) dBFS."
    }
    $audioResults += [pscustomobject]@{ Segment = 5; PeakDbfs = $measured[4]; RelativeGainDb = $null }
}

[pscustomobject]@{
    Status = 'phase00-compatible'
    SequenceName = $sequence.SelectSingleNode('name').InnerText
    Xml = $clipResults
    Audio = $audioResults
} | ConvertTo-Json -Depth 8

