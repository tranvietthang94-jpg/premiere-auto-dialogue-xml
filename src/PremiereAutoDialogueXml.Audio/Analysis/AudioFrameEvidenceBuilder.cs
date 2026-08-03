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
        DialogueProcessingPreset preset)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(preset);

        var noiseFloor = new AdaptiveNoiseFloor();
        var result = new List<AudioFrameEvidence>(observations.Count);
        foreach (var observation in observations)
        {
            var isVadSpeech = observation.ContainsMedia &&
                              observation.VadProbability >= preset.VadThreshold;
            if (observation.ContainsMedia && !isVadSpeech)
            {
                noiseFloor.Observe(observation.RmsDbfs);
            }

            var aboveDirectThreshold =
                observation.RmsDbfs >= noiseFloor.CurrentDbfs + preset.DirectVoiceAboveNoiseDb;
            result.Add(new(
                observation,
                noiseFloor.CurrentDbfs,
                isVadSpeech,
                isVadSpeech && aboveDirectThreshold,
                aboveDirectThreshold));
        }

        return result;
    }
}
