using System.Runtime.InteropServices;

namespace MicTap.App;

/// <summary>
/// Keeps Windows from sleeping while a recording runs. The request belongs to the calling thread, so it is made
/// from the UI thread, which lives as long as the app. The display may still turn off.
/// </summary>
internal static class SystemSleep
{
    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        Continuous = 0x80000000,
    }

    /// <returns>False if Windows rejected the request.</returns>
    public static bool Block() => SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired) != 0;

    /// <returns>False if Windows rejected the request.</returns>
    public static bool Allow() => SetThreadExecutionState(ExecutionState.Continuous) != 0;

    [DllImport("kernel32.dll")]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState state);
}
