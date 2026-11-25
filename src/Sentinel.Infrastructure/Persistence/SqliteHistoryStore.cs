using System.Globalization;
using Microsoft.Data.Sqlite;
using Sentinel.Core.Audit;
using Sentinel.Core.Devices;
using Sentinel.Core.History;

namespace Sentinel.Infrastructure.Persistence;

/// <summary>
/// SQLite implementation of <see cref="IHistoryStore"/> (ADR-009). The service is the single writer.
/// Every <see cref="Apply"/> call is one transaction: history rows and their audit entries commit together.
/// </summary>
public sealed class SqliteHistoryStore(string databasePath, TimeProvider timeProvider) : IHistoryStore
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
        ForeignKeys = true,
    }.ToString();

    public string DatabasePath { get; } = databasePath;

    public MigrationResult? LastMigration { get; private set; }

    public void Initialize()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = Open();
        LastMigration = DatabaseMigrator.Migrate(connection, DatabasePath, timeProvider);
    }

    public void Apply(IReadOnlyList<HistoryChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return;
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var writer = new BatchWriter(connection, transaction, ReadHead(connection, transaction));

        foreach (var change in changes)
        {
            switch (change)
            {
                case DeviceLifecycleChange { Event: var lifecycleEvent }:
                    writer.WriteLifecycle(lifecycleEvent);
                    writer.AppendAudit(AuditRecords.From(lifecycleEvent));
                    break;

                case AuditOnlyChange { Record: var record }:
                    writer.AppendAudit(record);
                    break;

                case CloseOpenConnectionsChange close:
                    var closed = writer.CloseOpenConnections(close.OccurredAt, close.Reason);
                    if (closed > 0)
                    {
                        writer.AppendAudit(new AuditRecord(close.OccurredAt, AuditEventType.ConnectionsClosed, null, null,
                            AuditRecords.Json(new()
                            {
                                ["reason"] = close.Reason.ToString(),
                                ["count"] = closed.ToString(CultureInfo.InvariantCulture),
                            })));
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(changes), change.GetType().Name, "Unknown history change.");
            }
        }

        transaction.Commit();
    }

    public AuditVerificationResult VerifyAuditChain()
    {
        using var connection = Open();
        return AuditChain.Verify(ReadAuditEntries(connection), ReadAnchor(connection, null));
    }

    public long Purge(DateTimeOffset cutoff)
    {
        using var connection = Open();

        // Never discard evidence of tampering: purging a broken chain would re-anchor it and hide the break.
        var verification = AuditChain.Verify(ReadAuditEntries(connection), ReadAnchor(connection, null));
        if (!verification.IsValid)
        {
            throw new InvalidOperationException($"Audit chain is invalid at sequence {verification.FailedAtSequence}; purge refused.");
        }

        var cutoffText = AuditChain.FormatTimestamp(cutoff);
        using var transaction = connection.BeginTransaction();

        long removed = 0;
        using (var last = Command(connection, transaction,
            "SELECT sequence, hash FROM audit_events WHERE occurred_utc < $cutoff ORDER BY sequence DESC LIMIT 1",
            ("$cutoff", cutoffText)))
        using (var reader = last.ExecuteReader())
        {
            if (reader.Read())
            {
                var anchorSequence = reader.GetInt64(0);
                var anchorHash = reader.GetString(1);
                reader.Close();

                Execute(connection, transaction,
                    "INSERT INTO audit_anchor (id, sequence, hash, created_utc) VALUES (1, $sequence, $hash, $now) " +
                    "ON CONFLICT (id) DO UPDATE SET sequence = excluded.sequence, hash = excluded.hash, created_utc = excluded.created_utc",
                    ("$sequence", anchorSequence), ("$hash", anchorHash), ("$now", AuditChain.FormatTimestamp(timeProvider.GetUtcNow())));
                removed = Execute(connection, transaction, "DELETE FROM audit_events WHERE sequence <= $sequence", ("$sequence", anchorSequence));
            }
        }

        Execute(connection, transaction,
            "DELETE FROM connections WHERE disconnected_utc IS NOT NULL AND disconnected_utc < $cutoff", ("$cutoff", cutoffText));
        Execute(connection, transaction,
            "DELETE FROM devices WHERE last_seen_utc < $cutoff AND NOT EXISTS (SELECT 1 FROM connections c WHERE c.fingerprint_key = devices.fingerprint_key)",
            ("$cutoff", cutoffText));

        transaction.Commit();
        return removed;
    }

    public IReadOnlyList<ConnectionHistoryEntry> GetRecentConnections(int count)
    {
        using var connection = Open();
        using var command = Command(connection, null,
            "SELECT c.connection_id, c.fingerprint_key, d.confidence, d.model, c.storage_instance_id, c.connected_utc, c.disconnected_utc, c.end_reason " +
            "FROM connections c JOIN devices d ON d.fingerprint_key = c.fingerprint_key ORDER BY c.connected_utc DESC LIMIT $count",
            ("$count", count));
        using var reader = command.ExecuteReader();

        var entries = new List<ConnectionHistoryEntry>();
        while (reader.Read())
        {
            entries.Add(new ConnectionHistoryEntry(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                Enum.Parse<IdentityConfidence>(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                ParseTimestamp(reader.GetString(5)),
                reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6)),
                reader.IsDBNull(7) ? null : Enum.Parse<ConnectionEndReason>(reader.GetString(7))));
        }

        return entries;
    }

    /// <summary>Consistent online copy of the database (SQLite backup API), e.g. before an upgrade.</summary>
    public void Backup(string destinationPath)
    {
        using var source = Open();
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationPath, Pooling = false }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            // WAL: readers do not block the writer. FULL: audit entries survive power loss once committed.
            Execute(connection, null, "PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA busy_timeout = 5000;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static (long Sequence, string Hash) ReadHead(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction, "SELECT sequence, hash FROM audit_events ORDER BY sequence DESC LIMIT 1");
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return (reader.GetInt64(0), reader.GetString(1));
        }

        reader.Close();
        return ReadAnchor(connection, transaction) is { } anchor ? (anchor.Sequence, anchor.Hash) : (0, AuditChain.GenesisHash);
    }

    private static AuditAnchor? ReadAnchor(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, "SELECT sequence, hash FROM audit_anchor WHERE id = 1");
        using var reader = command.ExecuteReader();
        return reader.Read() ? new AuditAnchor(reader.GetInt64(0), reader.GetString(1)) : null;
    }

    private static IEnumerable<AuditEntry> ReadAuditEntries(SqliteConnection connection)
    {
        using var command = Command(connection, null,
            "SELECT sequence, occurred_utc, event_type, connection_id, device_key, details, previous_hash, hash FROM audit_events ORDER BY sequence");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // Unparseable stored values map to defaults; the recomputed hash then differs, so tampering is reported, not thrown.
            _ = DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var occurredAt);
            _ = Enum.TryParse<AuditEventType>(reader.GetString(2), out var type);
            Guid? connectionId = !reader.IsDBNull(3) && Guid.TryParse(reader.GetString(3), out var id) ? id : null;

            yield return new AuditEntry(
                reader.GetInt64(0),
                occurredAt,
                type,
                connectionId,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7));
        }
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    /// <summary>Writes one batch inside an open transaction, tracking the audit-chain head.</summary>
    private sealed class BatchWriter(SqliteConnection connection, SqliteTransaction transaction, (long Sequence, string Hash) head)
    {
        private (long Sequence, string Hash) _head = head;

        public void AppendAudit(AuditRecord record)
        {
            var entry = AuditChain.CreateEntry(_head.Sequence + 1, record, _head.Hash);
            Execute(connection, transaction,
                "INSERT INTO audit_events (sequence, occurred_utc, event_type, connection_id, device_key, details, previous_hash, hash) " +
                "VALUES ($sequence, $occurred, $type, $connection, $device, $details, $previous, $hash)",
                ("$sequence", entry.Sequence),
                ("$occurred", AuditChain.FormatTimestamp(entry.OccurredAt)),
                ("$type", entry.Type.ToString()),
                ("$connection", entry.ConnectionId?.ToString("D")),
                ("$device", entry.DeviceKey),
                ("$details", entry.Details),
                ("$previous", entry.PreviousHash),
                ("$hash", entry.Hash));
            _head = (entry.Sequence, entry.Hash);
        }

        public void WriteLifecycle(DeviceLifecycleEvent lifecycleEvent)
        {
            switch (lifecycleEvent)
            {
                case DeviceConnected connected:
                    WriteConnected(connected);
                    break;

                case DeviceDisconnected disconnected:
                    var reason = disconnected.Reason == DisconnectReason.MediaChanged
                        ? ConnectionEndReason.MediaChanged
                        : ConnectionEndReason.Removed;
                    EndConnection(disconnected.ConnectionId, disconnected.OccurredAt, reason);
                    Execute(connection, transaction,
                        "UPDATE devices SET last_seen_utc = $at WHERE fingerprint_key = $key",
                        ("$at", AuditChain.FormatTimestamp(disconnected.OccurredAt)), ("$key", disconnected.LastKnown.Fingerprint.Key));
                    break;

                case VolumeMounted mounted:
                    WriteVolumeEvent(mounted.ConnectionId, mounted.OccurredAt, "Mounted", mounted.Volume);
                    break;

                case VolumeDismounted dismounted:
                    WriteVolumeEvent(dismounted.ConnectionId, dismounted.OccurredAt, "Dismounted", dismounted.Volume);
                    break;

                case VolumeUpdated updated:
                    WriteVolumeEvent(updated.ConnectionId, updated.OccurredAt, "Updated", updated.Current);
                    break;
            }
        }

        public int CloseOpenConnections(DateTimeOffset at, ConnectionEndReason reason) =>
            Execute(connection, transaction,
                "UPDATE connections SET disconnected_utc = $at, end_reason = $reason WHERE disconnected_utc IS NULL",
                ("$at", AuditChain.FormatTimestamp(at)), ("$reason", reason.ToString()));

        private void WriteConnected(DeviceConnected connected)
        {
            var device = connected.Disk.Device;
            var fingerprint = connected.Disk.Fingerprint;
            var at = AuditChain.FormatTimestamp(connected.OccurredAt);

            Execute(connection, transaction,
                "INSERT INTO devices (fingerprint_key, confidence, vendor_id, product_id, hardware_serial, vendor, product, revision, model, bus_type, size_bytes, first_seen_utc, last_seen_utc, connection_count) " +
                "VALUES ($key, $confidence, $vid, $pid, $serial, $vendor, $product, $revision, $model, $bus, $size, $at, $at, 1) " +
                "ON CONFLICT (fingerprint_key) DO UPDATE SET last_seen_utc = excluded.last_seen_utc, connection_count = connection_count + 1, " +
                "model = excluded.model, size_bytes = excluded.size_bytes",
                ("$key", fingerprint.Key),
                ("$confidence", fingerprint.Confidence.ToString()),
                ("$vid", (int?)fingerprint.VendorId),
                ("$pid", (int?)fingerprint.ProductId),
                ("$serial", fingerprint.HardwareSerial),
                ("$vendor", fingerprint.Descriptor?.Vendor),
                ("$product", fingerprint.Descriptor?.Product),
                ("$revision", fingerprint.Descriptor?.Revision),
                ("$model", device.Model ?? device.FriendlyName),
                ("$bus", device.BusType.ToString()),
                ("$size", (long)device.SizeBytes),
                ("$at", at));

            Execute(connection, transaction,
                "INSERT INTO connections (connection_id, fingerprint_key, storage_instance_id, usb_parent_instance_id, container_id, disk_number, connected_utc) " +
                "VALUES ($id, $key, $instance, $parent, $container, $disk, $at)",
                ("$id", connected.ConnectionId.ToString("D")),
                ("$key", fingerprint.Key),
                ("$instance", device.StorageInstanceId.Value),
                ("$parent", device.UsbParentInstanceId?.Value),
                ("$container", device.ContainerId?.ToString("D")),
                ("$disk", device.DiskNumber),
                ("$at", at));

            foreach (var volume in connected.Disk.Volumes)
            {
                WriteVolumeEvent(connected.ConnectionId, connected.OccurredAt, "Mounted", volume);
            }
        }

        private void EndConnection(Guid connectionId, DateTimeOffset at, ConnectionEndReason reason) =>
            Execute(connection, transaction,
                "UPDATE connections SET disconnected_utc = $at, end_reason = $reason WHERE connection_id = $id AND disconnected_utc IS NULL",
                ("$at", AuditChain.FormatTimestamp(at)), ("$reason", reason.ToString()), ("$id", connectionId.ToString("D")));

        // INSERT … SELECT … WHERE EXISTS: an event for an unknown connection (e.g. its connect was dropped while the
        // database was unavailable) is skipped instead of failing the whole batch on the foreign key.
        private void WriteVolumeEvent(Guid connectionId, DateTimeOffset at, string kind, VolumeInfo volume) =>
            Execute(connection, transaction,
                "INSERT INTO volume_events (connection_id, occurred_utc, kind, volume_guid_path, drive_letter, label, file_system, volume_serial, capacity_bytes) " +
                "SELECT $id, $at, $kind, $path, $letter, $label, $fs, $serial, $capacity WHERE EXISTS (SELECT 1 FROM connections WHERE connection_id = $id)",
                ("$id", connectionId.ToString("D")),
                ("$at", AuditChain.FormatTimestamp(at)),
                ("$kind", kind),
                ("$path", volume.VolumeGuidPath),
                ("$letter", volume.DriveLetter?.ToString()),
                ("$label", volume.Label),
                ("$fs", volume.FileSystem),
                ("$serial", (long?)volume.SerialNumber),
                ("$capacity", (long?)volume.CapacityBytes));
    }
}
