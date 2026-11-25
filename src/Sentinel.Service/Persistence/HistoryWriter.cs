using System.Globalization;
using System.Reflection;
using Sentinel.Core.Audit;
using Sentinel.Core.History;

namespace Sentinel.Service.Persistence;

public enum StorageHealth
{
    Initializing,
    Healthy,

    /// <summary>The database is unavailable; changes are buffered in memory (bounded) and retried.</summary>
    Degraded,
}

/// <summary>
/// Writes history through <see cref="IHistoryStore"/> without ever letting storage failures stop monitoring
/// (SECURITY.md §8). Changes are buffered in order while the database is unavailable. If the buffer overflows, the
/// oldest changes are dropped and an <see cref="AuditEventType.AuditEventsDropped"/> record with the count is written
/// once storage recovers. Not thread-safe; owned by the device orchestration loop.
/// </summary>
public sealed class HistoryWriter(
    IHistoryStore store,
    TimeProvider timeProvider,
    ILogger<HistoryWriter> logger,
    int retentionDays = HistoryWriter.DefaultRetentionDays)
{
    public const int DefaultRetentionDays = 365;
    public const int MaxPendingChanges = 10_000;

    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(24);

    private readonly List<HistoryChange> _pending = [];
    private bool _initialized;
    private long _dropped;
    private DateTimeOffset? _lastPurge;

    public StorageHealth Health { get; private set; } = StorageHealth.Initializing;

    public int PendingCount => _pending.Count;

    /// <summary>Records service start and closes connections left open by the previous run (ADR-007).</summary>
    public void Start()
    {
        var now = timeProvider.GetUtcNow();
        Enqueue(new AuditOnlyChange(new AuditRecord(now, AuditEventType.ServiceStarted, null, null,
            AuditRecords.Json(new()
            {
                ["version"] = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
                ["processId"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            }))));
        Enqueue(new CloseOpenConnectionsChange(now, ConnectionEndReason.ServiceRestart));
        Flush();
    }

    public void Record(IEnumerable<HistoryChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        foreach (var change in changes)
        {
            Enqueue(change);
        }

        Flush();
    }

    /// <summary>Closes open connections and records the stop. Anything still unwritten is reported as lost.</summary>
    public void Stop()
    {
        var now = timeProvider.GetUtcNow();
        Enqueue(new CloseOpenConnectionsChange(now, ConnectionEndReason.ServiceStopped));
        Enqueue(new AuditOnlyChange(new AuditRecord(now, AuditEventType.ServiceStopped, null, null, "{}")));
        Flush();

        if (_pending.Count > 0)
        {
            logger.LogError("Service stopping with {Count} history changes that could not be written; they are lost.", _pending.Count);
        }
    }

    /// <summary>Writes all buffered changes in one transaction. Called after every scan, which doubles as the retry.</summary>
    public void Flush()
    {
        if (!EnsureInitialized() || (_pending.Count == 0 && _dropped == 0))
        {
            return;
        }

        var batch = new List<HistoryChange>(_pending.Count + 1);
        if (_dropped > 0)
        {
            batch.Add(new AuditOnlyChange(new AuditRecord(timeProvider.GetUtcNow(), AuditEventType.AuditEventsDropped, null, null,
                AuditRecords.Json(new() { ["count"] = _dropped.ToString(CultureInfo.InvariantCulture) }))));
        }

        batch.AddRange(_pending);

        try
        {
            store.Apply(batch);
            _pending.Clear();
            _dropped = 0;
            SetHealth(StorageHealth.Healthy, null);
        }
        catch (Exception exception)
        {
            SetHealth(StorageHealth.Degraded, exception);
        }
    }

    /// <summary>Runs the retention purge at most once per <see cref="PurgeInterval"/>.</summary>
    public void PurgeIfDue()
    {
        var now = timeProvider.GetUtcNow();
        if (Health != StorageHealth.Healthy || (_lastPurge is { } last && now - last < PurgeInterval))
        {
            return;
        }

        _lastPurge = now;
        var cutoff = now.AddDays(-retentionDays);
        try
        {
            var removed = store.Purge(cutoff);
            if (removed > 0)
            {
                logger.LogInformation("Retention purge removed {Count} audit entries older than {Cutoff}.", removed, cutoff);
                Record([new AuditOnlyChange(new AuditRecord(now, AuditEventType.AuditPurged, null, null,
                    AuditRecords.Json(new()
                    {
                        ["removedEntries"] = removed.ToString(CultureInfo.InvariantCulture),
                        ["cutoff"] = AuditChain.FormatTimestamp(cutoff),
                    })))]);
            }
        }
        catch (InvalidOperationException exception)
        {
            logger.LogCritical(exception, "Retention purge refused: the audit chain failed verification.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Retention purge failed; it will be retried in {Interval}.", PurgeInterval);
        }
    }

    private bool EnsureInitialized()
    {
        if (_initialized)
        {
            return true;
        }

        try
        {
            store.Initialize();
            var verification = store.VerifyAuditChain();
            _initialized = true;

            if (verification.IsValid)
            {
                logger.LogInformation("History storage ready. Audit chain verified: {Count} entries.", verification.EntriesChecked);
            }
            else
            {
                logger.LogCritical(
                    "Audit chain verification FAILED at sequence {Sequence}: {Failure}",
                    verification.FailedAtSequence,
                    verification.Failure);
                Enqueue(new AuditOnlyChange(new AuditRecord(timeProvider.GetUtcNow(), AuditEventType.AuditChainInvalid, null, null,
                    AuditRecords.Json(new()
                    {
                        ["failedAtSequence"] = verification.FailedAtSequence?.ToString(CultureInfo.InvariantCulture),
                        ["failure"] = verification.Failure,
                    }))));
            }

            return true;
        }
        catch (Exception exception)
        {
            SetHealth(StorageHealth.Degraded, exception);
            return false;
        }
    }

    private void Enqueue(HistoryChange change)
    {
        if (_pending.Count >= MaxPendingChanges)
        {
            _pending.RemoveAt(0);
            _dropped++;
        }

        _pending.Add(change);
    }

    private void SetHealth(StorageHealth health, Exception? exception)
    {
        if (health == Health)
        {
            return;
        }

        var previous = Health;
        Health = health;

        if (health == StorageHealth.Degraded)
        {
            logger.LogError(exception, "History storage unavailable. Monitoring continues; {Count} changes buffered in memory.", _pending.Count);
        }
        else if (previous == StorageHealth.Degraded)
        {
            logger.LogInformation("History storage recovered; buffered changes written.");
        }
    }
}
