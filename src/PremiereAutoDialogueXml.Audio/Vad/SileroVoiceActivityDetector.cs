using Microsoft.ML.OnnxRuntime;

namespace PremiereAutoDialogueXml.Audio.Vad;

public sealed class SileroVoiceActivityDetector : IVoiceActivityDetector
{
    public const int SupportedSampleRate = 16_000;
    public const int SupportedChunkSampleCount = 512;
    private const int ContextSampleCount = 64;
    private const int StateElementCount = 2 * 1 * 128;

    private static readonly string[] InputNames = ["input", "state", "sr"];
    private static readonly string[] OutputNames = ["output", "stateN"];
    private static readonly long[] InputShape = [1, ContextSampleCount + SupportedChunkSampleCount];
    private static readonly long[] StateShape = [2, 1, 128];
    private static readonly long[] SampleRateShape = [1];
    private static readonly long[] ProbabilityShape = [1, 1];

    private readonly float[] _input = new float[ContextSampleCount + SupportedChunkSampleCount];
    private readonly float[] _state = new float[StateElementCount];
    private readonly long[] _sampleRate = [SupportedSampleRate];
    private readonly float[] _probabilityOutput = new float[1];
    private readonly float[] _stateOutput = new float[StateElementCount];
    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions = new();
    private readonly OrtValue[] _inputs;
    private readonly OrtValue[] _outputs;
    private bool _disposed;

    public SileroVoiceActivityDetector()
    {
        using var sessionOptions = new SessionOptions
        {
            InterOpNumThreads = 1,
            IntraOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        _session = new InferenceSession(SileroVadModelInfo.LoadVerifiedModel(), sessionOptions);
        ValidateModelContract(_session);

        _inputs =
        [
            OrtValue.CreateTensorValueFromMemory(_input, InputShape),
            OrtValue.CreateTensorValueFromMemory(_state, StateShape),
            OrtValue.CreateTensorValueFromMemory(_sampleRate, SampleRateShape)
        ];
        _outputs =
        [
            OrtValue.CreateTensorValueFromMemory(_probabilityOutput, ProbabilityShape),
            OrtValue.CreateTensorValueFromMemory(_stateOutput, StateShape)
        ];
    }

    public int SampleRate => SupportedSampleRate;

    public int ChunkSampleCount => SupportedChunkSampleCount;

    public float ProcessChunk(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (samples.Length != SupportedChunkSampleCount)
        {
            throw new ArgumentException(
                $"Silero VAD cần đúng {SupportedChunkSampleCount} mẫu cho mỗi khối; nhận được {samples.Length}.",
                nameof(samples));
        }

        samples.CopyTo(_input.AsSpan(ContextSampleCount));
        _session.Run(_runOptions, InputNames, _inputs, OutputNames, _outputs);

        _stateOutput.CopyTo(_state, 0);
        _input.AsSpan(_input.Length - ContextSampleCount, ContextSampleCount)
            .CopyTo(_input.AsSpan(0, ContextSampleCount));

        var probability = _probabilityOutput[0];
        if (!float.IsFinite(probability) || probability is < 0 or > 1)
        {
            throw new InvalidDataException($"Silero VAD trả về xác suất không hợp lệ: {probability}.");
        }

        return probability;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Array.Clear(_input);
        Array.Clear(_state);
        Array.Clear(_probabilityOutput);
        Array.Clear(_stateOutput);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var input in _inputs)
        {
            input.Dispose();
        }

        foreach (var output in _outputs)
        {
            output.Dispose();
        }

        _runOptions.Dispose();
        _session.Dispose();
        _disposed = true;
    }

    private static void ValidateModelContract(InferenceSession session)
    {
        foreach (var inputName in InputNames)
        {
            if (!session.InputMetadata.ContainsKey(inputName))
            {
                throw new InvalidDataException($"Model Silero thiếu input bắt buộc '{inputName}'.");
            }
        }

        foreach (var outputName in OutputNames)
        {
            if (!session.OutputMetadata.ContainsKey(outputName))
            {
                throw new InvalidDataException($"Model Silero thiếu output bắt buộc '{outputName}'.");
            }
        }
    }
}
