using System.Reflection;

namespace Sentinel.Infrastructure.Tests;

/// <summary>
/// Guards the dependency rules in docs/ARCHITECTURE.md: Sentinel.Infrastructure implements
/// Core contracts and must never depend on the host processes (service or desktop).
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly Assembly InfrastructureAssembly = Assembly.Load("Sentinel.Infrastructure");

    [Theory]
    [InlineData("Sentinel.Service")]
    [InlineData("Sentinel.Desktop")]
    [InlineData("PresentationFramework")]
    public void Infrastructure_DoesNotReference_HostAssembly(string forbidden)
    {
        var references = InfrastructureAssembly.GetReferencedAssemblies().Select(a => a.Name);

        Assert.DoesNotContain(forbidden, references);
    }
}
