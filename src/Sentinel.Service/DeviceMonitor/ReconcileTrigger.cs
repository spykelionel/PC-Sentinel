using System.Threading.Channels;

namespace Sentinel.Service.DeviceMonitor;

/// <summary>
/// Coalescing "storage may have changed" signal. Event sources only request a rescan; the
/// <see cref="DeviceInventoryService"/> performs a full scan and reconciles, so a burst of events costs one scan.
/// </summary>
public sealed class ReconcileTrigger
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    public void Request() => _channel.Writer.TryWrite(true);

    /// <summary>Waits for a request. Returns false when the timeout elapses first.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(timeoutSource.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Discards requests that arrived during debouncing; the next scan covers them.</summary>
    public void Drain()
    {
        while (_channel.Reader.TryRead(out _))
        {
        }
    }
}
