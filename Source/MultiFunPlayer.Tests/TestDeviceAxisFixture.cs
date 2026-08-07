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
                DeviceAxis.InitializeFromDevice(new()
                {
                    Name = "baseline-test-device",
                    OutputPrecision = 4,
                    Axes = [
                        new() { Name = "L0", FriendlyName = "Up/Down", FunscriptNames = ["raw", "*", "stroke", "L0", "up"], Enabled = true, DefaultValue = 0.5 },
                        new() { Name = "L1", FriendlyName = "Forward/Backward", FunscriptNames = ["surge", "L1", "forward"], Enabled = true, DefaultValue = 0.5 },
                        new() { Name = "L2", FriendlyName = "Left/Right", FunscriptNames = ["sway", "L2", "left"], Enabled = true, DefaultValue = 0.5 },
                        new() { Name = "R0", FriendlyName = "Twist", FunscriptNames = ["twist", "R0", "yaw"], Enabled = true, DefaultValue = 0.5 },
                        new() { Name = "R1", FriendlyName = "Roll", FunscriptNames = ["roll", "R1"], Enabled = true, DefaultValue = 0.5 },
                        new() { Name = "R2", FriendlyName = "Pitch", FunscriptNames = ["pitch", "R2"], Enabled = true, DefaultValue = 0.5 }
                    ]
                });
        }
    }
}
