using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Sentinel.Infrastructure.Persistence;

public sealed record MigrationResult(int FromVersion, int ToVersion, string? BackupPath);

/// <summary>
/// Forward-only schema migrations from embedded <c>Migrations/NNNN_name.sql</c> scripts. Each migration runs in
/// its own transaction together with its <c>schema_version</c> row. An existing database is backed up first.
/// </summary>
public static class DatabaseMigrator
{
    private const string ResourcePrefix = "Sentinel.Infrastructure.Persistence.Migrations.";

    public static int LatestVersion => Migrations.Value[^1].Version;

    private static readonly Lazy<IReadOnlyList<(int Version, string Sql)>> Migrations = new(LoadMigrations);

    public static MigrationResult Migrate(SqliteConnection connection, string databasePath, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(timeProvider);

        Execute(connection, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL PRIMARY KEY, applied_utc TEXT NOT NULL) STRICT;");

        var current = GetCurrentVersion(connection);
        if (current > LatestVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {current} is newer than this build supports ({LatestVersion}). Downgrade is not supported.");
        }

        var pending = Migrations.Value.Where(m => m.Version > current).ToList();
        if (pending.Count == 0)
        {
            return new MigrationResult(current, current, null);
        }

        string? backupPath = null;
        if (current > 0)
        {
            var stamp = timeProvider.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            backupPath = $"{databasePath}.v{current}.{stamp}.bak";
            using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString());
            target.Open();
            connection.BackupDatabase(target);
        }

        foreach (var (version, sql) in pending)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, sql, transaction);
            using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO schema_version (version, applied_utc) VALUES ($version, $applied)";
            record.Parameters.AddWithValue("$version", version);
            record.Parameters.AddWithValue("$applied", timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            record.ExecuteNonQuery();
            transaction.Commit();
        }

        return new MigrationResult(current, pending[^1].Version, backupPath);
    }

    public static int GetCurrentVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<(int Version, string Sql)> LoadMigrations()
    {
        var assembly = typeof(DatabaseMigrator).Assembly;
        var migrations = new List<(int Version, string Sql)>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            var fileName = name[ResourcePrefix.Length..];
            var version = int.Parse(fileName.AsSpan(0, fileName.IndexOf('_', StringComparison.Ordinal)), CultureInfo.InvariantCulture);
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Migration resource {name} not found.");
            using var reader = new StreamReader(stream);
            migrations.Add((version, reader.ReadToEnd()));
        }

        migrations.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
            {
                throw new InvalidOperationException($"Migrations must be numbered 1..n without gaps; found {migrations[i].Version} at position {i + 1}.");
            }
        }

        return migrations.Count == 0 ? throw new InvalidOperationException("No migrations embedded.") : migrations;
    }
}
