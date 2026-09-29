using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using Rafferty.Shared;

namespace Rafferty.Core;

public sealed class ConnectivityTester : IDisposable
{
    private readonly HttpClient _http;

    public ConnectivityTester()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(4),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        })
        {
            Timeout = TimeSpan.FromSeconds(7),
            DefaultRequestHeaders = { UserAgent = { new("Rafferty", "1.0") } }
        };
    }

    public async Task<IReadOnlyList<DiagnosticResult>> RunDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var tasks = new Task<DiagnosticResult>[]
        {
            TestInternetAsync(cancellationToken),
            TestDnsAsync("youtube.com", cancellationToken),
            TestAddressFamilyAsync(AddressFamily.InterNetwork, cancellationToken),
            TestAddressFamilyAsync(AddressFamily.InterNetworkV6, cancellationToken),
            TestTcpAsync("www.youtube.com", 443, "tcp443", "TCP 443", cancellationToken),
            TestHttpAsync("https://www.youtube.com/generate_204", "youtube", "YouTube", cancellationToken),
            TestHttpAsync("https://redirector.googlevideo.com/report_mapping", "googlevideo", "Google Video CDN", cancellationToken),
            TestHttpAsync("https://discord.com/api/v10/gateway", "discord-api", "Discord API", cancellationToken),
            TestHttpAsync("https://cdn.discordapp.com", "discord-cdn", "Discord CDN", cancellationToken),
            TestDiscordGatewayAsync(cancellationToken),
            TestHttp3Async(cancellationToken),
            TestStunAsync(cancellationToken)
        };
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public static ReachabilitySnapshot Summarize(IReadOnlyList<DiagnosticResult> results)
    {
        ServiceReachability State(params string[] ids)
        {
            var selected = results.Where(result => ids.Contains(result.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (selected.Length == 0) return ServiceReachability.NotTested;
            if (selected.All(result => result.State == DiagnosticState.Success)) return ServiceReachability.Working;
            if (selected.Any(result => result.State == DiagnosticState.Success)) return ServiceReachability.Degraded;
            return ServiceReachability.Unavailable;
        }

        return new ReachabilitySnapshot(
            State("internet", "dns"),
            // QUIC is reported separately because HTTP/3 can be unavailable while
            // ordinary YouTube playback over HTTPS is fully functional.
            State("youtube", "googlevideo"),
            State("discord-api", "discord-cdn", "discord-gateway"),
            State("discord-stun"),
            DateTimeOffset.Now);
    }

    private async Task<DiagnosticResult> TestInternetAsync(CancellationToken token) =>
        await TestHttpAsync("https://www.gstatic.com/generate_204", "internet", "Internet", token).ConfigureAwait(false);

    private static async Task<DiagnosticResult> TestDnsAsync(string host, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
            return new("dns", "DNS", addresses.Length > 0 ? DiagnosticState.Success : DiagnosticState.Error, $"Resolved {addresses.Length} address(es).", timer.Elapsed.TotalMilliseconds);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return new("dns", "DNS", DiagnosticState.Error, exception.Message, timer.Elapsed.TotalMilliseconds);
        }
    }

    private static Task<DiagnosticResult> TestAddressFamilyAsync(AddressFamily family, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var available = family == AddressFamily.InterNetwork ? Socket.OSSupportsIPv4 : Socket.OSSupportsIPv6;
        var name = family == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
        return Task.FromResult(new DiagnosticResult(name.ToLowerInvariant(), name, available ? DiagnosticState.Success : DiagnosticState.Warning, available ? "Supported by this system." : "Not available."));
    }

    private static async Task<DiagnosticResult> TestTcpAsync(string host, int port, string id, string name, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            return new(id, name, DiagnosticState.Success, $"Connected to {host}:{port}.", timer.Elapsed.TotalMilliseconds);
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or OperationCanceledException)
        {
            return new(id, name, DiagnosticState.Error, exception.Message, timer.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<DiagnosticResult> TestHttpAsync(string url, string id, string name, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var success = (int)response.StatusCode is >= 200 and < 500;
            return new(id, name, success ? DiagnosticState.Success : DiagnosticState.Error, $"HTTP {(int)response.StatusCode}.", timer.Elapsed.TotalMilliseconds);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new(id, name, DiagnosticState.Error, exception.Message, timer.Elapsed.TotalMilliseconds);
        }
    }

    private static async Task<DiagnosticResult> TestStunAsync(CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync("stun.l.google.com", token).ConfigureAwait(false);
            var endpoint = new IPEndPoint(addresses.First(address => address.AddressFamily == AddressFamily.InterNetwork), 19302);
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var transaction = Guid.NewGuid().ToByteArray()[..12];
            var request = new byte[20];
            request[1] = 0x01;
            request[4] = 0x21; request[5] = 0x12; request[6] = 0xA4; request[7] = 0x42;
            transaction.CopyTo(request, 8);
            await udp.SendAsync(request, endpoint, token).ConfigureAwait(false);
            var result = await udp.ReceiveAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(4), token).ConfigureAwait(false);
            var valid = result.Buffer.Length >= 20 && result.Buffer[0] == 0x01 && result.Buffer[1] == 0x01 && result.Buffer.AsSpan(8, 12).SequenceEqual(transaction);
            return new("discord-stun", "Discord Voice / STUN", valid ? DiagnosticState.Success : DiagnosticState.Error, valid ? "UDP STUN response received." : "Invalid STUN response.", timer.Elapsed.TotalMilliseconds);
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or OperationCanceledException)
        {
            return new("discord-stun", "Discord Voice / STUN", DiagnosticState.Error, exception.Message, timer.Elapsed.TotalMilliseconds);
        }
    }

    private static async Task<DiagnosticResult> TestDiscordGatewayAsync(CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=10&encoding=json"), token)
                .WaitAsync(TimeSpan.FromSeconds(7), token).ConfigureAwait(false);
            var buffer = new byte[4096];
            var result = await socket.ReceiveAsync(buffer, token).WaitAsync(TimeSpan.FromSeconds(7), token).ConfigureAwait(false);
            var valid = result.Count > 0 && result.MessageType == WebSocketMessageType.Text;
            return new("discord-gateway", "Discord Gateway WebSocket", valid ? DiagnosticState.Success : DiagnosticState.Error,
                valid ? $"Gateway HELLO received ({result.Count} bytes)." : "Gateway returned no HELLO payload.", timer.Elapsed.TotalMilliseconds);
        }
        catch (Exception exception) when (exception is WebSocketException or TimeoutException or OperationCanceledException)
        {
            return new("discord-gateway", "Discord Gateway WebSocket", DiagnosticState.Error, exception.Message, timer.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<DiagnosticResult> TestHttp3Async(CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/generate_204")
            {
                Version = HttpVersion.Version30,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            return new("youtube-quic", "YouTube QUIC / HTTP3", DiagnosticState.Success,
                $"HTTP/{response.Version} {(int)response.StatusCode}.", timer.Elapsed.TotalMilliseconds);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new("youtube-quic", "YouTube QUIC / HTTP3", DiagnosticState.Warning, exception.Message, timer.Elapsed.TotalMilliseconds);
        }
    }

    public void Dispose() => _http.Dispose();
}

