using System.Globalization;
using System.Management;

namespace Sentinel.Service.DeviceMonitor;

/// <summary>
/// Watches <c>Win32_DeviceChangeEvent</c> (arrival/removal of any device). Complements
/// <see cref="UsbVolumeMonitor"/>, which only reports volumes with drive letters: this also covers
/// disks without a lettered volume. It only requests a rescan; identification happens in the scanner.
/// </summary>
public sealed class DeviceArrivalMonitor : BackgroundService
{
    private readonly ILogger<DeviceArrivalMonitor> _logger;
    private readonly ReconcileTrigger _trigger;
    private ManagementEventWatcher? _watcher;

    public DeviceArrivalMonitor(
        ILogger<DeviceArrivalMonitor> logger,
        ReconcileTrigger trigger)
    {
        _logger = logger;
        _trigger = trigger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        try
        {
            var query = new WqlEventQuery(
                "SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 2 OR EventType = 3"
            );

            _watcher = new ManagementEventWatcher(query);

            _watcher.EventArrived += OnDeviceChanged;

            _watcher.Start();

            _logger.LogInformation(
                "Device arrival monitoring started."
            );

            await Task.Delay(
                Timeout.Infinite,
                stoppingToken
            );
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Device arrival monitoring stopped."
            );
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Device arrival monitoring failed."
            );

            throw;
        }
        finally
        {
            if (_watcher is not null)
            {
                _watcher.EventArrived -= OnDeviceChanged;
                _watcher.Stop();
                _watcher.Dispose();
                _watcher = null;
            }
        }
    }

    private void OnDeviceChanged(
        object sender,
        EventArrivedEventArgs args)
    {
        try
        {
            var eventType = Convert.ToInt32(
                args.NewEvent["EventType"],
                CultureInfo.InvariantCulture
            );

            _logger.LogDebug(
                "Device change event: {EventType}",
                eventType == 2 ? "Arrival" : "Removal"
            );

            _trigger.Request();
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to process device change event."
            );
        }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }
}
