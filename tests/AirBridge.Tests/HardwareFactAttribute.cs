namespace AirBridge.Tests;

public sealed class HardwareFactAttribute : FactAttribute
{
    public HardwareFactAttribute()
    {
        Timeout = 15000;
        if (Environment.GetEnvironmentVariable("AIRBRIDGE_RUN_HARDWARE_TESTS") != "1")
            Skip = "Set AIRBRIDGE_RUN_HARDWARE_TESTS=1 to run local Windows audio checks.";
    }
}
