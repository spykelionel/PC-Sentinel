namespace Sentinel.Infrastructure.Tests;

/// <summary>
/// A test that needs a USB storage device connected. Skipped unless SENTINEL_HARDWARE_TESTS=1,
/// so the default <c>dotnet test</c> run stays green on any machine.
/// Combine with <c>[Trait("Category", "Hardware")]</c>.
/// </summary>
public sealed class HardwareFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "SENTINEL_HARDWARE_TESTS";

    public HardwareFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
        {
            Skip = $"Requires a connected USB storage device. Set {EnvironmentVariable}=1 to run.";
        }
    }
}
