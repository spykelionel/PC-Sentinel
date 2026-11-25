using System.Reflection;

namespace Sentinel.Core.Tests;

/// <summary>
/// Guards the dependency rules in docs/ARCHITECTURE.md: Sentinel.Core is the platform-neutral
/// domain and must not depend on infrastructure, Windows-only APIs, or UI frameworks.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly Assembly CoreAssembly = Assembly.Load("Sentinel.Core");

    [Theory]
    [InlineData("Sentinel.Infrastructure")]
    [InlineData("Sentinel.Service")]
    [InlineData("Sentinel.Desktop")]
    [InlineData("System.Management")]
    [InlineData("PresentationFramework")]
    [InlineData("Microsoft.Data.Sqlite")]
    public void Core_DoesNotReference_ForbiddenAssembly(string forbidden)
    {
        var references = CoreAssembly.GetReferencedAssemblies().Select(a => a.Name);

        Assert.DoesNotContain(forbidden, references);
    }

    [Fact]
    public void Core_TargetsPlatformNeutralFramework()
    {
        var platform = CoreAssembly.GetCustomAttribute<System.Runtime.Versioning.TargetPlatformAttribute>();

        Assert.Null(platform);
    }
}
