namespace PremiereAutoDialogueXml.Audio.Analysis;

internal sealed class BackgroundEligibleNoiseFloor
{
    private readonly NoiseFloorPolicyDescriptor _policy =
        NoiseFloorPolicyCatalog.NoiseBoundaryCandidate;
    private readonly float[] _window;
    private readonly float[] _scratch;
    private readonly List<float> _warmup;
    private readonly Queue<float> _stableStep = new();
    private int _count;
    private int _next;
    private bool _stableStepConfirmed;
    private float _stableStepTargetDbfs;

    public BackgroundEligibleNoiseFloor()
    {
        _window = new float[_policy.WindowFrameCount];
        _scratch = new float[_policy.WindowFrameCount];
        _warmup = new(_policy.WarmupFrameCount);
        CurrentDbfs = _policy.InitialFloorDbfs;
    }

    public float CurrentDbfs { get; private set; }

    public bool IsReady { get; private set; }

    public void ObserveWarmup(float rmsDbfs)
    {
        if (IsReady || !float.IsFinite(rmsDbfs))
        {
            return;
        }

        _warmup.Add(ClampLevel(rmsDbfs));
        if (_warmup.Count < _policy.WarmupFrameCount)
        {
            return;
        }

        ResetWindow(_warmup);
        CurrentDbfs = CalculateObservedFloor();
        IsReady = true;
    }

    public void ObserveBackground(float rmsDbfs)
    {
        ResetStableStep();
        if (!IsReady || !float.IsFinite(rmsDbfs))
        {
            return;
        }

        AddToWindow(ClampLevel(rmsDbfs));
        MoveTowardObservedFloor();
    }

    public bool ObserveHighEnergyCandidate(float rmsDbfs)
    {
        if (!IsReady || !float.IsFinite(rmsDbfs))
        {
            ResetStableStep();
            return false;
        }

        var level = ClampLevel(rmsDbfs);
        if (_stableStepConfirmed)
        {
            if (MathF.Abs(level - _stableStepTargetDbfs) <=
                _policy.StableStepMaximumSpreadDb)
            {
                AddToWindow(level);
                MoveToward(_stableStepTargetDbfs);
                return true;
            }

            ResetStableStep();
        }

        _stableStep.Enqueue(level);
        while (_stableStep.Count > _policy.StableStepFrameCount)
        {
            _stableStep.Dequeue();
        }

        if (_stableStep.Count < _policy.StableStepFrameCount ||
            _stableStep.Max() - _stableStep.Min() > _policy.StableStepMaximumSpreadDb)
        {
            return false;
        }

        _stableStepConfirmed = true;
        _stableStepTargetDbfs = Percentile(_stableStep);
        ResetWindow([level]);
        MoveToward(_stableStepTargetDbfs);
        return true;
    }

    public void BreakHighEnergyContinuity() => ResetStableStep();

    private void ResetWindow(IEnumerable<float> levels)
    {
        _count = 0;
        _next = 0;
        foreach (var level in levels)
        {
            AddToWindow(level);
        }
    }

    private void AddToWindow(float level)
    {
        _window[_next] = level;
        _next = (_next + 1) % _policy.WindowFrameCount;
        _count = Math.Min(_count + 1, _policy.WindowFrameCount);
    }

    private float CalculateObservedFloor()
    {
        if (_count == 0)
        {
            return _policy.InitialFloorDbfs;
        }

        Array.Copy(_window, _scratch, _count);
        Array.Sort(_scratch, 0, _count);
        var percentileIndex = Math.Clamp(
            (int)Math.Floor((_count - 1) * _policy.Percentile),
            0,
            _count - 1);
        return ClampLevel(_scratch[percentileIndex]);
    }

    private void MoveTowardObservedFloor()
    {
        MoveToward(CalculateObservedFloor());
    }

    private void MoveToward(float observedFloor)
    {
        var smoothing = observedFloor > CurrentDbfs
            ? _policy.RiseSmoothing
            : _policy.FallSmoothing;
        CurrentDbfs = ClampLevel(CurrentDbfs + smoothing * (observedFloor - CurrentDbfs));
    }

    private float Percentile(IEnumerable<float> levels)
    {
        var ordered = levels.Order().ToArray();
        var index = Math.Clamp(
            (int)Math.Floor((ordered.Length - 1) * _policy.Percentile),
            0,
            ordered.Length - 1);
        return ClampLevel(ordered[index]);
    }

    private void ResetStableStep()
    {
        _stableStep.Clear();
        _stableStepConfirmed = false;
        _stableStepTargetDbfs = AudioMath.SilenceDbfs;
    }

    private static float ClampLevel(float level) =>
        Math.Clamp(level, AudioMath.SilenceDbfs, 0f);
}
