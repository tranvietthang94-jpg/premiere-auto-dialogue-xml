[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$validatorPath = Join-Path $repoRoot 'scripts\phase09\Test-Phase09RoundTrip.ps1'
if (-not (Test-Path -LiteralPath $validatorPath -PathType Leaf)) {
    throw "Round-trip validator was not found: $validatorPath"
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) "padx-phase13-policy-$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($testRoot) | Out-Null

function New-PolicyFixtureXml {
    param(
        [int]$FrameRate,
        [int]$SequenceDepth = 24,
        [string]$ClipEnabled = 'TRUE',
        [string]$GainFactor = '1',
        [string]$MarkerName = 'Cần kiểm tra'
    )

    $duration = 10 * $FrameRate
    return @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE xmeml>
<xmeml version="4">
  <sequence id="sequence-policy-$FrameRate">
    <name>PHASE13_${FrameRate}FPS_POLICY</name>
    <duration>$duration</duration>
    <rate><timebase>$FrameRate</timebase><ntsc>FALSE</ntsc></rate>
    <media>
      <audio>
        <format><samplecharacteristics><depth>$SequenceDepth</depth><samplerate>48000</samplerate><channelcount>2</channelcount></samplecharacteristics></format>
        <track currentExplodedTrackIndex="0" totalExplodedTrackCount="1" premiereTrackType="Stereo">
          <clipitem id="clipitem-policy-$FrameRate-1">
            <name>POLICY_SPEECH</name><enabled>$ClipEnabled</enabled>
            <start>0</start><end>$duration</end><in>0</in><out>$duration</out>
            <pproTicksIn>0</pproTicksIn><pproTicksOut>2540160000000</pproTicksOut>
            <rate><timebase>$FrameRate</timebase><ntsc>FALSE</ntsc></rate>
            <file id="file-policy-$FrameRate"><name>phase13-policy.wav</name><pathurl>file://localhost/C%3a/phase13-policy.wav</pathurl></file>
            <sourcetrack><mediatype>audio</mediatype><trackindex>1</trackindex></sourcetrack>
            <filter><effect><effectid>audiolevels</effectid><parameter><parameterid>level</parameterid><value>$GainFactor</value></parameter></effect></filter>
          </clipitem>
          <enabled>TRUE</enabled><locked>FALSE</locked><outputchannelindex>1</outputchannelindex>
        </track>
      </audio>
    </media>
    <marker><name>$MarkerName</name><comment>phase13-policy</comment><in>0</in><out>1</out></marker>
  </sequence>
</xmeml>
"@
}

function Invoke-RoundTripValidator {
    param(
        [string]$InputXml,
        [string]$ExportedXml,
        [switch]$AllowDepthNormalization
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = (Get-Process -Id $PID).Path
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $validatorPath, '-InputXml', $InputXml, '-ExportedXml', $ExportedXml)) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    if ($AllowDepthNormalization) {
        [void]$startInfo.ArgumentList.Add('-AllowPremiereSequenceDepthNormalization')
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        StandardOutput = $stdout
        StandardError = $stderr
    }
}

function Assert-ExitCode {
    param($Result, [int]$Expected, [string]$Case)

    if ($Result.ExitCode -ne $Expected) {
        throw "$Case returned exit $($Result.ExitCode), expected $Expected. stderr: $($Result.StandardError) stdout: $($Result.StandardOutput)"
    }
}

try {
    $caseCount = 0
    foreach ($frameRate in @(24, 30)) {
        $inputPath = Join-Path $testRoot "${frameRate}-input.xml"
        $normalizedPath = Join-Path $testRoot "${frameRate}-depth-normalized.xml"
        $wrongDepthPath = Join-Path $testRoot "${frameRate}-wrong-depth.xml"
        $enabledChangedPath = Join-Path $testRoot "${frameRate}-enabled-changed.xml"

        [IO.File]::WriteAllText($inputPath, (New-PolicyFixtureXml -FrameRate $frameRate), [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($normalizedPath, (New-PolicyFixtureXml -FrameRate $frameRate -SequenceDepth 16), [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($wrongDepthPath, (New-PolicyFixtureXml -FrameRate $frameRate -SequenceDepth 32), [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($enabledChangedPath, (New-PolicyFixtureXml -FrameRate $frameRate -SequenceDepth 16 -ClipEnabled 'FALSE'), [Text.UTF8Encoding]::new($false))

        $strict = Invoke-RoundTripValidator -InputXml $inputPath -ExportedXml $normalizedPath
        Assert-ExitCode $strict 1 "${frameRate}fps strict depth normalization"
        $caseCount++

        $allowed = Invoke-RoundTripValidator -InputXml $inputPath -ExportedXml $normalizedPath -AllowDepthNormalization
        Assert-ExitCode $allowed 0 "${frameRate}fps explicit depth normalization"
        $allowedReport = $allowed.StandardOutput | ConvertFrom-Json
        if ($allowedReport.status -ne 'phase09-roundtrip-compatible' -or
            @($allowedReport.allowedNormalizations).Count -ne 1 -or
            $allowedReport.allowedNormalizations[0] -ne 'sequence-depth-24-to-16') {
            throw "${frameRate}fps explicit normalization did not report the exact allowed policy."
        }
        $caseCount++

        $wrongDepth = Invoke-RoundTripValidator -InputXml $inputPath -ExportedXml $wrongDepthPath -AllowDepthNormalization
        Assert-ExitCode $wrongDepth 1 "${frameRate}fps disallowed depth transition"
        $caseCount++

        $semanticChange = Invoke-RoundTripValidator -InputXml $inputPath -ExportedXml $enabledChangedPath -AllowDepthNormalization
        Assert-ExitCode $semanticChange 1 "${frameRate}fps semantic change with normalization switch"
        $caseCount++
    }

    [pscustomobject]@{
        Status = 'phase13-premiere-roundtrip-policy-tests-passed'
        FrameRates = @(24, 30)
        CaseCount = $caseCount
        AllowedNormalization = 'sequence-depth-24-to-16'
    } | ConvertTo-Json -Depth 4
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
