namespace PremiereAutoDialogueXml.Audio.Analysis;

internal sealed class AdaptiveNoiseFloor
{
    private const int WindowSize = 512;
    private const float InitialFloorDbfs = -90f;
    private readonly float[] _window = new float[WindowSize];
    private readonly float[] _scratch = new float[WindowSize];
    private int _count;
    private int _next;

    public float CurrentDbfs { get; private set; } = InitialFloorDbfs;

    public bool IsReady => _count > 0;

    public void Observe(float rmsDbfs)
    {
        if (!float.IsFinite(rmsDbfs))
        {
            return;
        }

        _window[_next] = Math.Clamp(rmsDbfs, AudioMath.SilenceDbfs, 0);
        _next = (_next + 1) % WindowSize;
        _count = Math.Min(_count + 1, WindowSize);

        Array.Copy(_window, _scratch, _count);
        Array.Sort(_scratch, 0, _count);
        var percentileIndex = Math.Clamp((int)Math.Floor((_count - 1) * 0.20), 0, _count - 1);
        var observedFloor = _scratch[percentileIndex];

        if (_count == 1)
        {
            CurrentDbfs = observedFloor;
            return;
        }

        const float smoothing = 0.15f;
        CurrentDbfs += smoothing * (observedFloor - CurrentDbfs);
    }
}
