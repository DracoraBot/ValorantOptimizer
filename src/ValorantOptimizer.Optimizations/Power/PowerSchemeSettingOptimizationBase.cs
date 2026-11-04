using System.Globalization;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Shared lifecycle for a single Windows power-plan AC setting managed through <c>powercfg.exe</c>.
/// Derived modules declare the subgroup/setting GUID and the recommended AC value; this class owns
/// the query/capture/apply/verify/restore/verify sequence so every power-setting module behaves the
/// same way. No registry value is written by these modules; undo state lives in the
/// <see cref="ChangeSet"/> metadata.
/// </summary>
public abstract class PowerSchemeSettingOptimizationBase : IOptimization
{
    public abstract OptimizationDescriptor Descriptor { get; }

    protected abstract string SubgroupGuid { get; }

    protected abstract string SettingGuid { get; }

    protected abstract uint RecommendedAcValue { get; }

    /// <summary>Name used in evidence and messages, e.g. "Core parking minimum cores".</summary>
    protected abstract string SettingDisplayName { get; }

    protected abstract string AppliedHeadline { get; }

    protected abstract string NotAppliedHeadline { get; }

    protected abstract string AssessmentExplanation { get; }

    protected abstract string ApplySuccessMessage { get; }

    public async Task<OptimizationAssessment> AssessAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        PowerSettingReadResult read = await PowerSchemeValueAccess.ReadAsync(
                context.Processes,
                SubgroupGuid,
                SettingGuid,
                cancellationToken)
            .ConfigureAwait(false);

        if (!read.Success || read.Setting is null)
        {
            return OptimizationAssessment.Unsupported(
                read.Error ?? "The power setting could not be read, so the toolkit will not guess a value.",
                $"{SettingDisplayName} could not be read");
        }

        PowerSettingQuery setting = read.Setting;
        bool applied = setting.AcValue == RecommendedAcValue;

        return new OptimizationAssessment
        {
            State = applied ? OptimizationState.Applied : OptimizationState.NotApplied,
            Headline = applied ? AppliedHeadline : NotAppliedHeadline,
            Explanation = AssessmentExplanation,
            Evidence =
            [
                new AssessmentEvidence("Active power plan", DescribeScheme(setting)),
                new AssessmentEvidence($"{SettingDisplayName} (AC)", FormatValue(setting.AcValue)),
                new AssessmentEvidence("Recommended (AC)", FormatValue(RecommendedAcValue)),
            ],
        };
    }

    public async Task<ChangeSet> CaptureAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        PowerSettingReadResult read = await PowerSchemeValueAccess.ReadAsync(
                context.Processes,
                SubgroupGuid,
                SettingGuid,
                cancellationToken)
            .ConfigureAwait(false);

        if (!read.Success || read.Setting is null)
        {
            throw new InvalidOperationException(
                read.Error ?? "The current power setting could not be read; no backup was created.");
        }

        return new ChangeSet
        {
            OptimizationId = Descriptor.Id,
            Entries = [],
            Metadata = PowerSchemeValueAccess.CreateMetadata(read.Setting),
        };
    }

    public async Task<OperationOutcome> ApplyAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(original);
        cancellationToken.ThrowIfCancellationRequested();

        PowerSettingWriteResult write = await PowerSchemeValueAccess.SetAcValueAndActivateAsync(
                context.Processes,
                PowerSchemeValueAccess.CurrentScheme,
                SubgroupGuid,
                SettingGuid,
                RecommendedAcValue,
                cancellationToken)
            .ConfigureAwait(false);

        if (!write.Success)
        {
            return OperationOutcome.Failed(
                write.Error ?? "powercfg did not accept the power setting change, so nothing was applied.");
        }

        PowerSettingReadResult verify = await PowerSchemeValueAccess.ReadAsync(
                context.Processes,
                SubgroupGuid,
                SettingGuid,
                cancellationToken)
            .ConfigureAwait(false);

        if (!verify.Success || verify.Setting is null)
        {
            return OperationOutcome.Failed(
                "The power setting command completed, but the new value could not be verified " +
                $"({verify.Error}). Use Undo to restore the previous value.");
        }

        if (verify.Setting.AcValue != RecommendedAcValue)
        {
            return OperationOutcome.Failed(
                $"The power setting did not change; the AC value is {FormatValue(verify.Setting.AcValue)} " +
                $"instead of the requested {FormatValue(RecommendedAcValue)}. Use Undo to restore the " +
                "previous value.");
        }

        var outcome = OperationOutcome.Ok(ApplySuccessMessage, verified: true);
        outcome.Changes.Add($"{SettingDisplayName} (AC): {FormatValue(verify.Setting.AcValue)}");
        return outcome;
    }

    public async Task<OperationOutcome> RestoreAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(original);
        cancellationToken.ThrowIfCancellationRequested();

        if (!PowerSchemeValueAccess.TryReadMetadata(
                original,
                out Guid originalScheme,
                out uint originalValue,
                out string? metadataError))
        {
            return OperationOutcome.Failed(
                metadataError ?? "The saved backup does not contain the original power setting.");
        }

        // Restore into the scheme that was active when the backup was captured and make that
        // scheme active again, so the exact original plan and value come back even if the user
        // switched plans in between.
        string scheme = PowerSchemeValueAccess.FormatGuid(originalScheme);
        PowerSettingWriteResult write = await PowerSchemeValueAccess.SetAcValueAndActivateAsync(
                context.Processes,
                scheme,
                SubgroupGuid,
                SettingGuid,
                originalValue,
                cancellationToken)
            .ConfigureAwait(false);

        if (!write.Success)
        {
            return OperationOutcome.Failed(
                write.Error ?? "powercfg did not accept the restore command, so the power setting was not restored.");
        }

        PowerSettingReadResult verify = await PowerSchemeValueAccess.ReadAsync(
                context.Processes,
                SubgroupGuid,
                SettingGuid,
                cancellationToken)
            .ConfigureAwait(false);

        if (!verify.Success || verify.Setting is null)
        {
            return OperationOutcome.Failed(
                "The restore command completed, but the original value could not be verified " +
                $"({verify.Error}).");
        }

        if (verify.Setting.AcValue != originalValue)
        {
            return OperationOutcome.Failed(
                $"The original power setting was not restored; the AC value is " +
                $"{FormatValue(verify.Setting.AcValue)} instead of {FormatValue(originalValue)}.");
        }

        var outcome = OperationOutcome.Ok(
            $"Restored {SettingDisplayName} to the captured AC value {FormatValue(originalValue)} " +
            $"on power plan {scheme}.",
            verified: true);
        outcome.Changes.Add($"{SettingDisplayName} (AC): {FormatValue(originalValue)}");
        return outcome;
    }

    /// <summary>Formats a raw setting index for display; derived modules can add units or labels.</summary>
    protected virtual string FormatValue(uint value) =>
        $"{value.ToString(CultureInfo.InvariantCulture)} (0x{value:X8})";

    private static string DescribeScheme(PowerSettingQuery setting) =>
        string.IsNullOrWhiteSpace(setting.ActiveSchemeName)
            ? PowerSchemeValueAccess.FormatGuid(setting.ActiveSchemeGuid)
            : $"{setting.ActiveSchemeName} ({PowerSchemeValueAccess.FormatGuid(setting.ActiveSchemeGuid)})";
}
