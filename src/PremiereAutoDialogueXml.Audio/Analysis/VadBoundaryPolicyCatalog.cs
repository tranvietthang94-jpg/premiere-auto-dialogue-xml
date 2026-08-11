using PremiereAutoDialogueXml.Core.Domain;

namespace PremiereAutoDialogueXml.Audio.Analysis;

internal static class VadBoundaryPolicyCatalog
{
    private const double CandidateContinueThreshold = 0.40;

    public static VadBoundaryPolicyDescriptor For(
        NoiseBoundaryAnalysisMode mode,
        DialogueProcessingPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (mode == NoiseBoundaryAnalysisMode.NoiseBoundaryCandidate &&
            Math.Abs(preset.VadThreshold - 0.50) > 0.0001)
        {
            throw new InvalidOperationException(
                "Candidate Phase 10C chỉ hỗ trợ start threshold 0,50 đã khóa.");
        }

        return mode switch
        {
            NoiseBoundaryAnalysisMode.Phase09Baseline => new(
                Version: "phase09-vad-single-threshold-v1",
                StartThreshold: preset.VadThreshold,
                ContinueThreshold: preset.VadThreshold,
                PhraseBreakMilliseconds: preset.PhraseBreakMilliseconds,
                MinimumStartEvidenceMilliseconds: preset.MinimumSpeechMilliseconds,
                MinimumDirectEvidenceMilliseconds: preset.MinimumSpeechMilliseconds),
            NoiseBoundaryAnalysisMode.NoiseBoundaryCandidate => new(
                Version: "phase10-vad-start050-continue040-v1",
                StartThreshold: preset.VadThreshold,
                ContinueThreshold: CandidateContinueThreshold,
                PhraseBreakMilliseconds: preset.PhraseBreakMilliseconds,
                MinimumStartEvidenceMilliseconds: preset.MinimumSpeechMilliseconds,
                MinimumDirectEvidenceMilliseconds: preset.MinimumSpeechMilliseconds),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }
}
