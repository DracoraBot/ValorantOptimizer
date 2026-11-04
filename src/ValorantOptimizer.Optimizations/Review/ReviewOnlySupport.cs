using System.Globalization;
using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Shared behavior for the read-only review cards. It centralizes the refusal semantics so every
/// card reports the same message and none of them can ever write through apply/restore.
/// </summary>
internal static class ReviewOnlySupport
{
    internal const string RefusalMessage =
        "Review-only module; this card inspects settings but never writes them, so apply/restore were refused. Nothing was changed.";

    internal const string MetadataNote =
        "Read-only review: captured metadata only; no restorable registry state and no writes.";

    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    internal static ChangeSet CreateChangeSet(
        string optimizationId,
        params (string Key, string Value)[] metadata)
    {
        var changeSet = new ChangeSet { OptimizationId = optimizationId };
        changeSet.Metadata["reviewOnly"] = "true";
        changeSet.Metadata["note"] = MetadataNote;

        foreach ((string key, string value) in metadata)
        {
            changeSet.Metadata[key] = value;
        }

        return changeSet;
    }

    /// <summary>Both <c>ApplyAsync</c> and <c>RestoreAsync</c> fail without touching the context.</summary>
    internal static Task<OperationOutcome> RefuseAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(original);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(OperationOutcome.Failed(RefusalMessage));
    }

    internal static string FormatBytes(ulong bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024d && unit < ByteUnits.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {ByteUnits[unit]}");
    }

    internal static string FormatBytes(long bytes) => FormatBytes(bytes <= 0 ? 0UL : (ulong)bytes);

    internal static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string Invariant(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads a value, treating expected registry access failures as "not readable".</summary>
    internal static RegistryValue? TryReadValue(
        IRegistryStore registry,
        RegistryHive hive,
        string subKey,
        string valueName)
    {
        try
        {
            return registry.ReadValue(hive, subKey, valueName);
        }
        catch (Exception ex) when (IsExpectedRegistryFailure(ex))
        {
            return null;
        }
    }

    internal static string? TryReadText(
        IRegistryStore registry,
        RegistryHive hive,
        string subKey,
        string valueName)
    {
        RegistryValue? value = TryReadValue(registry, hive, subKey, valueName);
        if (value is null)
        {
            return null;
        }

        try
        {
            string text = RegistryValueCodec.Serialize(value.Data, value.Kind).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidCastException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Enumerates subkeys, treating expected registry access failures as "not readable".</summary>
    internal static IReadOnlyList<string>? TryGetSubKeyNames(
        IRegistryStore registry,
        RegistryHive hive,
        string subKey)
    {
        try
        {
            return registry.GetSubKeyNames(hive, subKey);
        }
        catch (Exception ex) when (IsExpectedRegistryFailure(ex))
        {
            return null;
        }
    }

    private static bool IsExpectedRegistryFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or InvalidOperationException;
}
