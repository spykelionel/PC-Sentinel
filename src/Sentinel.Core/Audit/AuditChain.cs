using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sentinel.Core.Audit;

public enum AuditEventType
{
    ServiceStarted = 1,
    ServiceStopped = 2,
    ConnectionsClosed = 3,
    DeviceConnected = 10,
    DeviceDisconnected = 11,
    VolumeMounted = 12,
    VolumeDismounted = 13,
    VolumeUpdated = 14,
    AuditEventsDropped = 90,
    AuditChainInvalid = 91,
    AuditPurged = 92,
}

/// <summary>An audit record before it is placed in the chain.</summary>
/// <param name="Details">JSON object with event-specific data. Must never contain secrets.</param>
public sealed record AuditRecord(
    DateTimeOffset OccurredAt,
    AuditEventType Type,
    Guid? ConnectionId,
    string? DeviceKey,
    string Details);

/// <summary>A stored audit record with its position and hashes in the chain.</summary>
public sealed record AuditEntry(
    long Sequence,
    DateTimeOffset OccurredAt,
    AuditEventType Type,
    Guid? ConnectionId,
    string? DeviceKey,
    string Details,
    string PreviousHash,
    string Hash);

/// <summary>The last entry removed by a retention purge. Verification continues from it.</summary>
public sealed record AuditAnchor(long Sequence, string Hash);

public sealed record AuditVerificationResult(bool IsValid, long EntriesChecked, long? FailedAtSequence, string? Failure)
{
    public static AuditVerificationResult Valid(long entriesChecked) => new(true, entriesChecked, null, null);

    public static AuditVerificationResult Invalid(long entriesChecked, long sequence, string failure) =>
        new(false, entriesChecked, sequence, failure);
}

/// <summary>
/// SHA-256 hash chain over audit entries (ADR-014). Each entry's hash covers its own fields and the previous
/// entry's hash, so modifying, deleting, inserting, or reordering entries is detectable. This is tamper-evident,
/// not tamper-proof: someone with write access can recompute the whole chain. Removing entries from the end
/// of the chain is only detectable against an external copy of the head.
/// </summary>
public static class AuditChain
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private const string FormatVersion = "v1";

    /// <summary>The canonical timestamp text: UTC, round-trip format. It is stored exactly like this.</summary>
    public static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static string ComputeHash(long sequence, AuditRecord record, string previousHash)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(previousHash);

        // Length-prefixed fields: no separator or field value can make two different entries serialize identically.
        var canonical = new StringBuilder();
        Append(canonical, FormatVersion);
        Append(canonical, sequence.ToString(CultureInfo.InvariantCulture));
        Append(canonical, FormatTimestamp(record.OccurredAt));
        Append(canonical, record.Type.ToString());
        Append(canonical, record.ConnectionId?.ToString("D") ?? string.Empty);
        Append(canonical, record.DeviceKey ?? string.Empty);
        Append(canonical, record.Details);
        Append(canonical, previousHash);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    public static AuditEntry CreateEntry(long sequence, AuditRecord record, string previousHash)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new AuditEntry(
            sequence,
            record.OccurredAt,
            record.Type,
            record.ConnectionId,
            record.DeviceKey,
            record.Details,
            previousHash,
            ComputeHash(sequence, record, previousHash));
    }

    /// <summary>Verifies entries in stored order, starting from the purge anchor if there is one.</summary>
    public static AuditVerificationResult Verify(IEnumerable<AuditEntry> entries, AuditAnchor? anchor)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var expectedSequence = (anchor?.Sequence ?? 0) + 1;
        var expectedPreviousHash = anchor?.Hash ?? GenesisHash;
        long checkedCount = 0;

        foreach (var entry in entries)
        {
            if (entry.Sequence != expectedSequence)
            {
                return AuditVerificationResult.Invalid(checkedCount, entry.Sequence,
                    $"Expected sequence {expectedSequence}, found {entry.Sequence} (entries missing, inserted, or reordered).");
            }

            if (!string.Equals(entry.PreviousHash, expectedPreviousHash, StringComparison.Ordinal))
            {
                return AuditVerificationResult.Invalid(checkedCount, entry.Sequence,
                    "Previous-hash link does not match the preceding entry.");
            }

            var record = new AuditRecord(entry.OccurredAt, entry.Type, entry.ConnectionId, entry.DeviceKey, entry.Details);
            var recomputed = ComputeHash(entry.Sequence, record, entry.PreviousHash);
            if (!string.Equals(recomputed, entry.Hash, StringComparison.Ordinal))
            {
                return AuditVerificationResult.Invalid(checkedCount, entry.Sequence,
                    "Entry content does not match its hash (entry modified).");
            }

            checkedCount++;
            expectedSequence++;
            expectedPreviousHash = entry.Hash;
        }

        return AuditVerificationResult.Valid(checkedCount);
    }

    private static void Append(StringBuilder builder, string field) =>
        builder.Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field).Append('\n');
}
