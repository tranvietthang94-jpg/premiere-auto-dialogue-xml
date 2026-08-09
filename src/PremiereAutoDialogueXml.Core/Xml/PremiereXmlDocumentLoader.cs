using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace PremiereAutoDialogueXml.Core.Xml;

public static class PremiereXmlDocumentLoader
{
    private const long MaximumSourceXmlBytes = 128L * 1024 * 1024;
    private const long MaximumGeneratedXmlBytes = 512L * 1024 * 1024;
    private const int PrologCharacterLimit = 4_096;
    private const string RequiredDocumentType = "<!DOCTYPE xmeml>";

    public static PremiereXmlSourceDocument Load(string xmlPath, string? expectedSha256 = null)
        => LoadCore(xmlPath, expectedSha256, MaximumSourceXmlBytes);

    public static PremiereXmlSourceDocument LoadGeneratedOutput(
        string xmlPath,
        string expectedSha256)
        => LoadCore(xmlPath, expectedSha256, MaximumGeneratedXmlBytes);

    public static void ValidateGeneratedOutputSyntax(
        string xmlPath,
        string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlPath);
        using var stream = new FileStream(
            xmlPath,
            new FileStreamOptions
            {
                Access = FileAccess.Read,
                Mode = FileMode.Open,
                Share = FileShare.Read,
                Options = FileOptions.SequentialScan
            });
        ValidateFileEnvelope(stream, expectedSha256, MaximumGeneratedXmlBytes);
        stream.Position = 0;
        ValidatePremiereDocumentType(stream);
        stream.Position = 0;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumGeneratedXmlBytes,
            IgnoreComments = false,
            IgnoreWhitespace = false,
            CloseInput = false
        };
        using var reader = XmlReader.Create(stream, settings);
        var rootCount = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0)
            {
                rootCount++;
                if (!reader.LocalName.Equals("xmeml", StringComparison.Ordinal))
                {
                    throw new PremiereXmlLoadException("xml-root-invalid", "XML kết quả không có root xmeml.");
                }
            }
        }

        if (rootCount != 1)
        {
            throw new PremiereXmlLoadException("xml-root-invalid", "XML kết quả phải có đúng một root xmeml.");
        }
    }

    private static PremiereXmlSourceDocument LoadCore(
        string xmlPath,
        string? expectedSha256,
        long maximumXmlBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlPath);
        using var stream = new FileStream(
            xmlPath,
            new FileStreamOptions
            {
                Access = FileAccess.Read,
                Mode = FileMode.Open,
                Share = FileShare.Read,
                Options = FileOptions.SequentialScan
            });

        var sourceHash = ValidateFileEnvelope(stream, expectedSha256, maximumXmlBytes);

        stream.Position = 0;
        ValidatePremiereDocumentType(stream);
        stream.Position = 0;
        return new(LoadDocument(stream, maximumXmlBytes), sourceHash);
    }

    private static string ValidateFileEnvelope(
        Stream stream,
        string? expectedSha256,
        long maximumXmlBytes)
    {
        if (stream.Length <= 0)
        {
            throw new PremiereXmlLoadException("xml-empty", "Tệp XML đang trống.");
        }

        if (stream.Length > maximumXmlBytes)
        {
            throw new PremiereXmlLoadException(
                "xml-too-large",
                $"XML vượt giới hạn an toàn {maximumXmlBytes / 1024 / 1024} MB của MVP.");
        }

        var sourceHash = Convert.ToHexString(SHA256.HashData(stream));
        if (expectedSha256 is not null &&
            !sourceHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new PremiereXmlLoadException(
                "xml-source-changed",
                "XML nguồn đã thay đổi sau bước kiểm tra; hãy kiểm tra và phân tích lại.");
        }

        return sourceHash;
    }

    private static XDocument LoadDocument(Stream stream, long maximumXmlBytes)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = maximumXmlBytes,
            IgnoreComments = false,
            IgnoreWhitespace = false,
            CloseInput = false
        };
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
    }

    private static void ValidatePremiereDocumentType(Stream stream)
    {
        using var reader = new StreamReader(
            stream,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: PrologCharacterLimit,
            leaveOpen: true);
        var buffer = new char[PrologCharacterLimit];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        var prolog = new string(buffer, 0, count);
        var documentTypeIndex = prolog.IndexOf("<!DOCTYPE", StringComparison.Ordinal);
        var rootIndex = prolog.IndexOf("<xmeml", StringComparison.Ordinal);
        if (documentTypeIndex < 0 || rootIndex < 0 || documentTypeIndex > rootIndex ||
            !prolog.AsSpan(documentTypeIndex).StartsWith(RequiredDocumentType, StringComparison.Ordinal))
        {
            throw new PremiereXmlLoadException(
                "doctype-not-allowed",
                "Chỉ chấp nhận DOCTYPE Premiere nguyên văn '<!DOCTYPE xmeml>'; external hoặc internal DTD bị từ chối.");
        }

        var afterRequiredType = documentTypeIndex + RequiredDocumentType.Length;
        if (prolog.IndexOf("<!DOCTYPE", afterRequiredType, StringComparison.Ordinal) >= 0)
        {
            throw new PremiereXmlLoadException("doctype-not-allowed", "XML chứa nhiều hơn một DOCTYPE.");
        }
    }
}

public sealed record PremiereXmlSourceDocument(XDocument Document, string Sha256);

public sealed class PremiereXmlLoadException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
