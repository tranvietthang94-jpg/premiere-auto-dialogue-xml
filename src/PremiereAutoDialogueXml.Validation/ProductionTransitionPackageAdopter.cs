using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PremiereAutoDialogueXml.Core.ProjectModel;
using PremiereAutoDialogueXml.Output;
using PremiereAutoDialogueXml.Output.Audit;

namespace PremiereAutoDialogueXml.Validation;

public sealed class ProductionTransitionPackageAdopter
{
    public const string Policy = "phase16-production-transition-adoption-v1";
    public const int MaximumTransitions = 12;
    public const int MaximumCapturedBoundarySamples = 1_024;

    private static readonly JsonSerializerOptions AuditJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<OutputPackageResult> AdoptAsync(
        PremiereProject project,
        OutputPackageResult package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(package);
        ValidatePackageEnvelope(package);
        cancellationToken.ThrowIfCancellationRequested();

        var audit = await ReadAuditAsync(package.AuditPath, cancellationToken);
        ValidateAuditEnvelope(project, package, audit);
        var baselineAuditSha256 = await ComputeSha256Async(package.AuditPath, cancellationToken);
        var scan = new BoundaryDiscontinuityScanner().Scan(
            audit,
            project,
            maximumCapturedSamples: MaximumCapturedBoundarySamples);
        var safety = new PremiereTransitionGainSafetyGate().Evaluate(
            scan,
            audit,
            project,
            cancellationToken: cancellationToken);
        var plan = new PremiereConstantGainSafeBatchPlanner().Plan(safety, MaximumTransitions);
        cancellationToken.ThrowIfCancellationRequested();

        var runDirectory = Path.GetFullPath(package.RunDirectory);
        var token = Guid.NewGuid().ToString("N");
        var transitionedXmlPath = Path.Combine(
            runDirectory,
            $".{Path.GetFileName(package.XmlPath)}.{token}.phase16.tmp");
        var updatedAuditPath = Path.Combine(
            runDirectory,
            $".{Path.GetFileName(package.AuditPath)}.{token}.phase16.tmp");
        string? finalXmlSha256 = package.OutputXmlSha256;
        PremiereConstantGainBatchCandidateResult? candidate = null;
        try
        {
            if (plan.SelectedCount > 0)
            {
                candidate = await new PremiereConstantGainBatchCandidateWriter().WriteAsync(
                    package.XmlPath,
                    package.OutputXmlSha256,
                    audit,
                    baselineAuditSha256,
                    transitionedXmlPath,
                    plan.Selected,
                    cancellationToken);
                finalXmlSha256 = candidate.OutputXmlSha256;
            }

            var transitionAudit = BuildAudit(scan, safety, plan);
            var updatedAudit = audit with
            {
                SchemaVersion = "2.0",
                OutputXmlSha256 = finalXmlSha256,
                TransitionSafety = transitionAudit
            };
            await WriteAuditAsync(updatedAuditPath, updatedAudit, cancellationToken);
            await ValidateWrittenAuditAsync(updatedAuditPath, updatedAudit, cancellationToken);
            CommitPackage(
                package.XmlPath,
                package.AuditPath,
                transitionedXmlPath,
                updatedAuditPath,
                candidate is not null,
                token);

            return package with
            {
                OutputXmlSha256 = finalXmlSha256,
                TransitionCount = plan.SelectedCount
            };
        }
        finally
        {
            DeleteIfExists(transitionedXmlPath);
            DeleteIfExists(updatedAuditPath);
        }
    }

    private static TransitionSafetyAudit BuildAudit(
        BoundaryDiscontinuityScanReport scan,
        PremiereTransitionGainSafetyReport safety,
        PremiereConstantGainSafeBatchPlan plan)
    {
        var decisions = safety.Decisions.ToDictionary(
            item => (item.Request.TrackIndex, item.Request.BoundaryFrame));
        var boundaries = plan.Selected.Select(request =>
        {
            var decision = decisions[(request.TrackIndex, request.BoundaryFrame)];
            return new TransitionBoundaryAudit(
                request.TrackIndex,
                request.BoundaryFrame,
                request.ExpectedKind.ToString(),
                request.RenderedStepDbfs,
                request.StepAboveLocalP99Db,
                decision.AffectedPhraseIds.ToArray());
        }).ToArray();

        return new(
            "1.0",
            Policy,
            scan.Policy,
            safety.Policy,
            plan.Policy,
            PremiereConstantGainBatchCandidateWriter.Policy,
            MaximumTransitions,
            scan.TransitionCount,
            scan.TransientScreeningCandidateCount,
            safety.CapturedTransientCandidateCount,
            safety.UncapturedTransientCandidateCount,
            safety.EligibleCount,
            safety.RejectedCount,
            safety.RejectedMissingPhraseEvidenceCount,
            safety.RejectedSourcePeakMismatchCount,
            safety.RejectedNoRetainedPeakCount,
            safety.RejectedExpectedPeakLossCount,
            plan.SelectedCount,
            plan.EnabledToDisabledSelectedCount,
            plan.DisabledToEnabledSelectedCount,
            plan.GainChangeSelectedCount,
            scan.TransitionStreamSha256,
            safety.DecisionStreamSha256,
            plan.SelectionStreamSha256,
            boundaries);
    }

    private static async Task<OutputAudit> ReadAuditAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<OutputAudit>(stream, AuditJsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Audit production rỗng hoặc không hợp lệ.");
    }

    private static async Task WriteAuditAsync(
        string path,
        OutputAudit audit,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, audit, AuditJsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task ValidateWrittenAuditAsync(
        string path,
        OutputAudit expected,
        CancellationToken cancellationToken)
    {
        var actual = await ReadAuditAsync(path, cancellationToken);
        if (!string.Equals(actual.OutputXmlSha256, expected.OutputXmlSha256, StringComparison.Ordinal) ||
            !string.Equals(actual.SchemaVersion, "2.0", StringComparison.Ordinal) ||
            actual.TransitionSafety is null ||
            actual.TransitionSafety.SelectedCount != expected.TransitionSafety?.SelectedCount ||
            !string.Equals(
                actual.TransitionSafety.SelectionStreamSha256,
                expected.TransitionSafety?.SelectionStreamSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Audit Phase 16 đọc lại không khớp dữ liệu đã ghi.");
        }
    }

    private static void CommitPackage(
        string xmlPath,
        string auditPath,
        string transitionedXmlPath,
        string updatedAuditPath,
        bool replaceXml,
        string token)
    {
        var xmlBackupPath = $"{xmlPath}.{token}.phase16-backup";
        var auditBackupPath = $"{auditPath}.{token}.phase16-backup";
        try
        {
            if (replaceXml)
            {
                File.Move(xmlPath, xmlBackupPath);
                File.Move(transitionedXmlPath, xmlPath);
            }

            File.Move(auditPath, auditBackupPath);
            File.Move(updatedAuditPath, auditPath);
            DeleteIfExists(xmlBackupPath);
            DeleteIfExists(auditBackupPath);
        }
        catch
        {
            if (File.Exists(auditBackupPath))
            {
                DeleteIfExists(auditPath);
                File.Move(auditBackupPath, auditPath);
            }

            if (File.Exists(xmlBackupPath))
            {
                DeleteIfExists(xmlPath);
                File.Move(xmlBackupPath, xmlPath);
            }

            throw;
        }
    }

    private static void ValidatePackageEnvelope(OutputPackageResult package)
    {
        var runDirectory = Path.GetFullPath(package.RunDirectory);
        var xmlPath = Path.GetFullPath(package.XmlPath);
        var auditPath = Path.GetFullPath(package.AuditPath);
        if (!Directory.Exists(runDirectory) ||
            !File.Exists(xmlPath) ||
            !File.Exists(auditPath) ||
            !string.Equals(Path.GetDirectoryName(xmlPath), runDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(auditPath), runDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Output package không thuộc đúng thư mục run Phase 16.");
        }
    }

    private static void ValidateAuditEnvelope(
        PremiereProject project,
        OutputPackageResult package,
        OutputAudit audit)
    {
        if (!string.Equals(audit.SourceXmlSha256, project.SourceXmlSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(audit.OutputXmlSha256, package.OutputXmlSha256, StringComparison.Ordinal) ||
            !string.Equals(audit.OutputXmlFileName, Path.GetFileName(package.XmlPath), StringComparison.Ordinal) ||
            audit.TransitionSafety is not null)
        {
            throw new InvalidDataException("Audit baseline không khớp output package trước adoption.");
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
