[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(24, 25, 30)]
    [int]$FrameRate,

    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null

$prefix = "phase12-${FrameRate}fps-ndf"
$wavePath = Join-Path $outputRoot "$prefix-voice.wav"
$xmlPath = Join-Path $outputRoot "$prefix-input.xml"
$manifestPath = Join-Path $outputRoot "$prefix-fixture.json"
$targets = @($wavePath, $xmlPath, $manifestPath)
foreach ($target in $targets) {
    if (Test-Path -LiteralPath $target) {
        throw "Fixture target already exists; refusing to overwrite: $target"
    }
}

$ffmpeg = Get-Command ffmpeg -ErrorAction Stop
$filter = @(
    '[0:a]aresample=48000,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=mono,atrim=duration=1[s0]'
    '[1:a]aresample=48000,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=mono,volume=-10dB,apad=whole_dur=3,atrim=duration=3[s1]'
    '[2:a]aresample=48000,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=mono,volume=-10dB,apad=whole_dur=4,atrim=duration=4[s2]'
    '[3:a]aresample=48000,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=mono,atrim=duration=1[s3]'
    '[4:a]aresample=48000,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=mono,volume=-28dB,apad=whole_dur=3,atrim=duration=3[s4]'
    '[5:a]aresample=48000,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=mono,atrim=duration=1[s5]'
    '[s0][s1][s2][s3][s4][s5]concat=n=6:v=0:a=1[out]'
) -join ';'

& $ffmpeg.Source -nostdin -hide_banner -loglevel error -n `
    -f lavfi -i 'anullsrc=r=48000:cl=mono:d=1' `
    -f lavfi -i "flite=text='Clear speech should remain enabled while background noise should be disabled':voice=slt" `
    -f lavfi -i "flite=text='This deliberately long sentence crosses the edit boundary so timing stays exact in Premiere Pro':voice=slt" `
    -f lavfi -i 'anullsrc=r=48000:cl=mono:d=1' `
    -f lavfi -i "flite=text='Very quiet dialogue checks the eighteen decibel safety cap':voice=slt" `
    -f lavfi -i 'anullsrc=r=48000:cl=mono:d=1' `
    -filter_complex $filter -map '[out]' -ac 1 -ar 48000 -c:a pcm_s24le $wavePath
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $wavePath -PathType Leaf)) {
    throw 'FFmpeg could not create the Phase 12 speech fixture.'
}

$ticksPerSecond = [Int64]254016000000
if ($ticksPerSecond % $FrameRate -ne 0 -or 48000 % $FrameRate -ne 0) {
    throw 'Frame rate does not define an exact Phase 12 NDF frame grid.'
}

$ticksPerFrame = [Int64]($ticksPerSecond / $FrameRate)
$samplesPerFrame = [int](48000 / $FrameRate)
$sequenceFrames = [Int64](13 * $FrameRate)
$fileUrl = ([Uri]$wavePath).AbsoluteUri -replace '^file:///([A-Za-z]):', 'file://localhost/$1%3a'
$escapedFileUrl = [Security.SecurityElement]::Escape($fileUrl)
$sequenceUuid = [Guid]::NewGuid().ToString()
$clips = @(
    [pscustomobject]@{ Id = 1; Name = 'MODERATE_SPEECH_AND_BOUNDARY_A'; Start = 0 * $FrameRate; End = 5 * $FrameRate; In = 1 * $FrameRate; Out = 6 * $FrameRate },
    [pscustomobject]@{ Id = 2; Name = 'BOUNDARY_B_AND_SILENCE'; Start = 5 * $FrameRate; End = 8 * $FrameRate; In = 6 * $FrameRate; Out = 9 * $FrameRate },
    [pscustomobject]@{ Id = 3; Name = 'QUIET_CAPPED_SPEECH'; Start = 9 * $FrameRate; End = 13 * $FrameRate; In = 9 * $FrameRate; Out = 13 * $FrameRate }
)

$clipXml = [Text.StringBuilder]::new()
foreach ($clip in $clips) {
    $ticksIn = [Int64]$clip.In * $ticksPerFrame
    $ticksOut = [Int64]$clip.Out * $ticksPerFrame
    $fileElement = if ($clip.Id -eq 1) {
@"
						<file id="file-phase12-$FrameRate">
							<name>$(Split-Path -Leaf $wavePath)</name>
							<pathurl>$escapedFileUrl</pathurl>
							<rate><timebase>$FrameRate</timebase><ntsc>FALSE</ntsc></rate>
							<duration>$sequenceFrames</duration>
							<timecode><rate><timebase>$FrameRate</timebase><ntsc>FALSE</ntsc></rate><string>00:00:00:00</string><frame>0</frame><displayformat>NDF</displayformat></timecode>
							<media><audio><samplecharacteristics><depth>24</depth><samplerate>48000</samplerate></samplecharacteristics><channelcount>1</channelcount></audio></media>
						</file>
"@
    } else {
        "`t`t`t`t`t`t<file id=`"file-phase12-$FrameRate`"/>`n"
    }

    [void]$clipXml.Append(@"
					<clipitem id="clipitem-phase12-$FrameRate-$($clip.Id)" premiereChannelType="mono">
						<masterclipid>masterclip-phase12-$FrameRate</masterclipid>
						<name>PHASE12_$($clip.Name)</name><enabled>TRUE</enabled><duration>$sequenceFrames</duration>
						<rate><timebase>$FrameRate</timebase><ntsc>FALSE</ntsc></rate>
						<start>$($clip.Start)</start><end>$($clip.End)</end><in>$($clip.In)</in><out>$($clip.Out)</out>
						<pproTicksIn>$ticksIn</pproTicksIn><pproTicksOut>$ticksOut</pproTicksOut>
$fileElement						<sourcetrack><mediatype>audio</mediatype><trackindex>1</trackindex></sourcetrack>
					</clipitem>
"@)
}

$xml = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE xmeml>
<xmeml version="4">
	<sequence id="sequence-phase12-$FrameRate">
		<uuid>$sequenceUuid</uuid><duration>$sequenceFrames</duration>
		<rate><timebase>$FrameRate</timebase><ntsc>FALSE</ntsc></rate>
		<name>PHASE12_${FrameRate}FPS_NDF_ADOPTION</name>
		<timecode><rate><timebase>$FrameRate</timebase><ntsc>FALSE</ntsc></rate><string>00:00:00:00</string><frame>0</frame><displayformat>NDF</displayformat></timecode>
		<media>
			<video>
				<format><samplecharacteristics><rate><timebase>$FrameRate</timebase><ntsc>FALSE</ntsc></rate><width>1920</width><height>1080</height><anamorphic>FALSE</anamorphic><pixelaspectratio>square</pixelaspectratio><fielddominance>none</fielddominance></samplecharacteristics></format>
				<track><enabled>TRUE</enabled><locked>FALSE</locked></track>
			</video>
			<audio>
				<numOutputChannels>2</numOutputChannels>
				<format><samplecharacteristics><depth>24</depth><samplerate>48000</samplerate></samplecharacteristics></format>
				<outputs><group><index>1</index><numchannels>1</numchannels><downmix>0</downmix><channel><index>1</index></channel></group><group><index>2</index><numchannels>1</numchannels><downmix>0</downmix><channel><index>2</index></channel></group></outputs>
				<track TL.SQTrackAudioKeyframeStyle="0" TL.SQTrackShy="0" TL.SQTrackExpandedHeight="41" TL.SQTrackExpanded="0" MZ.TrackTargeted="1" PannerCurrentValue="0.5" PannerIsInverted="true" PannerStartKeyframe="-91445760000000000,0.5,0,0,0,0,0,0" PannerName="Balance" currentExplodedTrackIndex="0" totalExplodedTrackCount="1" premiereTrackType="Stereo">
$clipXml					<enabled>TRUE</enabled><locked>FALSE</locked><outputchannelindex>1</outputchannelindex>
				</track>
			</audio>
		</media>
	</sequence>
</xmeml>
"@

[IO.File]::WriteAllText($xmlPath, $xml, [Text.UTF8Encoding]::new($false))
$manifest = [ordered]@{
    SchemaVersion = '1.0'
    Purpose = 'phase12-integer-ndf-premiere-adoption'
    FrameRate = $FrameRate
    Ntsc = $false
    AudioSampleRate = 48000
    SamplesPerFrame = $samplesPerFrame
    SequenceFrames = $sequenceFrames
    SequenceSeconds = 13
    SourceXmlFileName = Split-Path -Leaf $xmlPath
    SourceXmlSha256 = (Get-FileHash -LiteralPath $xmlPath -Algorithm SHA256).Hash
    SourceWaveFileName = Split-Path -Leaf $wavePath
    SourceWaveSha256 = (Get-FileHash -LiteralPath $wavePath -Algorithm SHA256).Hash
    ClipCount = $clips.Count
    BoundaryFrame = 5 * $FrameRate
    GapStartFrame = 8 * $FrameRate
    GapEndFrame = 9 * $FrameRate
    ExpectedCoverage = 'source-trim; adjacent-boundary; one-second-gap; moderate-and-quiet-speech; trailing-silence'
}
[IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json -Depth 5),
    [Text.UTF8Encoding]::new($false))

$manifest | ConvertTo-Json -Depth 5
