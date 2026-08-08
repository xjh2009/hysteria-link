using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Hysteria2Link.Plugin.Services;

internal sealed class LanBroadcaster : IAsyncDisposable
{
    private const int MotdMaxLength = 32;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly UdpClient _client = new(AddressFamily.InterNetwork);
    private readonly byte[] _payload;
    private readonly Task _broadcastTask;

    public LanBroadcaster(string description, int port)
    {
        var motd = description?.Trim() ?? string.Empty;
        if (motd.Length > MotdMaxLength)
            motd = motd[..MotdMaxLength];
        if (motd.Length == 0)
            motd = "Hysteria P2P";
        _payload = Encoding.UTF8.GetBytes($"[MOTD]{motd}[/MOTD][AD]{port}[/AD]");
        _broadcastTask = BroadcastLoopAsync(_cancellation.Token);
    }

    private async Task BroadcastLoopAsync(CancellationToken cancellationToken)
    {
        var loopbackEndpoint = new IPEndPoint(IPAddress.Loopback, 4445);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _client.SendAsync(_payload, loopbackEndpoint, cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _client.Dispose();
        try
        {
            await _broadcastTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }

        _cancellation.Dispose();
    }
}
