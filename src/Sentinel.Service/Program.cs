
using Microsoft.Extensions.Hosting.WindowsServices;
using Sentinel.Core.Devices;
using Sentinel.Core.History;
using Sentinel.Infrastructure.Devices;
using Sentinel.Infrastructure.Persistence;
using Sentinel.Service.DeviceMonitor;
using Sentinel.Service.Persistence;

// Development diagnostics. Read-only; they use the development database location.
if (args.Contains("--list-devices", StringComparer.OrdinalIgnoreCase))
{
    var result = new WmiStorageScanner().Scan(CancellationToken.None);
    Console.WriteLine(result.ExternalDisks.Count == 0
        ? "No external storage devices found."
        : string.Join(Environment.NewLine + Environment.NewLine, result.ExternalDisks.Select(DeviceFormatting.DescribeDisk)));
    foreach (var item in result.UnresolvedItems)
    {
        Console.WriteLine($"Unresolved: {item}");
    }

    return 0;
}

if (args.Contains("--history", StringComparer.OrdinalIgnoreCase) || args.Contains("--verify-audit", StringComparer.OrdinalIgnoreCase))
{
    var databasePath = new PersistenceOptions().ResolveDatabasePath(runningAsService: false);
    if (!File.Exists(databasePath))
    {
        Console.WriteLine($"No database at {databasePath}.");
        return 1;
    }

    var store = new SqliteHistoryStore(databasePath, TimeProvider.System);
    Console.WriteLine($"Database: {databasePath}");

    if (args.Contains("--verify-audit", StringComparer.OrdinalIgnoreCase))
    {
        var verification = store.VerifyAuditChain();
        Console.WriteLine(verification.IsValid
            ? $"Audit chain VALID: {verification.EntriesChecked} entries verified."
            : $"Audit chain INVALID at sequence {verification.FailedAtSequence}: {verification.Failure} ({verification.EntriesChecked} entries verified before it).");
        return verification.IsValid ? 0 : 2;
    }

    foreach (var entry in store.GetRecentConnections(20))
    {
        Console.WriteLine(
            $"{entry.ConnectedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {entry.DisconnectedAt?.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture) ?? "(open)  "}  " +
            $"{entry.EndReason?.ToString() ?? "-",-14} {entry.Confidence,-6} {entry.Model}  {entry.ConnectionId}");
    }

    return 0;
}

var builder = Host.CreateApplicationBuilder(args);

// No effect when run as a console app; enables SCM integration when hosted as a service (not installed yet).
builder.Services.AddWindowsService(options => options.ServiceName = "PCSentinel");

var persistence = builder.Configuration.GetSection(PersistenceOptions.SectionName).Get<PersistenceOptions>() ?? new PersistenceOptions();
var databaseFile = persistence.ResolveDatabasePath(WindowsServiceHelpers.IsWindowsService());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ReconcileTrigger>();
builder.Services.AddSingleton<IStorageScanner, WmiStorageScanner>();
builder.Services.AddSingleton<IHistoryStore>(sp => new SqliteHistoryStore(databaseFile, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new HistoryWriter(
    sp.GetRequiredService<IHistoryStore>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<HistoryWriter>>(),
    persistence.RetentionDays));

builder.Services.AddHostedService<UsbVolumeMonitor>();
builder.Services.AddHostedService<DeviceArrivalMonitor>();
builder.Services.AddHostedService<DeviceInventoryService>();

var host = builder.Build();

host.Services.GetRequiredService<ILogger<HistoryWriter>>().LogInformation("History database: {Path}", databaseFile);

host.Run();

return 0;
