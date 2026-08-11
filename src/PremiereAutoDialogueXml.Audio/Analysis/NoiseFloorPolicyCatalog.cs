namespace PremiereAutoDialogueXml.Audio.Analysis;

internal static class NoiseFloorPolicyCatalog
{
    public static NoiseFloorPolicyDescriptor Phase09Baseline { get; } = new(
        Version: "phase09-adaptive-p20-v1",
        WindowFrameCount: 512,
        Percentile: 0.20,
        InitialFloorDbfs: -90f,
        WarmupFrameCount: 1,
        RiseSmoothing: 0.15f,
        FallSmoothing: 0.15f,
        StableStepFrameCount: 0,
        StableStepMaximumSpreadDb: 0f);

    public static NoiseFloorPolicyDescriptor NoiseBoundaryCandidate { get; } = new(
        Version: "phase10-background-eligible-p20-v1",
        WindowFrameCount: 512,
        Percentile: 0.20,
        InitialFloorDbfs: -90f,
        WarmupFrameCount: 8,
        RiseSmoothing: 0.05f,
        FallSmoothing: 0.20f,
        StableStepFrameCount: 16,
        StableStepMaximumSpreadDb: 3f);

    public static NoiseFloorPolicyDescriptor For(NoiseBoundaryAnalysisMode mode) => mode switch
    {
        NoiseBoundaryAnalysisMode.Phase09Baseline => Phase09Baseline,
        NoiseBoundaryAnalysisMode.NoiseBoundaryCandidate => NoiseBoundaryCandidate,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}
