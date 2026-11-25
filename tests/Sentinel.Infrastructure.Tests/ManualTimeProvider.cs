namespace Sentinel.Infrastructure.Tests;

/// <summary>A clock that only moves when the test moves it.</summary>
public sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
