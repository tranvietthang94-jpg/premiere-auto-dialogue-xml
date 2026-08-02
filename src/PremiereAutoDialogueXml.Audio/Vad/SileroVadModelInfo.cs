using System.Security.Cryptography;

namespace PremiereAutoDialogueXml.Audio.Vad;

public static class SileroVadModelInfo
{
    public const string Version = "6.2.1";
    public const string SourceCommit = "7e30209a3e901f9842f81b225f3e93d8199902b1";
    public const string Sha256 = "1A153A22F4509E292A94E67D6F9B85E8DEB25B4988682B7E174C65279D8788E3";
    public const string ResourceName = "PremiereAutoDialogueXml.Audio.Models.silero_vad.onnx";

    public static byte[] LoadVerifiedModel()
    {
        var assembly = typeof(SileroVadModelInfo).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Không tìm thấy model nhúng: {ResourceName}.");

        if (stream.Length > int.MaxValue)
        {
            throw new InvalidOperationException("Model nhúng vượt quá giới hạn bộ nhớ cho phép.");
        }

        var model = new byte[checked((int)stream.Length)];
        stream.ReadExactly(model);
        var actualHash = Convert.ToHexString(SHA256.HashData(model));

        if (!actualHash.Equals(Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Checksum model Silero không khớp. Mong đợi {Sha256}, nhận được {actualHash}.");
        }

        return model;
    }
}
