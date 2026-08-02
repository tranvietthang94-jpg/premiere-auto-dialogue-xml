namespace PremiereAutoDialogueXml.Core.Validation;

public static class InputSelectionValidator
{
    public static SelectionValidationResult Validate(InputSelectionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var issues = new List<ValidationIssue>();
        ValidateXml(facts, issues);
        ValidateOutputDirectory(facts, issues);
        return new(issues);
    }

    private static void ValidateXml(InputSelectionFacts facts, ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(facts.XmlPath))
        {
            issues.Add(new("xml-required", "Hãy chọn tệp XML do Premiere xuất."));
            return;
        }

        string extension;
        try
        {
            extension = Path.GetExtension(facts.XmlPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            issues.Add(new("xml-path-invalid", "Đường dẫn XML không hợp lệ."));
            return;
        }

        if (!string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("xml-extension-invalid", "Tệp đầu vào phải có phần mở rộng .xml."));
        }

        if (!facts.XmlExists)
        {
            issues.Add(new("xml-not-found", "Không tìm thấy tệp XML đã chọn."));
        }
        else if (facts.XmlLength <= 0)
        {
            issues.Add(new("xml-empty", "Tệp XML đang trống."));
        }
    }

    private static void ValidateOutputDirectory(InputSelectionFacts facts, ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(facts.OutputDirectory))
        {
            issues.Add(new("output-required", "Hãy chọn thư mục lưu kết quả."));
            return;
        }

        if (!facts.OutputDirectoryExists)
        {
            issues.Add(new("output-not-found", "Thư mục lưu kết quả không tồn tại."));
        }
    }
}
