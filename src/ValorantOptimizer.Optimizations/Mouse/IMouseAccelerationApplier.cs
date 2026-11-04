using System.Runtime.InteropServices;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Applies the mouse-acceleration preference to the live Windows session. The registry values
/// written by <see cref="MouseAccelerationOptimization"/> are read by Windows at sign-in; this
/// abstraction performs the extra <c>SystemParametersInfo</c> call that makes the change take
/// effect immediately. It is injectable so tests never touch the real machine.
/// </summary>
public interface IMouseAccelerationApplier
{
    /// <summary>
    /// Applies the three Windows pointer parameters (speed, first threshold, second threshold)
    /// to the current session. Returns <c>false</c> when the operating system reports failure.
    /// </summary>
    bool TryApply(int speed, int threshold1, int threshold2);
}

/// <summary>
/// Production applier. Calls <c>SystemParametersInfo(SPI_SETMOUSE = 0x0004, 0, { speed, t1, t2 },
/// SPIF_UPDATEINIFILE | SPIF_SENDCHANGE)</c>, which updates the per-user input setting and
/// broadcasts the change to the current session.
/// </summary>
public sealed class Win32MouseAccelerationApplier : IMouseAccelerationApplier
{
    private const uint SpiSetMouse = 0x0004;
    private const uint SpifUpdateIniFile = 0x0001;
    private const uint SpifSendChange = 0x0002;

    public bool TryApply(int speed, int threshold1, int threshold2)
    {
        // SPI_SETMOUSE expects a pointer to exactly three consecutive integers:
        // mouse speed, first threshold and second threshold.
        int[] parameters = [speed, threshold1, threshold2];
        return SystemParametersInfo(
            SpiSetMouse,
            0,
            parameters,
            SpifUpdateIniFile | SpifSendChange);
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint uiAction,
        uint uiParam,
        int[] pvParam,
        uint fWinIni);
}
