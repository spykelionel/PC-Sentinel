using Microsoft.Extensions.Logging.Abstractions;
using Sentinel.Core.Audit;
using Sentinel.Core.History;
using Sentinel.Service.Persistence;

namespace Sentinel.Service.Tests.Persistence;

public sealed class HistoryWriterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeHistoryStore _store = new();
    private readonly ManualTimeProvider _time = new(T0);
    private readonly HistoryWriter _writer;

    public HistoryWriterTests()
    {
        _writer = new HistoryWriter(_store, _time, NullLogger<HistoryWriter>.Instance, retentionDays: 30);
    }

    [Fact]
    public void Start_RecordsServiceStarted_ThenClosesConnectionsFromPreviousRun()
    {
        _writer.Start();

        Assert.Equal(StorageHealth.Healthy, _writer.Health);
        Assert.Collection(
            _store.Applied,
            c => Assert.Equal(AuditEventType.ServiceStarted, Assert.IsType<AuditOnlyChange>(c).Record.Type),
            c => Assert.Equal(ConnectionEndReason.ServiceRestart, Assert.IsType<CloseOpenConnectionsChange>(c).Reason));
    }

    [Fact]
    public void Stop_ClosesConnections_ThenRecordsServiceStopped()
    {
        _writer.Start();
        _store.Applied.Clear();

        _writer.Stop();

        Assert.Collection(
            _store.Applied,
            c => Assert.Equal(ConnectionEndReason.ServiceStopped, Assert.IsType<CloseOpenConnectionsChange>(c).Reason),
            c => Assert.Equal(AuditEventType.ServiceStopped, Assert.IsType<AuditOnlyChange>(c).Record.Type));
    }

    [Fact]
    public void ApplyFailure_BuffersInOrder_AndRecoveryWritesEverything()
    {
        _writer.Start();
        _store.Applied.Clear();
        _store.FailApply = true;

        _writer.Record([Audit(1)]);
        _writer.Record([Audit(2)]);

        Assert.Equal(StorageHealth.Degraded, _writer.Health);
        Assert.Equal(2, _writer.PendingCount);
        Assert.Empty(_store.Applied);

        _store.FailApply = false;
        _writer.Flush();

        Assert.Equal(StorageHealth.Healthy, _writer.Health);
        Assert.Equal(0, _writer.PendingCount);
        Assert.Equal(["1", "2"], _store.Applied.Cast<AuditOnlyChange>().Select(c => c.Record.Details));
    }

    [Fact]
    public void InitializeFailure_IsRetriedOnNextFlush()
    {
        _store.FailInitialize = true;
        _writer.Start();

        Assert.Equal(StorageHealth.Degraded, _writer.Health);
        Assert.Equal(2, _writer.PendingCount);

        _store.FailInitialize = false;
        _writer.Flush();

        Assert.Equal(StorageHealth.Healthy, _writer.Health);
        Assert.Equal(2, _store.Applied.Count);
    }

    [Fact]
    public void BufferOverflow_DropsOldest_AndRecordsDroppedCount()
    {
        _writer.Start();
        _store.Applied.Clear();
        _store.FailApply = true;

        _writer.Record(Enumerable.Range(0, HistoryWriter.MaxPendingChanges + 5).Select(Audit));
        Assert.Equal(HistoryWriter.MaxPendingChanges, _writer.PendingCount);

        _store.FailApply = false;
        _writer.Flush();

        var first = Assert.IsType<AuditOnlyChange>(_store.Applied[0]);
        Assert.Equal(AuditEventType.AuditEventsDropped, first.Record.Type);
        Assert.Contains("\"count\":\"5\"", first.Record.Details, StringComparison.Ordinal);
        Assert.Equal("5", Assert.IsType<AuditOnlyChange>(_store.Applied[1]).Record.Details);
    }

    [Fact]
    public void InvalidChainAtStartup_IsRecordedAsAuditEvent()
    {
        _store.Verification = AuditVerificationResult.Invalid(3, 4, "Entry modified.");

        _writer.Start();

        var invalid = _store.Applied.OfType<AuditOnlyChange>().Single(c => c.Record.Type == AuditEventType.AuditChainInvalid);
        Assert.Contains("\"failedAtSequence\":\"4\"", invalid.Record.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void PurgeIfDue_RunsAtMostOncePerDay_WithRetentionCutoff()
    {
        _writer.Start();

        _writer.PurgeIfDue();
        _time.Advance(TimeSpan.FromHours(23));
        _writer.PurgeIfDue();

        Assert.Equal([T0.AddDays(-30)], _store.PurgeCutoffs);

        _time.Advance(TimeSpan.FromHours(2));
        _writer.PurgeIfDue();

        Assert.Equal(2, _store.PurgeCutoffs.Count);
    }

    [Fact]
    public void PurgeIfDue_AuditsRemovals()
    {
        _writer.Start();
        _store.PurgeResult = 12;

        _writer.PurgeIfDue();

        var purged = _store.Applied.OfType<AuditOnlyChange>().Single(c => c.Record.Type == AuditEventType.AuditPurged);
        Assert.Contains("\"removedEntries\":\"12\"", purged.Record.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void PurgeIfDue_RefusedPurge_DoesNotThrow()
    {
        _writer.Start();
        _store.PurgeException = new InvalidOperationException("chain broken");

        _writer.PurgeIfDue();

        Assert.Equal(StorageHealth.Healthy, _writer.Health);
    }

    [Fact]
    public void PurgeIfDue_SkippedWhileDegraded()
    {
        _store.FailInitialize = true;
        _writer.Start();

        _writer.PurgeIfDue();

        Assert.Empty(_store.PurgeCutoffs);
    }

    private static AuditOnlyChange Audit(int i) =>
        new(new AuditRecord(T0, AuditEventType.VolumeMounted, null, null, i.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private sealed class FakeHistoryStore : IHistoryStore
    {
        public List<HistoryChange> Applied { get; } = [];

        public List<DateTimeOffset> PurgeCutoffs { get; } = [];

        public bool FailInitialize { get; set; }

        public bool FailApply { get; set; }

        public long PurgeResult { get; set; }

        public Exception? PurgeException { get; set; }

        public AuditVerificationResult Verification { get; set; } = AuditVerificationResult.Valid(0);

        public void Initialize()
        {
            if (FailInitialize)
            {
                throw new IOException("database unavailable");
            }
        }

        public void Apply(IReadOnlyList<HistoryChange> changes)
        {
            if (FailApply)
            {
                throw new IOException("database locked");
            }

            Applied.AddRange(changes);
        }

        public AuditVerificationResult VerifyAuditChain() => Verification;

        public long Purge(DateTimeOffset cutoff)
        {
            PurgeCutoffs.Add(cutoff);
            return PurgeException is null ? PurgeResult : throw PurgeException;
        }

        public IReadOnlyList<ConnectionHistoryEntry> GetRecentConnections(int count) => [];
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
