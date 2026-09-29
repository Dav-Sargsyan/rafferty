using System.IO.Pipes;
using System.IO;
using System.Text;
using System.Text.Json;
using Rafferty.Shared;

namespace Rafferty.UI;

public sealed class IpcClient
{
    public async Task<PipeResponse> SendAsync(PipeCommand command, string? strategyId = null, CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(".", AppPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2500, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        var request = new PipeRequest(command, strategyId, Guid.NewGuid().ToString("N"));
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonDefaults.Options)).ConfigureAwait(false);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<PipeResponse>(line ?? string.Empty, JsonDefaults.Options)
            ?? throw new InvalidDataException("The service returned an empty response.");
    }
}

