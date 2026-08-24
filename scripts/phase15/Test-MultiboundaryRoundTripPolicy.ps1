[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) "padx-phase15-policy-$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $candidatePath = Join-Path $root 'candidate.xml'
    $exportedPath = Join-Path $root 'exported.xml'
    $batchPath = Join-Path $root 'batch.json'
    $reportPath = Join-Path $root 'result.json'
    $mutatedPath = Join-Path $root 'exported-mutated.xml'
    $mutatedReportPath = Join-Path $root 'mutated-result.json'
    $frameTicks = 10160640000L
    $halfFrameTicks = 5080320000L

    function New-TransitionXml([long]$Frame) {
        $ticksIn = $Frame * $frameTicks - $halfFrameTicks
        $ticksOut = $Frame * $frameTicks + $halfFrameTicks
        return "<transitionitem><start>$Frame</start><end>$Frame</end><pproTicksIn>$ticksIn</pproTicksIn><pproTicksOut>$ticksOut</pproTicksOut><alignment>center</alignment><cutPointTicks>$halfFrameTicks</cutPointTicks><rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate><effect><name>Cross Fade ( 0dB)</name><effectid>KGAudioTransCrossFade0dB</effectid><effecttype>transition</effecttype><mediatype>audio</mediatype><wipecode>0</wipecode><wipeaccuracy>100</wipeaccuracy><startratio>0</startratio><endratio>1</endratio><reverse>FALSE</reverse></effect></transitionitem>"
    }

    function New-ClipXml(
        [string]$Id,
        [bool]$Enabled,
        [long]$Start,
        [long]$End,
        [long]$TicksIn,
        [long]$TicksOut) {
        $enabledText = if ($Enabled) { 'TRUE' } else { 'FALSE' }
        $clipNumber = [int]$Id.Split('-')[-1]
        $sourceIn = $clipNumber * 2
        $sourceOut = ($clipNumber + 1) * 2
        return '<clipitem id="{0}"><masterclipid>masterclip-1</masterclipid><enabled>{1}</enabled><duration>8</duration><rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate><start>{2}</start><end>{3}</end><in>{4}</in><out>{5}</out><pproTicksIn>{6}</pproTicksIn><pproTicksOut>{7}</pproTicksOut><file id="file-1"><name>media.wav</name><pathurl>file://localhost/C:/fixture/media.wav</pathurl></file><sourcetrack><mediatype>audio</mediatype><trackindex>1</trackindex></sourcetrack></clipitem>' -f $Id, $enabledText, $Start, $End, $sourceIn, $sourceOut, $TicksIn, $TicksOut
    }

    $candidateClips = @(
        New-ClipXml 'clip-0' $false 0 2 0 20321280000
        New-ClipXml 'clip-1' $true 2 4 20321280000 40642560000
        New-ClipXml 'clip-2' $true 4 6 40642560000 60963840000
        New-ClipXml 'clip-3' $false 6 8 60963840000 81285120000
    )
    $exportedClips = @(
        New-ClipXml 'clip-0' $false 0 -1 0 25401600000
        New-ClipXml 'clip-1' $true -1 -1 15240960000 45722880000
        New-ClipXml 'clip-2' $true -1 -1 35562240000 66044160000
        New-ClipXml 'clip-3' $false -1 8 55883520000 81285120000
    )
    $transitions = @(New-TransitionXml 2; New-TransitionXml 4; New-TransitionXml 6) -join ''
    $prefix = '<?xml version="1.0" encoding="utf-8"?>' + "`r`n<!DOCTYPE xmeml>`r`n" +
        '<xmeml version="4"><sequence id="sequence-test"><duration>8</duration><rate><timebase>25</timebase><ntsc>FALSE</ntsc></rate><name>Phase 15 policy</name><media><audio><numOutputChannels>2</numOutputChannels><format><samplecharacteristics><depth>16</depth><samplerate>48000</samplerate></samplecharacteristics></format><track>'
    $suffix = '</track></audio></media></sequence></xmeml>'
    [IO.File]::WriteAllText(
        $candidatePath,
        $prefix + ($candidateClips -join '') + $transitions + $suffix,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $exportedPath,
        $prefix + ($exportedClips -join '') + $transitions + $suffix,
        [Text.UTF8Encoding]::new($false))

    $candidateHash = (Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash
    $boundaries = @(
        [ordered]@{ trackIndex = 1; boundaryFrame = 2; leftClipItemId = 'clip-0'; rightClipItemId = 'clip-1' }
        [ordered]@{ trackIndex = 1; boundaryFrame = 4; leftClipItemId = 'clip-1'; rightClipItemId = 'clip-2' }
        [ordered]@{ trackIndex = 1; boundaryFrame = 6; leftClipItemId = 'clip-2'; rightClipItemId = 'clip-3' }
    )
    $batch = [ordered]@{
        outputXmlFileName = 'candidate.xml'
        plan = [ordered]@{ selectedCount = 3; selectionStreamSha256 = ('A' * 64) }
        candidate = [ordered]@{
            outputXmlSha256 = $candidateHash
            frameTicks = $frameTicks
            halfFrameTicks = $halfFrameTicks
            transitionCount = 3
            boundaries = $boundaries
        }
    }
    [IO.File]::WriteAllText(
        $batchPath,
        ($batch | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false))

    & (Join-Path $PSScriptRoot 'Test-MultiboundaryPremiereRoundTrip.ps1') `
        -CandidateXml $candidatePath `
        -ExportedXml $exportedPath `
        -BatchReport $batchPath `
        -ReportPath $reportPath | Out-Null
    $result = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
    if ($result.status -ne 'phase15-multiboundary-premiere-roundtrip-compatible' -or
        [int]$result.transitionCount -ne 3 -or
        [int]$result.allowedClipNormalizationCount -ne 12) {
        throw 'Synthetic compatible round-trip không đạt policy Phase 15.'
    }

    $mutated = (Get-Content -Raw -LiteralPath $exportedPath).Replace(
        '<clipitem id="clip-2"><masterclipid>masterclip-1</masterclipid><enabled>TRUE</enabled>',
        '<clipitem id="clip-2"><masterclipid>masterclip-1</masterclipid><enabled>FALSE</enabled>')
    [IO.File]::WriteAllText($mutatedPath, $mutated, [Text.UTF8Encoding]::new($false))
    $rejected = $false
    try {
        & (Join-Path $PSScriptRoot 'Test-MultiboundaryPremiereRoundTrip.ps1') `
            -CandidateXml $candidatePath `
            -ExportedXml $mutatedPath `
            -BatchReport $batchPath `
            -ReportPath $mutatedReportPath | Out-Null
    } catch {
        $rejected = $true
    }
    if (-not $rejected -or (Test-Path -LiteralPath $mutatedReportPath)) {
        throw 'Semantic mutation không bị validator Phase 15 từ chối.'
    }

    [pscustomobject]@{
        Status = 'phase15-multiboundary-roundtrip-policy-tests-passed'
        CompatibleCaseCount = 1
        RejectedSemanticMutationCount = 1
        TransitionCount = 3
        AllowedNormalizationCount = 12
    } | ConvertTo-Json
} finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
