
using System.Globalization;
using System.Management;

namespace Sentinel.Service.DeviceMonitor;

public sealed class UsbVolumeMonitor : BackgroundService
{
    private readonly ILogger<UsbVolumeMonitor> _logger;
    private readonly ReconcileTrigger _trigger;
    private ManagementEventWatcher? _watcher;

    public UsbVolumeMonitor(
        ILogger<UsbVolumeMonitor> logger,
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
                "SELECT * FROM Win32_VolumeChangeEvent"
            );

            _watcher = new ManagementEventWatcher(query);

            _watcher.EventArrived += OnVolumeChanged;

            _watcher.Start();

            _logger.LogInformation(
                "USB volume monitoring started."
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
                "USB volume monitoring stopped."
            );
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "USB volume monitoring failed."
            );

            throw;
        }
        finally
        {
            if (_watcher is not null)
            {
                _watcher.EventArrived -= OnVolumeChanged;
                _watcher.Stop();
                _watcher.Dispose();
                _watcher = null;
            }
        }
    }

    private void OnVolumeChanged(
        object sender,
        EventArrivedEventArgs args)
    {
        try
        {
            var eventType = Convert.ToInt32(
                args.NewEvent["EventType"],
                CultureInfo.InvariantCulture
            );

            var driveName = Convert.ToString(
                args.NewEvent["DriveName"],
                CultureInfo.InvariantCulture
            ) ?? "Unknown";

            var action = eventType switch
            {
                2 => "Volume connected",
                3 => "Volume disconnected",
                _ => "Unknown volume event"
            };

            _logger.LogInformation(
                "Volume event: {Action} | Drive: {Drive} | Time: {Timestamp}",
                action,
                driveName,
                DateTimeOffset.Now
            );

            _trigger.Request();
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to process volume event."
            );
        }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }
}
