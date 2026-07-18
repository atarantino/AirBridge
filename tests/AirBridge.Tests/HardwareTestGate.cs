namespace AirBridge.Tests;

internal static class HardwareTestGate
{
    public static bool Enabled => string.Equals(
        Environment.GetEnvironmentVariable("AIRBRIDGE_RUN_HARDWARE_TESTS"),
        "1",
        StringComparison.Ordinal);
}
