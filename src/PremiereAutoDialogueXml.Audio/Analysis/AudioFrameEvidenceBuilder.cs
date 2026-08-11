using PremiereAutoDialogueXml.Core.Domain;

namespace PremiereAutoDialogueXml.Audio.Analysis;

public readonly record struct AudioFrameEvidence(
    AudioFrameObservation Observation,
    float AdaptiveNoiseFloorDbfs,
    bool IsVadSpeech,
    bool IsDirectEvidence,
    bool IsAboveDirectEnergyThreshold,
    bool IsWarmupUncertain,
    bool IsStableFloorStep);

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

        return mode == NoiseBoundaryAnalysisMode.Phase09Baseline
            ? BuildPhase09(observations, preset, captureTrace)
            : BuildCandidate(observations, preset, captureTrace);
    }

    private static AudioFrameEvidenceBuildResult BuildPhase09(
        IReadOnlyList<AudioFrameObservation> observations,
        DialogueProcessingPreset preset,
        bool captureTrace)
    {
        var policy = NoiseFloorPolicyCatalog.Phase09Baseline;
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
            var readyBefore = noiseFloor.IsReady;
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
            var readyAfter = noiseFloor.IsReady;
            var aboveDirectThreshold =
                observation.RmsDbfs >= floorAfter + preset.DirectVoiceAboveNoiseDb;
            result.Add(new(
                observation,
                floorAfter,
                isVadSpeech,
                isVadSpeech && aboveDirectThreshold,
                aboveDirectThreshold,
                IsWarmupUncertain: false,
                IsStableFloorStep: false));
            trace?.Add(new(
                floorBefore,
                floorAfter,
                readyBefore,
                readyAfter,
                trainingDecision));
        }

        return new(result, trace ?? [], policy);
    }

    private static AudioFrameEvidenceBuildResult BuildCandidate(
        IReadOnlyList<AudioFrameObservation> observations,
        DialogueProcessingPreset preset,
        bool captureTrace)
    {
        var policy = NoiseFloorPolicyCatalog.NoiseBoundaryCandidate;
        var noiseFloor = new BackgroundEligibleNoiseFloor();
        var result = new List<AudioFrameEvidence>(observations.Count);
        var trace = captureTrace
            ? new List<NoiseFloorFrameSnapshot>(observations.Count)
            : null;

        foreach (var observation in observations)
        {
            var isVadSpeech = observation.ContainsMedia &&
                              observation.VadProbability >= preset.VadThreshold;
            var floorBefore = noiseFloor.CurrentDbfs;
            var readyBefore = noiseFloor.IsReady;
            var hasFiniteLevel = float.IsFinite(observation.RmsDbfs);
            var aboveDirectThreshold =
                hasFiniteLevel &&
                observation.RmsDbfs >= floorBefore + preset.DirectVoiceAboveNoiseDb;
            NoiseFloorTrainingDecision trainingDecision;

            if (!observation.ContainsMedia)
            {
                trainingDecision = NoiseFloorTrainingDecision.NotEligibleNoMedia;
                noiseFloor.BreakHighEnergyContinuity();
            }
            else if (isVadSpeech)
            {
                trainingDecision = NoiseFloorTrainingDecision.NotEligibleVadSpeech;
                noiseFloor.BreakHighEnergyContinuity();
            }
            else if (!hasFiniteLevel)
            {
                trainingDecision = NoiseFloorTrainingDecision.NotEligibleInvalidLevel;
                noiseFloor.BreakHighEnergyContinuity();
            }
            else if (!readyBefore)
            {
                trainingDecision = NoiseFloorTrainingDecision.WarmupCandidate;
                noiseFloor.ObserveWarmup(observation.RmsDbfs);
            }
            else if (aboveDirectThreshold)
            {
                trainingDecision = noiseFloor.ObserveHighEnergyCandidate(observation.RmsDbfs)
                    ? NoiseFloorTrainingDecision.EligibleStableFloorStep
                    : NoiseFloorTrainingDecision.NotEligibleHighEnergyConflict;
            }
            else
            {
                trainingDecision = NoiseFloorTrainingDecision.EligibleBackground;
                noiseFloor.ObserveBackground(observation.RmsDbfs);
            }

            var floorAfter = noiseFloor.CurrentDbfs;
            var readyAfter = noiseFloor.IsReady;
            var isWarmupUncertain =
                trainingDecision == NoiseFloorTrainingDecision.WarmupCandidate &&
                aboveDirectThreshold;
            var isStableFloorStep =
                trainingDecision == NoiseFloorTrainingDecision.EligibleStableFloorStep;
            result.Add(new(
                observation,
                floorBefore,
                isVadSpeech,
                isVadSpeech && aboveDirectThreshold,
                aboveDirectThreshold,
                isWarmupUncertain,
                isStableFloorStep));
            trace?.Add(new(
                floorBefore,
                floorAfter,
                readyBefore,
                readyAfter,
                trainingDecision));
        }

        return new(result, trace ?? [], policy);
    }
}
