using Sentinel.Core.Audit;

namespace Sentinel.Core.Tests.Audit;

public sealed class AuditChainTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    private static AuditRecord Record(int i, string details = "{}") =>
        new(T0.AddMinutes(i), AuditEventType.DeviceConnected, Guid.Parse("3294c600-b16f-430a-bf0c-cccdd7e30b39"), "v1:abc", details);

    private static List<AuditEntry> Chain(int count, AuditAnchor? anchor = null)
    {
        var entries = new List<AuditEntry>();
        var sequence = anchor?.Sequence ?? 0;
        var previous = anchor?.Hash ?? AuditChain.GenesisHash;
        for (var i = 0; i < count; i++)
        {
            var entry = AuditChain.CreateEntry(++sequence, Record(i), previous);
            entries.Add(entry);
            previous = entry.Hash;
        }

        return entries;
    }

    [Fact]
    public void Verify_EmptyChain_IsValid()
    {
        Assert.True(AuditChain.Verify([], null).IsValid);
    }

    [Fact]
    public void Verify_IntactChain_IsValid()
    {
        var result = AuditChain.Verify(Chain(10), null);

        Assert.True(result.IsValid);
        Assert.Equal(10, result.EntriesChecked);
    }

    [Fact]
    public void Verify_ModifiedDetails_FailsAtThatEntry()
    {
        var chain = Chain(5);
        chain[2] = chain[2] with { Details = """{"tampered":"yes"}""" };

        var result = AuditChain.Verify(chain, null);

        Assert.False(result.IsValid);
        Assert.Equal(3, result.FailedAtSequence);
    }

    [Fact]
    public void Verify_ModifiedTimestamp_Fails()
    {
        var chain = Chain(3);
        chain[1] = chain[1] with { OccurredAt = chain[1].OccurredAt.AddSeconds(1) };

        Assert.Equal(2, AuditChain.Verify(chain, null).FailedAtSequence);
    }

    [Fact]
    public void Verify_DeletedEntry_Fails()
    {
        var chain = Chain(5);
        chain.RemoveAt(2);

        var result = AuditChain.Verify(chain, null);

        Assert.False(result.IsValid);
        Assert.Equal(4, result.FailedAtSequence);
    }

    [Fact]
    public void Verify_ReorderedEntries_Fails()
    {
        var chain = Chain(4);
        (chain[1], chain[2]) = (chain[2], chain[1]);

        Assert.False(AuditChain.Verify(chain, null).IsValid);
    }

    [Fact]
    public void Verify_RecomputedHashWithoutRelinking_Fails()
    {
        // An attacker edits an entry and recomputes its hash, but does not rewrite every later entry.
        var chain = Chain(4);
        var edited = new AuditRecord(chain[1].OccurredAt, chain[1].Type, chain[1].ConnectionId, chain[1].DeviceKey, """{"x":1}""");
        chain[1] = AuditChain.CreateEntry(2, edited, chain[0].Hash);

        var result = AuditChain.Verify(chain, null);

        Assert.False(result.IsValid);
        Assert.Equal(3, result.FailedAtSequence);
    }

    [Fact]
    public void Verify_TailTruncation_IsNotDetectable_DocumentedLimitation()
    {
        // ADR-014: removing entries from the end leaves a valid shorter chain. Detection needs an external copy of the head.
        var chain = Chain(5);
        chain.RemoveRange(3, 2);

        Assert.True(AuditChain.Verify(chain, null).IsValid);
    }

    [Fact]
    public void Verify_FromAnchor_ContinuesChain()
    {
        var anchor = new AuditAnchor(100, new string('a', 64));

        Assert.True(AuditChain.Verify(Chain(3, anchor), anchor).IsValid);
        Assert.False(AuditChain.Verify(Chain(3, anchor), null).IsValid);
    }

    [Fact]
    public void ComputeHash_FieldBoundaries_AreUnambiguous()
    {
        var a = new AuditRecord(T0, AuditEventType.DeviceConnected, null, "ab", "c");
        var b = new AuditRecord(T0, AuditEventType.DeviceConnected, null, "a", "bc");

        Assert.NotEqual(AuditChain.ComputeHash(1, a, AuditChain.GenesisHash), AuditChain.ComputeHash(1, b, AuditChain.GenesisHash));
    }

    [Fact]
    public void FormatTimestamp_NormalizesToUtc()
    {
        var local = new DateTimeOffset(2026, 11, 25, 11, 0, 0, TimeSpan.FromHours(1));

        Assert.Equal("2026-11-25T10:00:00.0000000Z", AuditChain.FormatTimestamp(local));
    }
}
