using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Rafferty.Shared;
using Microsoft.Extensions.Hosting;

namespace Rafferty.Service;

public sealed class PipeServerWorker(CommandDispatcher dispatcher) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = CreateSecurePipe();
            await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            await HandleClientAsync(pipe, stoppingToken).ConfigureAwait(false);
        }
    }

    private static NamedPipeServerStream CreateSecurePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            AppPaths.PipeName,
            PipeDirection.InOut,
            4,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            16 * 1024,
            16 * 1024,
            security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line)) return;
        PipeResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<PipeRequest>(line, JsonDefaults.Options) ?? throw new InvalidDataException("Empty request.");
            response = await dispatcher.DispatchAsync(request, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            response = new(false, exception.Message);
        }
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonDefaults.Options)).ConfigureAwait(false);
    }
}

