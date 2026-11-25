using Microsoft.Data.Sqlite;
using Sentinel.Core.Audit;
using Sentinel.Core.Devices;
using Sentinel.Core.History;
using Sentinel.Infrastructure.Persistence;

namespace Sentinel.Infrastructure.Tests.Persistence;

/// <summary>Integration tests on real SQLite databases, one temporary directory per test.</summary>
public sealed class SqliteHistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PCSentinel.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private readonly ManualTimeProvider _time = new(T0);

    public SqliteHistoryStoreTests()
    {
        _databasePath = Path.Combine(_directory, "sentinel.db");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private SqliteHistoryStore NewStore()
    {
        var store = new SqliteHistoryStore(_databasePath, _time);
        store.Initialize();
        return store;
    }

    [Fact]
    public void Initialize_FreshDatabase_CreatesLatestSchema_WithoutBackup()
    {
        var store = NewStore();

        Assert.Equal(new MigrationResult(0, DatabaseMigrator.LatestVersion, null), store.LastMigration);
        Assert.True(File.Exists(_databasePath));
    }

    [Fact]
    public void Initialize_Twice_IsNoOp()
    {
        NewStore();

        var second = NewStore();

        Assert.Equal(new MigrationResult(DatabaseMigrator.LatestVersion, DatabaseMigrator.LatestVersion, null), second.LastMigration);
    }

    [Fact]
    public void Initialize_NewerSchemaThanBuild_Refuses()
    {
        NewStore();
        Exec("INSERT INTO schema_version (version, applied_utc) VALUES (999, 'x')");

        Assert.Throws<InvalidOperationException>(() => NewStore());
    }

    [Fact]
    public void Initialize_UnwritableLocation_Throws()
    {
        Directory.CreateDirectory(_directory);
        var fileInTheWay = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(fileInTheWay, "x");

        var store = new SqliteHistoryStore(Path.Combine(fileInTheWay, "sentinel.db"), _time);

        Assert.ThrowsAny<IOException>(store.Initialize);
    }

    [Fact]
    public void ConnectAndDisconnect_ArePersisted_AndSurviveReopen()
    {
        var connected = Connected();
        NewStore().Apply([
            new DeviceLifecycleChange(connected),
            new DeviceLifecycleChange(new DeviceDisconnected(connected.ConnectionId, T0.AddMinutes(5), connected.Disk, DisconnectReason.Removed)),
        ]);

        var reopened = NewStore();
        var entry = Assert.Single(reopened.GetRecentConnections(10));

        Assert.Equal(connected.ConnectionId, entry.ConnectionId);
        Assert.Equal(connected.Disk.Fingerprint.Key, entry.FingerprintKey);
        Assert.Equal(IdentityConfidence.Weak, entry.Confidence);
        Assert.Equal(T0, entry.ConnectedAt);
        Assert.Equal(T0.AddMinutes(5), entry.DisconnectedAt);
        Assert.Equal(ConnectionEndReason.Removed, entry.EndReason);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM volume_events WHERE kind = 'Mounted'"));
    }

    [Fact]
    public void Reconnect_SameDevice_IsOneDeviceWithTwoConnections()
    {
        var store = NewStore();
        var first = Connected();
        var second = Connected(Guid.NewGuid(), T0.AddMinutes(10));
        store.Apply([
            new DeviceLifecycleChange(first),
            new DeviceLifecycleChange(new DeviceDisconnected(first.ConnectionId, T0.AddMinutes(1), first.Disk, DisconnectReason.Removed)),
            new DeviceLifecycleChange(second),
        ]);

        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM devices"));
        Assert.Equal(2, Scalar("SELECT connection_count FROM devices"));
        Assert.Equal(2, store.GetRecentConnections(10).Count);
    }

    [Fact]
    public void CloseOpenConnections_ClosesOnlyOpenOnes_AndAuditsCount()
    {
        var store = NewStore();
        var closed = Connected();
        var open = Connected(Guid.NewGuid(), T0.AddMinutes(2));
        store.Apply([
            new DeviceLifecycleChange(closed),
            new DeviceLifecycleChange(new DeviceDisconnected(closed.ConnectionId, T0.AddMinutes(1), closed.Disk, DisconnectReason.Removed)),
            new DeviceLifecycleChange(open),
        ]);

        store.Apply([new CloseOpenConnectionsChange(T0.AddMinutes(3), ConnectionEndReason.ServiceRestart)]);

        var byId = store.GetRecentConnections(10).ToDictionary(c => c.ConnectionId);
        Assert.Equal(ConnectionEndReason.Removed, byId[closed.ConnectionId].EndReason);
        Assert.Equal(ConnectionEndReason.ServiceRestart, byId[open.ConnectionId].EndReason);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM audit_events WHERE event_type = 'ConnectionsClosed' AND details LIKE '%\"count\":\"1\"%'"));
    }

    [Fact]
    public void CloseOpenConnections_WithNothingOpen_WritesNoAudit()
    {
        var store = NewStore();

        store.Apply([new CloseOpenConnectionsChange(T0, ConnectionEndReason.ServiceRestart)]);

        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM audit_events"));
    }

    [Fact]
    public void VolumeEvent_ForUnknownConnection_IsSkipped_NotFatal()
    {
        var store = NewStore();

        store.Apply([new DeviceLifecycleChange(new VolumeMounted(Guid.NewGuid(), T0, Volume()))]);

        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM volume_events"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM audit_events"));
    }

    [Fact]
    public void Apply_IsAtomic_FailedBatchLeavesNothing()
    {
        var store = NewStore();
        var connected = Connected();

        // The same connection ID twice violates the primary key, so the whole batch must roll back.
        Assert.Throws<SqliteException>(() => store.Apply([new DeviceLifecycleChange(connected), new DeviceLifecycleChange(connected)]));

        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM connections"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM audit_events"));
    }

    [Fact]
    public void DeviceStrings_AreStoredLiterally()
    {
        var store = NewStore();
        var hostile = "'; DROP TABLE devices; --";
        var connected = Connected(volume: Volume(label: hostile));

        store.Apply([new DeviceLifecycleChange(connected)]);

        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM devices"));
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM volume_events WHERE label = '{hostile.Replace("'", "''", StringComparison.Ordinal)}'"));
    }

    [Fact]
    public void AuditChain_AcrossBatchesAndReopen_IsValid()
    {
        NewStore().Apply([new DeviceLifecycleChange(Connected())]);
        NewStore().Apply([Audit(AuditEventType.ServiceStopped), Audit(AuditEventType.ServiceStarted)]);

        var result = NewStore().VerifyAuditChain();

        Assert.True(result.IsValid, result.Failure);
        Assert.Equal(3, result.EntriesChecked);
    }

    [Fact]
    public void AuditChain_ModifiedRow_IsDetected()
    {
        var store = NewStore();
        store.Apply([Audit(AuditEventType.ServiceStarted), new DeviceLifecycleChange(Connected()), Audit(AuditEventType.ServiceStopped)]);

        Exec("UPDATE audit_events SET details = '{\"reason\":\"forged\"}' WHERE sequence = 2");

        var result = store.VerifyAuditChain();
        Assert.False(result.IsValid);
        Assert.Equal(2, result.FailedAtSequence);
    }

    [Fact]
    public void AuditChain_DeletedRow_IsDetected()
    {
        var store = NewStore();
        store.Apply([Audit(AuditEventType.ServiceStarted), Audit(AuditEventType.ServiceStopped), Audit(AuditEventType.ServiceStarted)]);

        Exec("DELETE FROM audit_events WHERE sequence = 2");

        Assert.Equal(3, store.VerifyAuditChain().FailedAtSequence);
    }

    [Fact]
    public void AuditChain_UnparseableStoredValues_AreReportedNotThrown()
    {
        var store = NewStore();
        store.Apply([Audit(AuditEventType.ServiceStarted)]);

        Exec("UPDATE audit_events SET event_type = 'NotAType', occurred_utc = 'garbage', connection_id = 'zzz' WHERE sequence = 1");

        Assert.False(store.VerifyAuditChain().IsValid);
    }

    [Fact]
    public void Purge_RemovesOldHistory_KeepsChainVerifiable_AndContinuesChain()
    {
        var store = NewStore();
        var old = Connected();
        store.Apply([
            new DeviceLifecycleChange(old),
            new DeviceLifecycleChange(new DeviceDisconnected(old.ConnectionId, T0.AddMinutes(1), old.Disk, DisconnectReason.Removed)),
        ]);
        var recent = Connected(Guid.NewGuid(), T0.AddDays(30));
        store.Apply([new DeviceLifecycleChange(recent)]);

        var removed = store.Purge(T0.AddDays(10));

        Assert.Equal(2, removed);
        Assert.Equal(recent.ConnectionId, Assert.Single(store.GetRecentConnections(10)).ConnectionId);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM audit_events"));
        Assert.True(store.VerifyAuditChain().IsValid);

        store.Apply([Audit(AuditEventType.ServiceStopped)]);
        var afterAppend = store.VerifyAuditChain();
        Assert.True(afterAppend.IsValid, afterAppend.Failure);
        Assert.Equal(2, afterAppend.EntriesChecked);
    }

    [Fact]
    public void Purge_AllEntries_NextAppendContinuesFromAnchor()
    {
        var store = NewStore();
        store.Apply([Audit(AuditEventType.ServiceStarted), Audit(AuditEventType.ServiceStopped)]);

        store.Purge(T0.AddDays(1));
        store.Apply([Audit(AuditEventType.ServiceStarted)]);

        Assert.Equal(3, Scalar("SELECT sequence FROM audit_events"));
        Assert.True(store.VerifyAuditChain().IsValid);
    }

    [Fact]
    public void Purge_KeepsOpenConnections()
    {
        var store = NewStore();
        var open = Connected();
        store.Apply([new DeviceLifecycleChange(open)]);

        store.Purge(T0.AddDays(400));

        Assert.Equal(open.ConnectionId, Assert.Single(store.GetRecentConnections(10)).ConnectionId);
    }

    [Fact]
    public void Purge_RefusedWhenChainIsBroken()
    {
        var store = NewStore();
        store.Apply([Audit(AuditEventType.ServiceStarted), Audit(AuditEventType.ServiceStopped)]);
        Exec("UPDATE audit_events SET details = '{}x' WHERE sequence = 1");

        Assert.Throws<InvalidOperationException>(() => store.Purge(T0.AddDays(1)));
        Assert.Equal(2, Scalar("SELECT COUNT(*) FROM audit_events"));
    }

    [Fact]
    public void Backup_ProducesReadableCopy()
    {
        var store = NewStore();
        var connected = Connected();
        store.Apply([new DeviceLifecycleChange(connected)]);
        var backupPath = Path.Combine(_directory, "backup.db");

        store.Backup(backupPath);

        var copy = new SqliteHistoryStore(backupPath, _time);
        copy.Initialize();
        Assert.Equal(connected.ConnectionId, Assert.Single(copy.GetRecentConnections(10)).ConnectionId);
        Assert.True(copy.VerifyAuditChain().IsValid);
    }

    private static AuditOnlyChange Audit(AuditEventType type) => new(new AuditRecord(T0, type, null, null, "{}"));

    private static VolumeInfo Volume(string? label = "Afanyu Lionel") =>
        new(@"\\?\Volume{98f0be27-c8ac-11f1-8713-7c7635f75b38}\", 'D', label, "NTFS", 0xD4F0A755, 1_993_076_736);

    private static DeviceConnected Connected(Guid? id = null, DateTimeOffset? at = null, VolumeInfo? volume = null)
    {
        var device = new PhysicalDeviceInfo(
            Id(@"USBSTOR\DISK&VEN_SMI&PROD_USB_DISK&REV_1100\6&172B058&1"),
            Id(@"USB\VID_090C&PID_1000\5&27fb95dd&0&1"),
Guid.Parse("98f0be20-c8ac-11f1-8713-7c7635f75b38"), 1, StorageBusType.Usb,
            "SMI USB DISK USB Device", "SMI USB DISK USB Device", null, 1_993_342_976, "mbr:C3072E18");
        var disk = new ExternalDisk(device, DeviceFingerprint.Create(device), [volume ?? Volume()]);
        return new DeviceConnected(id ?? Guid.Parse("3294c600-b16f-430a-bf0c-cccdd7e30b39"), at ?? T0, disk);
    }

    private static PnpInstanceId Id(string value) =>
        PnpInstanceId.TryParse(value, out var id) ? id : throw new ArgumentException($"Invalid instance ID: {value}");

    private SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private void Exec(string sql)
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
