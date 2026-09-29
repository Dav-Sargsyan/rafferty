using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Rafferty.UI;

internal sealed record ServiceLatencyResult(bool Success, double? LatencyMs, string? Error = null);

internal sealed class LatencyTester : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(4) })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    public async Task<ServiceLatencyResult> TestYouTubeAsync(CancellationToken token = default)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("www.youtube.com", 443, token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            timer.Stop();
            return new(true, timer.Elapsed.TotalMilliseconds);
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or OperationCanceledException)
        {
            return new(false, null, exception.Message);
        }
    }

    public async Task<ServiceLatencyResult> TestDiscordAsync(CancellationToken token = default)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/gateway");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            timer.Stop();
            return (int)response.StatusCode is >= 200 and < 500
                ? new(true, timer.Elapsed.TotalMilliseconds)
                : new(false, null, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new(false, null, exception.Message);
        }
    }

    public async Task<ServiceLatencyResult> TestDiscordVoiceAsync(CancellationToken token = default)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync("stun.l.google.com", token).ConfigureAwait(false);
            var address = addresses.First(item => item.AddressFamily == AddressFamily.InterNetwork);
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var transaction = Guid.NewGuid().ToByteArray()[..12];
            var request = new byte[20];
            request[1] = 0x01;
            request[4] = 0x21; request[5] = 0x12; request[6] = 0xA4; request[7] = 0x42;
            transaction.CopyTo(request, 8);
            await udp.SendAsync(request, new IPEndPoint(address, 19302), token).ConfigureAwait(false);
            var response = await udp.ReceiveAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            timer.Stop();
            var valid = response.Buffer.Length >= 20 && response.Buffer[0] == 0x01 && response.Buffer[1] == 0x01 && response.Buffer.AsSpan(8, 12).SequenceEqual(transaction);
            return valid ? new(true, timer.Elapsed.TotalMilliseconds) : new(false, null, "Invalid STUN response");
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or OperationCanceledException)
        {
            return new(false, null, exception.Message);
        }
    }

    public void Dispose() => _http.Dispose();
}
