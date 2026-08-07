using MultiFunPlayer.Common;

namespace MultiFunPlayer.Tests;

internal static class TestDeviceAxisFixture
{
    private static readonly object Sync = new();

    public static void EnsureInitialized()
    {
        lock (Sync)
        {
            if (DeviceAxis.All.IsDefaultOrEmpty)
                DeviceAxis.InitializeFromDevice(DeviceSettings.DefaultDevices[1]);
        }
    }
}
