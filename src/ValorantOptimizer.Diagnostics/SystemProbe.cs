using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ValorantOptimizer.Diagnostics;

/// <summary>
/// The machine probes that cannot be expressed through <see cref="Core.IRegistryStore"/>.
/// Extracted behind a small interface so the snapshot composition can be unit-tested without
/// depending on the hardware of the machine running the tests.
/// </summary>
internal interface ISystemProbe
{
    /// <summary>Total physical memory in bytes, or null when the probe is unavailable.</summary>
    ulong? GetTotalMemoryBytes();

    /// <summary>Physical core count, or null when the information is unavailable.</summary>
    int? GetPhysicalCoreCount();

    /// <summary>Current primary display mode, or null when no display is attached/readable.</summary>
    DisplayInfo? GetPrimaryDisplay();

    /// <summary>Whether the current process runs elevated, or null when it cannot be determined.</summary>
    bool? GetIsElevated();
}

/// <summary>Production probe backed by Win32 read-only APIs.</summary>
internal sealed class Win32SystemProbe : ISystemProbe
{
    public ulong? GetTotalMemoryBytes()
    {
        try
        {
            var status = new NativeMethods.MemoryStatusEx
            {
                Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>(),
            };

            if (!NativeMethods.GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
            {
                return null;
            }

            return status.TotalPhys;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public int? GetPhysicalCoreCount()
    {
        try
        {
            return TryGetPhysicalCoresEx() ?? TryGetPhysicalCoresLegacy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public DisplayInfo? GetPrimaryDisplay()
    {
        try
        {
            NativeMethods.DevMode mode = new()
            {
                DeviceName = string.Empty,
                FormName = string.Empty,
                Size = (short)Marshal.SizeOf<NativeMethods.DevMode>(),
            };

            if (!NativeMethods.EnumDisplaySettings(null, NativeMethods.EnumCurrentSettings, ref mode))
            {
                return null;
            }

            return SystemScanner.CreateDisplayInfo(
                TryGetPrimaryDeviceName(),
                mode.PelsWidth,
                mode.PelsHeight,
                mode.DisplayFrequency,
                mode.BitsPerPixel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public bool? GetIsElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Best effort adapter name for the primary display, e.g. <c>\\.\DISPLAY1</c>.</summary>
    private static string? TryGetPrimaryDeviceName()
    {
        for (uint index = 0; index < 8; index++)
        {
            NativeMethods.DisplayDevice device = new()
            {
                Cb = Marshal.SizeOf<NativeMethods.DisplayDevice>(),
                DeviceName = string.Empty,
                DeviceString = string.Empty,
                DeviceId = string.Empty,
                DeviceKey = string.Empty,
            };

            if (!NativeMethods.EnumDisplayDevices(null, index, ref device, 0))
            {
                break;
            }

            bool attached = (device.StateFlags & NativeMethods.DisplayDeviceAttachedToDesktop) != 0;
            bool primary = (device.StateFlags & NativeMethods.DisplayDevicePrimaryDevice) != 0;
            if (attached && primary)
            {
                return device.DeviceName;
            }
        }

        return null;
    }

    private static int? TryGetPhysicalCoresEx()
    {
        uint length = 0;
        NativeMethods.GetLogicalProcessorInformationEx(NativeMethods.RelationProcessorCore, IntPtr.Zero, ref length);
        if (length == 0 || length > int.MaxValue)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!NativeMethods.GetLogicalProcessorInformationEx(NativeMethods.RelationProcessorCore, buffer, ref length))
            {
                return null;
            }

            byte[] data = new byte[length];
            Marshal.Copy(buffer, data, 0, data.Length);
            return SystemScanner.CountPhysicalCores(data);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Fallback for pre-Windows 7 kernels. Record layout is an architecture-sized mask followed by
    /// a 4-byte relationship and a 12-byte union (24 bytes on x64, 20 bytes on x86).
    /// </summary>
    private static int? TryGetPhysicalCoresLegacy()
    {
        uint length = 0;
        NativeMethods.GetLogicalProcessorInformation(IntPtr.Zero, ref length);
        if (length == 0 || length > int.MaxValue)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!NativeMethods.GetLogicalProcessorInformation(buffer, ref length))
            {
                return null;
            }

            byte[] data = new byte[length];
            Marshal.Copy(buffer, data, 0, data.Length);

            int pointerSize = IntPtr.Size;
            int recordSize = pointerSize == 8 ? 24 : 20;
            return SystemScanner.CountPhysicalCoreRecords(data, recordSize, pointerSize);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
