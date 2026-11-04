using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ValorantOptimizer.Optimizations;

/// <summary>One process working set reported by the memory review.</summary>
public sealed record MemoryConsumer(string Name, long WorkingSetBytes);

/// <summary>Read-only physical-memory and process-working-set observation.</summary>
public sealed record MemoryOverviewSnapshot(
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    IReadOnlyList<MemoryConsumer> LargestBackgroundProcesses)
{
    public ulong UsedPhysicalBytes =>
        TotalPhysicalBytes > AvailablePhysicalBytes ? TotalPhysicalBytes - AvailablePhysicalBytes : 0;
}

/// <summary>
/// Probe abstraction for the memory review so tests can supply deterministic memory and process
/// numbers instead of depending on the machine running the tests.
/// </summary>
public interface IMemoryOverviewProbe
{
    /// <summary>Returns the current memory snapshot, or null when it cannot be read.</summary>
    MemoryOverviewSnapshot? Read();
}

/// <summary>
/// Production probe built on <c>GlobalMemoryStatusEx</c> plus a bounded, exception-safe scan of
/// process working sets. It only queries state; it never trims, suspends or stops anything.
/// </summary>
public sealed class Win32MemoryOverviewProbe : IMemoryOverviewProbe
{
    /// <summary>The review card never shows more than three background processes.</summary>
    public const int MaximumBackgroundProcesses = 3;

    /// <summary>Upper bound on processes inspected so one assessment stays cheap.</summary>
    public const int MaximumProcessesInspected = 512;

    public MemoryOverviewSnapshot? Read()
    {
        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>(),
        };

        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
        {
            return null;
        }

        return new MemoryOverviewSnapshot(
            status.TotalPhys,
            status.AvailPhys,
            ReadLargestBackgroundProcesses());
    }

    private static IReadOnlyList<MemoryConsumer> ReadLargestBackgroundProcesses()
    {
        var consumers = new List<MemoryConsumer>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (IsExpectedProcessFailure(ex))
        {
            return consumers;
        }

        try
        {
            int inspected = 0;
            foreach (Process process in processes)
            {
                if (++inspected > MaximumProcessesInspected)
                {
                    break;
                }

                try
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    long workingSet = process.WorkingSet64;
                    if (workingSet <= 0 || !IsBackground(process))
                    {
                        continue;
                    }

                    string name = process.ProcessName;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    consumers.Add(new MemoryConsumer(name, workingSet));
                }
                catch (Exception ex) when (IsExpectedProcessFailure(ex))
                {
                    // Protected or exiting processes are skipped; the rest of the list is still usable.
                }
            }
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }

        return consumers
            .OrderByDescending(consumer => consumer.WorkingSetBytes)
            .ThenBy(consumer => consumer.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumBackgroundProcesses)
            .ToList();
    }

    private static bool IsBackground(Process process)
    {
        try
        {
            return process.MainWindowHandle == IntPtr.Zero;
        }
        catch (Exception ex) when (IsExpectedProcessFailure(ex))
        {
            // If Windows will not tell us, treat the process as background rather than dropping it.
            return true;
        }
    }

    private static bool IsExpectedProcessFailure(Exception exception) =>
        exception is Win32Exception
            or InvalidOperationException
            or NotSupportedException
            or UnauthorizedAccessException
            or System.Security.SecurityException;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
