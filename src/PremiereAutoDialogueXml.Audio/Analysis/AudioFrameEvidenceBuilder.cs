using PremiereAutoDialogueXml.Core.Domain;

namespace PremiereAutoDialogueXml.Audio.Analysis;

public readonly record struct AudioFrameEvidence(
    AudioFrameObservation Observation,
    float AdaptiveNoiseFloorDbfs,
    bool IsVadSpeech,
    bool IsDirectEvidence,
    bool IsAboveDirectEnergyThreshold);

public sealed class AudioFrameEvidenceBuilder
{
    public IReadOnlyList<AudioFrameEvidence> Build(
        IReadOnlyList<AudioFrameObservation> observations,
        DialogueProcessingPreset preset) =>
        BuildCore(
            observations,
            preset,
            NoiseBoundaryAnalysisMode.Phase09Baseline,
            captureTrace: false).Evidence;

    internal AudioFrameEvidenceBuildResult BuildWithTrace(
        IReadOnlyList<AudioFrameObservation> observations,
        DialogueProcessingPreset preset,
        NoiseBoundaryAnalysisMode mode) =>
        BuildCore(observations, preset, mode, captureTrace: true);

    private static AudioFrameEvidenceBuildResult BuildCore(
        IReadOnlyList<AudioFrameObservation> observations,
        DialogueProcessingPreset preset,
        NoiseBoundaryAnalysisMode mode,
        bool captureTrace)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(preset);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        var noiseFloor = new AdaptiveNoiseFloor();
        var result = new List<AudioFrameEvidence>(observations.Count);
        var trace = captureTrace
            ? new List<NoiseFloorFrameSnapshot>(observations.Count)
            : null;
        foreach (var observation in observations)
        {
            var isVadSpeech = observation.ContainsMedia &&
                              observation.VadProbability >= preset.VadThreshold;
            var floorBefore = noiseFloor.CurrentDbfs;
            var trainingDecision = !observation.ContainsMedia
                ? NoiseFloorTrainingDecision.NotEligibleNoMedia
                : isVadSpeech
                    ? NoiseFloorTrainingDecision.NotEligibleVadSpeech
                    : NoiseFloorTrainingDecision.EligibleVadNegative;
            if (trainingDecision == NoiseFloorTrainingDecision.EligibleVadNegative)
            {
                noiseFloor.Observe(observation.RmsDbfs);
            }

            var floorAfter = noiseFloor.CurrentDbfs;
            var aboveDirectThreshold =
                observation.RmsDbfs >= floorAfter + preset.DirectVoiceAboveNoiseDb;
            result.Add(new(
                observation,
                floorAfter,
                isVadSpeech,
                isVadSpeech && aboveDirectThreshold,
                aboveDirectThreshold));
            trace?.Add(new(floorBefore, floorAfter, trainingDecision));
        }

        return new(result, trace ?? []);
    }
}
