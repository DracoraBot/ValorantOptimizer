namespace ValorantOptimizer.Optimizations;

using System.Diagnostics;

/// <summary>Free/total capacity for one drive root.</summary>
public sealed record SystemDriveSpace(string RootPath, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes > FreeBytes ? TotalBytes - FreeBytes : 0;

    public int FreePercent => TotalBytes <= 0 ? 0 : (int)Math.Round(FreeBytes * 100d / TotalBytes, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Probe abstraction for the storage review so tests can supply deterministic drive capacity and
/// temp-folder sizes instead of depending on the machine running the tests.
/// </summary>
public interface IStorageOverviewProbe
{
    /// <summary>Reads capacity for a drive root, or null when it is unavailable.</summary>
    SystemDriveSpace? ReadDrive(string rootPath);

    /// <summary>Measures a directory tree, bounded, or null when it cannot be measured.</summary>
    long? MeasureDirectoryBytes(string directoryPath);
}

/// <summary>
/// Production probe built on <see cref="DriveInfo"/> and a bounded, exception-safe directory
/// enumeration. It only reads metadata; nothing is deleted, moved or created.
/// </summary>
public sealed class SystemStorageOverviewProbe : IStorageOverviewProbe
{
    /// <summary>Upper bound on files inspected so a huge temp tree cannot stall an assessment.</summary>
    public const int MaximumEntriesInspected = 5_000;

    /// <summary>Upper bound on directory recursion depth for the temp estimate.</summary>
    public const int MaximumRecursionDepth = 16;

    /// <summary>
    /// Wall-clock budget for the temp estimate. Review cards run on the UI scan path, so a huge
    /// temp tree must never freeze the first paint for several seconds.
    /// </summary>
    public static readonly TimeSpan MaximumElapsed = TimeSpan.FromMilliseconds(1200);

    public SystemDriveSpace? ReadDrive(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        try
        {
            var drive = new DriveInfo(rootPath);
            if (!drive.IsReady)
            {
                return null;
            }

            return new SystemDriveSpace(
                drive.RootDirectory.FullName,
                drive.TotalSize,
                drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (IsExpectedFileSystemFailure(ex))
        {
            return null;
        }
    }

    public long? MeasureDirectoryBytes(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return null;
        }

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MaxRecursionDepth = MaximumRecursionDepth,
            };

            long total = 0;
            int inspected = 0;
            long startedAt = Stopwatch.GetTimestamp();
            foreach (string file in Directory.EnumerateFiles(directoryPath, "*", options))
            {
                if (++inspected > MaximumEntriesInspected ||
                    Stopwatch.GetElapsedTime(startedAt) > MaximumElapsed)
                {
                    break;
                }

                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (IsExpectedFileSystemFailure(ex))
                {
                    // Files that disappear during enumeration are skipped; the estimate stays useful.
                }
            }

            return total;
        }
        catch (Exception ex) when (IsExpectedFileSystemFailure(ex))
        {
            return null;
        }
    }

    private static bool IsExpectedFileSystemFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException;
}
