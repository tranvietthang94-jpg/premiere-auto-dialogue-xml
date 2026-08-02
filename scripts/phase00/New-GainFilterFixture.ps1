[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'private-artifacts\phase00\3'
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null

$ffmpeg = Get-Command ffmpeg -ErrorAction Stop
$tonePath = Join-Path $outputRoot 'phase00-gain-filter-tone.wav'
$xmlPath = Join-Path $outputRoot 'phase00-gain-filter-input.xml'

& $ffmpeg.Source -hide_banner -loglevel error -y `
    -f lavfi -i 'sine=frequency=1000:sample_rate=48000:duration=14' `
    -af 'volume=-30dB' -ac 1 -c:a pcm_s24le $tonePath
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $tonePath -PathType Leaf)) {
    throw 'FFmpeg could not create the Phase 00 Gain-filter WAV.'
}

$culture = [Globalization.CultureInfo]::InvariantCulture
$ticksPerFrame = [Int64]10160640000
$segments = @(
    [pscustomobject]@{ Name = 'UNITY_REFERENCE'; Start = 0; End = 50; LevelDb = 0.0; GainDb = 0.0; Kind = 'reference' },
    [pscustomobject]@{ Name = 'PLUS_12_LEVEL_REFERENCE'; Start = 50; End = 100; LevelDb = 12.0; GainDb = 0.0; Kind = 'reference' },
    [pscustomobject]@{ Name = 'PLUS_3_GAIN_REFERENCE'; Start = 100; End = 150; LevelDb = 0.0; GainDb = 3.0; Kind = 'reference' },
    [pscustomobject]@{ Name = 'PLUS_18_GAIN_ONLY'; Start = 150; End = 200; LevelDb = 0.0; GainDb = 18.0; Kind = 'candidate' },
    [pscustomobject]@{ Name = 'PLUS_15_GAIN_ONLY'; Start = 200; End = 250; LevelDb = 0.0; GainDb = 15.0; Kind = 'observation' },
    [pscustomobject]@{ Name = 'PLUS_18_LEVEL12_GAIN6'; Start = 250; End = 300; LevelDb = 12.0; GainDb = 6.0; Kind = 'candidate' },
    [pscustomobject]@{ Name = 'PLUS_18_LEVEL6_GAIN12'; Start = 300; End = 350; LevelDb = 6.0; GainDb = 12.0; Kind = 'candidate' }
)

function Escape-Xml([string]$Value) {
    return [Security.SecurityElement]::Escape($Value)
}

function New-GainFilter([double]$GainDb) {
    if ([Math]::Abs($GainDb) -lt 0.000001) { return '' }
    $value = $GainDb.ToString('0.###', $culture)
    return @"
						<filter>
							<effect>
								<name>Gain</name>
								<effectid>{61756678, 4761696e, 4b657947}</effectid>
								<effecttype>filter</effecttype>
								<mediatype>audio</mediatype>
								<parameter authoringApp="PremierePro">
									<parameterid>Gain(dB)</parameterid>
									<name>Gain(dB)</name>
									<valuemin>-96</valuemin>
									<valuemax>96</valuemax>
									<value>$value</value>
								</parameter>
							</effect>
						</filter>
"@
}

function New-AudioLevelFilter([double]$GainDb) {
    $linear = [Math]::Pow(10.0, $GainDb / 20.0).ToString('0.#########', $culture)
    return @"
						<filter>
							<effect>
								<name>Audio Levels</name>
								<effectid>audiolevels</effectid>
								<effectcategory>audiolevels</effectcategory>
								<effecttype>audiolevels</effecttype>
								<mediatype>audio</mediatype>
								<pproBypass>false</pproBypass>
								<parameter authoringApp="PremierePro">
									<parameterid>level</parameterid>
									<name>Level</name>
									<valuemin>0</valuemin>
									<valuemax>3.98109</valuemax>
									<value>$linear</value>
								</parameter>
							</effect>
						</filter>
"@
}

$toneUrl = ([Uri]$tonePath).AbsoluteUri -replace '^file:///([A-Za-z]):', 'file://localhost/$1%3a'
$clipXml = [Text.StringBuilder]::new()
for ($index = 0; $index -lt $segments.Count; $index++) {
    $segment = $segments[$index]
    $clipNumber = $index + 1
    $ticksIn = [Int64]$segment.Start * $ticksPerFrame
    $ticksOut = [Int64]$segment.End * $ticksPerFrame
    $fileElement = if ($index -eq 0) {
@"
						<file id="file-gain-filter-1">
							<name>phase00-gain-filter-tone.wav</name>
							<pathurl>$(Escape-Xml $toneUrl)</pathurl>
							<rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate>
							<duration>350</duration>
							<timecode><rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate><string>00:00:00:00</string><frame>0</frame><displayformat>NDF</displayformat></timecode>
							<media><audio><samplecharacteristics><depth>24</depth><samplerate>48000</samplerate></samplecharacteristics><channelcount>1</channelcount></audio></media>
						</file>
"@
    } else {
        "`t`t`t`t`t`t<file id=`"file-gain-filter-1`"/>`n"
    }
    $filters = (New-GainFilter -GainDb $segment.GainDb) + (New-AudioLevelFilter -GainDb $segment.LevelDb)
    [void]$clipXml.Append(@"
					<clipitem id="clipitem-gain-filter-$clipNumber" premiereChannelType="mono">
						<masterclipid>masterclip-gain-filter-1</masterclipid>
						<name>PHASE00_$($segment.Name)</name>
						<enabled>TRUE</enabled>
						<duration>350</duration>
						<rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate>
						<start>$($segment.Start)</start><end>$($segment.End)</end>
						<in>$($segment.Start)</in><out>$($segment.End)</out>
						<pproTicksIn>$ticksIn</pproTicksIn><pproTicksOut>$ticksOut</pproTicksOut>
$fileElement$filters						<sourcetrack><mediatype>audio</mediatype><trackindex>1</trackindex></sourcetrack>
					</clipitem>
"@)
}

$sequenceUuid = '00000000-0000-4000-8000-000000000003'
$xml = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE xmeml>
<xmeml version="4">
	<sequence id="sequence-phase00-gain-filter">
		<uuid>$sequenceUuid</uuid>
		<duration>350</duration>
		<rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate>
		<name>PHASE00_GAIN_FILTER_COMPATIBILITY</name>
		<timecode><rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate><string>00:00:00:00</string><frame>0</frame><displayformat>NDF</displayformat></timecode>
		<media>
			<video>
				<format><samplecharacteristics><rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate><width>1920</width><height>1080</height><anamorphic>FALSE</anamorphic><pixelaspectratio>square</pixelaspectratio><fielddominance>none</fielddominance></samplecharacteristics></format>
				<track><enabled>TRUE</enabled><locked>FALSE</locked></track>
			</video>
			<audio>
				<numOutputChannels>2</numOutputChannels>
				<format><samplecharacteristics><depth>24</depth><samplerate>48000</samplerate></samplecharacteristics></format>
				<outputs>
					<group><index>1</index><numchannels>1</numchannels><downmix>0</downmix><channel><index>1</index></channel></group>
					<group><index>2</index><numchannels>1</numchannels><downmix>0</downmix><channel><index>2</index></channel></group>
				</outputs>
				<track TL.SQTrackAudioKeyframeStyle="0" TL.SQTrackShy="0" TL.SQTrackExpandedHeight="41" TL.SQTrackExpanded="0" MZ.TrackTargeted="1" PannerCurrentValue="0.5" PannerIsInverted="true" PannerStartKeyframe="-91445760000000000,0.5,0,0,0,0,0,0" PannerName="Balance" currentExplodedTrackIndex="0" totalExplodedTrackCount="1" premiereTrackType="Stereo">
$clipXml					<enabled>TRUE</enabled><locked>FALSE</locked><outputchannelindex>1</outputchannelindex>
				</track>
			</audio>
		</media>
	</sequence>
</xmeml>
"@

[IO.File]::WriteAllText($xmlPath, $xml, [Text.UTF8Encoding]::new($false))
[pscustomobject]@{
    XmlPath = $xmlPath
    TonePath = $tonePath
    SequenceSeconds = 14
    Cases = @($segments | ForEach-Object { $_.Name })
} | ConvertTo-Json -Depth 4
