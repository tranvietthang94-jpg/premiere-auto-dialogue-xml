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
$tonePath = Join-Path $outputRoot 'phase00-tone-minus24db.wav'
$xmlPath = Join-Path $outputRoot 'phase00-compatibility-input.xml'

& $ffmpeg.Source -hide_banner -loglevel error -y `
    -f lavfi -i 'sine=frequency=1000:sample_rate=48000:duration=10' `
    -af 'volume=-24dB' -ac 1 -c:a pcm_s24le $tonePath
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $tonePath -PathType Leaf)) {
    throw 'Không thể tạo WAV kiểm thử Phase 00 bằng FFmpeg.'
}

$culture = [Globalization.CultureInfo]::InvariantCulture
$ticksPerFrame = [Int64]10160640000
$segments = @(
    [pscustomobject]@{ Name = 'UNITY'; Start = 0;   End = 50;  Enabled = $true;  Gains = @() },
    [pscustomobject]@{ Name = 'PLUS_6_DB'; Start = 50;  End = 100; Enabled = $true;  Gains = @(6.0) },
    [pscustomobject]@{ Name = 'PLUS_12_DB'; Start = 100; End = 150; Enabled = $true;  Gains = @(12.0) },
    [pscustomobject]@{ Name = 'PLUS_18_DB_STACKED'; Start = 150; End = 200; Enabled = $true;  Gains = @(9.0, 9.0) },
    [pscustomobject]@{ Name = 'DISABLED'; Start = 200; End = 250; Enabled = $false; Gains = @() }
)

function Escape-Xml([string]$Value) {
    return [Security.SecurityElement]::Escape($Value)
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
								<parameter authoringApp="PremierePro">
									<parameterid>level</parameterid>
									<name>Level</name>
									<valuemin>0</valuemin>
									<valuemax>3.98109</valuemax>
									<value>$linear</value>
								</parameter>
								<pproBypass>false</pproBypass>
							</effect>
						</filter>
"@
}

$toneUrl = ([Uri]$tonePath).AbsoluteUri
$clipXml = [Text.StringBuilder]::new()
for ($index = 0; $index -lt $segments.Count; $index++) {
    $segment = $segments[$index]
    $clipNumber = $index + 1
    $enabled = if ($segment.Enabled) { 'TRUE' } else { 'FALSE' }
    $ticksIn = [Int64]$segment.Start * $ticksPerFrame
    $ticksOut = [Int64]$segment.End * $ticksPerFrame
    $fileElement = if ($index -eq 0) {
@"
						<file id="file-1">
							<name>phase00-tone-minus24db.wav</name>
							<pathurl>$(Escape-Xml $toneUrl)</pathurl>
							<rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate>
							<duration>250</duration>
							<timecode><rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate><string>00:00:00:00</string><frame>0</frame><displayformat>NDF</displayformat></timecode>
							<media><audio><samplecharacteristics><depth>24</depth><samplerate>48000</samplerate></samplecharacteristics><channelcount>1</channelcount></audio></media>
						</file>
"@
    } else {
        "`t`t`t`t`t`t<file id=`"file-1`"/>`n"
    }

    $filters = ($segment.Gains | ForEach-Object { New-AudioLevelFilter $_ }) -join ''
    [void]$clipXml.Append(@"
					<clipitem id="clipitem-$clipNumber" premiereChannelType="mono">
						<masterclipid>masterclip-1</masterclipid>
						<name>PHASE00_$($segment.Name)</name>
						<enabled>$enabled</enabled>
						<duration>250</duration>
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
	<sequence id="sequence-phase00">
		<uuid>$sequenceUuid</uuid>
		<duration>250</duration>
		<rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate>
		<name>PHASE00_AUDIO_LEVELS_COMPATIBILITY</name>
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
				<track TL.SQTrackAudioKeyframeStyle="0" PannerCurrentValue="0.5" PannerIsInverted="true" PannerName="Balance" premiereTrackType="Stereo">
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
    TonePeakDbfs = -24
    SegmentSeconds = 2
} | ConvertTo-Json

