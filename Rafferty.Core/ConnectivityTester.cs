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

    public async Task<IReadOnlyList<DiagnosticResult>> RunDiagnosticsAsync(CancellationToken cancellationToken = default) =>
        await RunDiagnosticsAsync(ServiceTargetCatalog.All.Select(target => target.Id).ToArray(), cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<DiagnosticResult>> RunDiagnosticsAsync(
        IReadOnlyCollection<string> enabledTargetIds,
        CancellationToken cancellationToken = default)
    {
        var tasks = new List<Task<DiagnosticResult>>
        {
            TestInternetAsync(cancellationToken),
            TestDnsAsync("youtube.com", cancellationToken),
            TestAddressFamilyAsync(AddressFamily.InterNetwork, cancellationToken),
            TestAddressFamilyAsync(AddressFamily.InterNetworkV6, cancellationToken)
        };
        foreach (var targetId in enabledTargetIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(targetId, ServiceTargetCatalog.Voice, StringComparison.OrdinalIgnoreCase))
            {
                tasks.Add(TestStunAsync(cancellationToken));
                continue;
            }
            var target = ServiceTargetCatalog.Get(targetId);
            tasks.AddRange(target.TestEndpoints.Select(endpoint => TestEndpointAsync(endpoint, cancellationToken)));
            if (string.Equals(targetId, ServiceTargetCatalog.YouTube, StringComparison.OrdinalIgnoreCase))
            {
                tasks.Add(TestTcpAsync("www.youtube.com", 443, "tcp443", "TCP 443", cancellationToken));
                tasks.Add(TestHttp3Async(cancellationToken));
            }
            if (string.Equals(targetId, ServiceTargetCatalog.Discord, StringComparison.OrdinalIgnoreCase))
                tasks.Add(TestDiscordGatewayAsync(cancellationToken));
        }
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DiagnosticResult>> RunQuickHealthCheckAsync(
        bool checkYouTube,
        bool checkDiscord,
        bool checkVoice,
        CancellationToken cancellationToken = default)
    {
        var targets = new List<string>();
        if (checkYouTube) targets.Add(ServiceTargetCatalog.YouTube);
        if (checkDiscord) targets.Add(ServiceTargetCatalog.Discord);
        if (checkVoice) targets.Add(ServiceTargetCatalog.Voice);
        return await RunQuickHealthCheckAsync(targets, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DiagnosticResult>> RunQuickHealthCheckAsync(
        IReadOnlyCollection<string> enabledTargetIds,
        CancellationToken cancellationToken = default)
    {
        var tasks = new List<Task<DiagnosticResult>>();
        foreach (var targetId in enabledTargetIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(targetId, ServiceTargetCatalog.Voice, StringComparison.OrdinalIgnoreCase))
            {
                tasks.Add(TestStunAsync(cancellationToken));
                continue;
            }
            var target = ServiceTargetCatalog.Get(targetId);
            tasks.AddRange(target.TestEndpoints.Select(endpoint => TestEndpointAsync(endpoint, cancellationToken)));
            if (string.Equals(targetId, ServiceTargetCatalog.Discord, StringComparison.OrdinalIgnoreCase))
                tasks.Add(TestDiscordGatewayAsync(cancellationToken));
        }
        if (tasks.Count == 0) throw new ArgumentException("Select at least one service for the health check.");
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

        var serviceResults = ServiceTargetCatalog.SummarizeTargets(results, ServiceTargetCatalog.All.Select(target => target.Id));
        return new ReachabilitySnapshot(
            State("internet", "dns"),
            // QUIC is reported separately because HTTP/3 can be unavailable while
            // ordinary YouTube playback over HTTPS is fully functional.
            State("youtube", "googlevideo"),
            State("discord-api", "discord-cdn", "discord-gateway"),
            State("discord-stun"),
            DateTimeOffset.Now,
            serviceResults);
    }

    private async Task<DiagnosticResult> TestInternetAsync(CancellationToken token) =>
        await TestHttpAsync("https://www.gstatic.com/generate_204", "internet", "Internet", token, [204]).ConfigureAwait(false);

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
            return new(id, name, DiagnosticState.Success, $"Connected to {host}:{port}.", timer.Elapsed.TotalMilliseconds,
                ConnectivityState.TransportOnly, BlockClassification.Unknown, TransportReachable: true);
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or OperationCanceledException)
        {
            return new(id, name, DiagnosticState.Error, DescribeException(exception), timer.Elapsed.TotalMilliseconds,
                ConnectivityState.Failed, BlockClassification.TcpBlocked);
        }
    }

    private async Task<DiagnosticResult> TestHttpAsync(
        string url,
        string id,
        string name,
        CancellationToken token,
        IReadOnlyCollection<int>? expectedStatusCodes = null,
        string? expectedContentType = null,
        string? expectedRedirectHost = null,
        string? optionalBodyMarker = null)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            var finalUri = response.RequestMessage?.RequestUri?.ToString();
            var evaluation = ClassifyHttpStatus((int)response.StatusCode, expectedStatusCodes);
            var contentMatches = string.IsNullOrWhiteSpace(expectedContentType)
                || string.Equals(contentType, expectedContentType, StringComparison.OrdinalIgnoreCase);
            var redirectMatches = string.IsNullOrWhiteSpace(expectedRedirectHost)
                || string.Equals(response.RequestMessage?.RequestUri?.Host, expectedRedirectHost, StringComparison.OrdinalIgnoreCase);
            var bodyMatches = true;
            if (evaluation.ServiceValidated && !string.IsNullOrWhiteSpace(optionalBodyMarker))
            {
                var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                bodyMatches = body.Contains(optionalBodyMarker, StringComparison.OrdinalIgnoreCase);
            }
            var validated = evaluation.ServiceValidated && contentMatches && redirectMatches && bodyMatches;
            var detail = $"HTTP {(int)response.StatusCode}.";
            if (!evaluation.ServiceValidated)
                detail += evaluation.Connectivity == ConnectivityState.ServerRejected
                    ? " Server is reachable, but rejected this request."
                    : " Server is reachable, but this response does not validate the service."
                    ;
            else if (!validated)
                detail += " Response did not match the service validation rule.";
            return new(id, name, validated ? DiagnosticState.Success : evaluation.State, detail, timer.Elapsed.TotalMilliseconds,
                validated ? ConnectivityState.Working : evaluation.Connectivity,
                validated ? BlockClassification.Unknown : evaluation.Classification,
                (int)response.StatusCode, contentType, finalUri, TransportReachable: true, ServiceValidated: validated);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            var classification = IsTlsFailure(exception) ? BlockClassification.TlsBlocked : BlockClassification.Unknown;
            return new(id, name, DiagnosticState.Error, DescribeException(exception), timer.Elapsed.TotalMilliseconds,
                ConnectivityState.Failed, classification);
        }
    }

    private Task<DiagnosticResult> TestEndpointAsync(ServiceTestEndpoint endpoint, CancellationToken token)
    {
        var uri = new Uri(endpoint.Url);
        return string.Equals(uri.Scheme, "tcp", StringComparison.OrdinalIgnoreCase)
            ? TestTcpAsync(uri.Host, uri.Port, endpoint.Id, endpoint.DisplayName, token)
            : TestHttpAsync(endpoint.Url, endpoint.Id, endpoint.DisplayName, token,
                endpoint.ExpectedStatusCodes, endpoint.ExpectedContentType, endpoint.ExpectedRedirectHost, endpoint.OptionalBodyMarker);
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
            return new("discord-stun", "Discord Voice / STUN", valid ? DiagnosticState.Success : DiagnosticState.Error,
                valid ? "UDP STUN response received." : "Invalid STUN response.", timer.Elapsed.TotalMilliseconds,
                valid ? ConnectivityState.Working : ConnectivityState.Failed,
                valid ? BlockClassification.Unknown : BlockClassification.ProtocolBlocked,
                TransportReachable: valid, ServiceValidated: valid);
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or OperationCanceledException)
        {
            return new("discord-stun", "Discord Voice / STUN", DiagnosticState.Error, DescribeException(exception), timer.Elapsed.TotalMilliseconds,
                ConnectivityState.Failed, BlockClassification.ProtocolBlocked);
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
                valid ? $"Gateway HELLO received ({result.Count} bytes)." : "Gateway returned no HELLO payload.", timer.Elapsed.TotalMilliseconds,
                valid ? ConnectivityState.Working : ConnectivityState.Failed,
                valid ? BlockClassification.Unknown : BlockClassification.ProtocolBlocked,
                TransportReachable: valid, ServiceValidated: valid);
        }
        catch (Exception exception) when (exception is WebSocketException or TimeoutException or OperationCanceledException)
        {
            return new("discord-gateway", "Discord Gateway WebSocket", DiagnosticState.Error, DescribeException(exception), timer.Elapsed.TotalMilliseconds,
                ConnectivityState.Failed, BlockClassification.ProtocolBlocked);
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
            var evaluation = ClassifyHttpStatus((int)response.StatusCode, [204]);
            return new("youtube-quic", "YouTube QUIC / HTTP3", evaluation.State,
                $"HTTP/{response.Version} {(int)response.StatusCode}.", timer.Elapsed.TotalMilliseconds,
                evaluation.Connectivity, evaluation.Classification, (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType, response.RequestMessage?.RequestUri?.ToString(),
                TransportReachable: true, ServiceValidated: evaluation.ServiceValidated);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new("youtube-quic", "YouTube QUIC / HTTP3", DiagnosticState.Warning, DescribeException(exception), timer.Elapsed.TotalMilliseconds,
                ConnectivityState.Failed, BlockClassification.QuicBlocked);
        }
    }

    public static (DiagnosticState State, ConnectivityState Connectivity, BlockClassification Classification, bool ServiceValidated)
        ClassifyHttpStatus(int statusCode, IReadOnlyCollection<int>? expectedStatusCodes = null)
    {
        var expected = expectedStatusCodes is { Count: > 0 }
            ? expectedStatusCodes.Contains(statusCode)
            : statusCode is >= 200 and < 300;
        if (expected) return (DiagnosticState.Success, ConnectivityState.Working, BlockClassification.Unknown, true);
        if (statusCode is 401 or 403)
            return (DiagnosticState.Warning, ConnectivityState.ServerRejected, BlockClassification.ServerRejected, false);
        if (statusCode == 404)
            return (DiagnosticState.Warning, ConnectivityState.Inconclusive, BlockClassification.Unknown, false);
        if (statusCode is >= 400 and < 500)
            return (DiagnosticState.Warning, ConnectivityState.ServerRejected, BlockClassification.ServerRejected, false);
        return (DiagnosticState.Error, ConnectivityState.Failed, BlockClassification.Unknown, false);
    }

    private static bool IsTlsFailure(Exception exception) => DescribeException(exception).Contains("SSL", StringComparison.OrdinalIgnoreCase)
        || DescribeException(exception).Contains("TLS", StringComparison.OrdinalIgnoreCase)
        || DescribeException(exception).Contains("authentication", StringComparison.OrdinalIgnoreCase);

    private static string DescribeException(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
            parts.Add($"{current.GetType().Name}: {current.Message}");
        return string.Join(" --> ", parts);
    }

    public void Dispose() => _http.Dispose();
}

