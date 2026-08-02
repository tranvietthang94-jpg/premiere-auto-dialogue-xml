[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'private-artifacts\phase00'
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null

$ffmpeg = Get-Command ffmpeg -ErrorAction Stop
$tonePath = Join-Path $outputRoot 'phase00-high-gain-tone.wav'
$xmlPath = Join-Path $outputRoot 'phase00-high-gain-input.xml'

& $ffmpeg.Source -hide_banner -loglevel error -y `
    -f lavfi -i 'sine=frequency=1000:sample_rate=48000:duration=14' `
    -af 'volume=-30dB' -ac 1 -c:a pcm_s24le $tonePath
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $tonePath -PathType Leaf)) {
    throw 'FFmpeg could not create the Phase 00 high-gain WAV.'
}

$culture = [Globalization.CultureInfo]::InvariantCulture
$ticksPerFrame = [Int64]10160640000
$segments = @(
    [pscustomobject]@{ Name = 'UNITY_REFERENCE'; Start = 0; End = 50; Filters = @() },
    [pscustomobject]@{ Name = 'PLUS_12_REFERENCE'; Start = 50; End = 100; Filters = @([pscustomobject]@{ GainDb = 12.0; MaxDb = 12.0 }) },
    [pscustomobject]@{ Name = 'PLUS_15_SINGLE_EXTENDED'; Start = 100; End = 150; Filters = @([pscustomobject]@{ GainDb = 15.0; MaxDb = 15.0 }) },
    [pscustomobject]@{ Name = 'PLUS_18_SINGLE_EXTENDED'; Start = 150; End = 200; Filters = @([pscustomobject]@{ GainDb = 18.0; MaxDb = 18.0 }) },
    [pscustomobject]@{ Name = 'PLUS_18_SINGLE_LEGACY_MAX'; Start = 200; End = 250; Filters = @([pscustomobject]@{ GainDb = 18.0; MaxDb = 12.0 }) },
    [pscustomobject]@{ Name = 'PLUS_18_STACKED_12_6'; Start = 250; End = 300; Filters = @([pscustomobject]@{ GainDb = 12.0; MaxDb = 12.0 }, [pscustomobject]@{ GainDb = 6.0; MaxDb = 12.0 }) },
    [pscustomobject]@{ Name = 'PLUS_18_STACKED_6_12'; Start = 300; End = 350; Filters = @([pscustomobject]@{ GainDb = 6.0; MaxDb = 12.0 }, [pscustomobject]@{ GainDb = 12.0; MaxDb = 12.0 }) }
)

function Escape-Xml([string]$Value) {
    return [Security.SecurityElement]::Escape($Value)
}

function New-AudioLevelFilter {
    param(
        [Parameter(Mandatory)]
        [double]$GainDb,
        [Parameter(Mandatory)]
        [double]$MaxDb
    )

    $linear = [Math]::Pow(10.0, $GainDb / 20.0).ToString('0.#########', $culture)
    $linearMax = [Math]::Pow(10.0, $MaxDb / 20.0).ToString('0.#########', $culture)
    return @"
						<filter>
							<effect>
								<name>Audio Levels</name>
								<effectid>audiolevels</effectid>
								<effectcategory>audiolevels</effectcategory>
								<effecttype>audiolevels</effecttype>
								<mediatype>audio</mediatype>
								<parameter authoringApp="PremierePro">
									<parameterid>level</parameterid>
									<name>Level</name>
									<valuemin>0</valuemin>
									<valuemax>$linearMax</valuemax>
									<value>$linear</value>
								</parameter>
								<pproBypass>false</pproBypass>
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
						<file id="file-high-gain-1">
							<name>phase00-high-gain-tone.wav</name>
							<pathurl>$(Escape-Xml $toneUrl)</pathurl>
							<rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate>
							<duration>350</duration>
							<timecode><rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate><string>00:00:00:00</string><frame>0</frame><displayformat>NDF</displayformat></timecode>
							<media><audio><samplecharacteristics><depth>24</depth><samplerate>48000</samplerate></samplecharacteristics><channelcount>1</channelcount></audio></media>
						</file>
"@
    } else {
        "`t`t`t`t`t`t<file id=`"file-high-gain-1`"/>`n"
    }
    $filters = @($segment.Filters | ForEach-Object { New-AudioLevelFilter -GainDb $_.GainDb -MaxDb $_.MaxDb }) -join ''
    [void]$clipXml.Append(@"
					<clipitem id="clipitem-high-gain-$clipNumber" premiereChannelType="mono">
						<masterclipid>masterclip-high-gain-1</masterclipid>
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

$sequenceUuid = [Guid]::NewGuid().ToString()
$xml = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE xmeml>
<xmeml version="4">
	<sequence id="sequence-phase00-high-gain">
		<uuid>$sequenceUuid</uuid>
		<duration>350</duration>
		<rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate>
		<name>PHASE00_HIGH_GAIN_COMPATIBILITY</name>
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
    Cases = @($segments.Name)
} | ConvertTo-Json -Depth 4
