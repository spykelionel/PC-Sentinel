using Sentinel.Core.Devices;
using Sentinel.Core.History;
using Sentinel.Service.Persistence;

namespace Sentinel.Service.DeviceMonitor;

/// <summary>
/// Single orchestration loop for device lifecycle. Performs a full storage scan at startup,
/// after (debounced) device events, on a retry schedule while enumeration is incomplete, and
/// periodically as a safety net for missed events. Each scan is reconciled into the inventory.
/// </summary>
public sealed class DeviceInventoryService(
    IStorageScanner scanner,
    ReconcileTrigger trigger,
    HistoryWriter history,
    TimeProvider timeProvider,
    ILogger<DeviceInventoryService> logger) : BackgroundService
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PeriodicInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15),
    ];

    private readonly DeviceInventory _inventory = new();

    /// <summary>Index into <see cref="RetryDelays"/>, or -1 when no retry is pending.</summary>
    private int _retryAttempt = -1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        history.Start();
        logger.LogInformation("Device inventory started. Running initial storage scan.");
        await ReconcileAsync(ScanReason.Startup, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var retryPending = _retryAttempt >= 0;
            var wait = retryPending ? RetryDelays[_retryAttempt] : PeriodicInterval;

            bool triggered;
            try
            {
                triggered = await trigger.WaitAsync(wait, stoppingToken);
                if (triggered)
                {
                    await Task.Delay(DebounceDelay, timeProvider, stoppingToken);
                    trigger.Drain();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var reason = triggered ? ScanReason.DeviceChange : retryPending ? ScanReason.Retry : ScanReason.Periodic;
            await ReconcileAsync(reason, stoppingToken);
            history.PurgeIfDue();
        }

        history.Stop();
        logger.LogInformation("Device inventory stopped.");
    }

    private async Task ReconcileAsync(ScanReason reason, CancellationToken cancellationToken)
    {
        StorageScanResult result;
        try
        {
            result = await Task.Run(() => scanner.Scan(cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            // A failed scan must never stop monitoring; it is retried with backoff.
            logger.LogError(exception, "Storage scan failed ({Reason}).", reason);
            UpdateRetryState(reason, ["scan failed"]);
            history.Flush();
            return;
        }

        var events = _inventory.Reconcile(result.ExternalDisks, timeProvider.GetUtcNow());
        foreach (var lifecycleEvent in events)
        {
            LogLifecycleEvent(lifecycleEvent);
        }

        // Always called, even with no events: an empty flush retries anything buffered while storage was unavailable.
        history.Record(events.Select(e => new DeviceLifecycleChange(e)));

        UpdateRetryState(reason, result.UnresolvedItems);
    }

    private void UpdateRetryState(ScanReason reason, IReadOnlyList<string> unresolved)
    {
        if (unresolved.Count == 0)
        {
            _retryAttempt = -1;
            return;
        }

        _retryAttempt = reason == ScanReason.Retry ? _retryAttempt + 1 : 0;
        if (_retryAttempt >= RetryDelays.Length)
        {
            logger.LogWarning(
                "Storage enumeration still incomplete after {Attempts} retries: {Unresolved}",
                RetryDelays.Length,
                string.Join("; ", unresolved));
            _retryAttempt = -1;
            return;
        }

        logger.LogDebug(
            "Storage enumeration incomplete, retrying in {Delay}: {Unresolved}",
            RetryDelays[_retryAttempt],
            string.Join("; ", unresolved));
    }

    private void LogLifecycleEvent(DeviceLifecycleEvent lifecycleEvent)
    {
        switch (lifecycleEvent)
        {
            case DeviceConnected connected:
                var device = connected.Disk.Device;
                var fingerprint = connected.Disk.Fingerprint;
                logger.LogInformation(
                    "Device connected. Connection={ConnectionId} Disk={DiskNumber} Bus={BusType} Model={Model} " +
                    "Instance={InstanceId} UsbParent={UsbParent} Container={ContainerId} Confidence={Confidence} " +
                    "HardwareSerial={HardwareSerial} SizeBytes={SizeBytes} Fingerprint={FingerprintKey} Volumes={Volumes}",
                    connected.ConnectionId,
                    device.DiskNumber,
                    device.BusType,
                    device.Model ?? device.FriendlyName,
                    device.StorageInstanceId,
                    device.UsbParentInstanceId?.Value ?? "(none)",
                    device.ContainerId,
                    fingerprint.Confidence,
                    fingerprint.HardwareSerial ?? "(none)",
                    device.SizeBytes,
                    fingerprint.Key,
                    DeviceFormatting.DescribeVolumes(connected.Disk.Volumes));
                break;

            case DeviceDisconnected disconnected:
                logger.LogInformation(
                    "Device disconnected. Connection={ConnectionId} Reason={Reason} Model={Model} Instance={InstanceId}",
                    disconnected.ConnectionId,
                    disconnected.Reason,
                    disconnected.LastKnown.Device.Model ?? disconnected.LastKnown.Device.FriendlyName,
                    disconnected.LastKnown.Device.StorageInstanceId);
                break;

            case VolumeMounted mounted:
                logger.LogInformation(
                    "Volume mounted. Connection={ConnectionId} Volume={Volume}",
                    mounted.ConnectionId,
                    DeviceFormatting.DescribeVolume(mounted.Volume));
                break;

            case VolumeDismounted dismounted:
                logger.LogInformation(
                    "Volume dismounted. Connection={ConnectionId} Volume={Volume}",
                    dismounted.ConnectionId,
                    DeviceFormatting.DescribeVolume(dismounted.Volume));
                break;

            case VolumeUpdated updated:
                logger.LogInformation(
                    "Volume changed. Connection={ConnectionId} From={Previous} To={Current}",
                    updated.ConnectionId,
                    DeviceFormatting.DescribeVolume(updated.Previous),
                    DeviceFormatting.DescribeVolume(updated.Current));
                break;
        }
    }

    private enum ScanReason
    {
        Startup,
        DeviceChange,
        Retry,
        Periodic,
    }
}
