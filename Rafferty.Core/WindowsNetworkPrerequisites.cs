using System.Diagnostics;
using System.Security.Principal;

namespace Rafferty.Core;

public enum TcpTimestampState
{
    Unknown,
    Enabled,
    Disabled
}

public sealed record WindowsNetworkPrerequisitesSnapshot(
    bool IsAdministrator,
    bool BaseFilteringEngineRunning,
    TcpTimestampState TcpTimestamps,
    IReadOnlyList<string> PotentiallyConflictingServices);

public static class WindowsNetworkPrerequisites
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static async Task<bool> IsWinDivertRunningAsync(CancellationToken token = default) =>
        await IsServiceRunningAsync("WinDivert", token).ConfigureAwait(false) ||
        await IsServiceRunningAsync("WinDivert14", token).ConfigureAwait(false);

    public static async Task<bool> IsBaseFilteringEngineRunningAsync(CancellationToken token = default) =>
        await IsServiceRunningAsync("BFE", token).ConfigureAwait(false);

    public static async Task<string> EnsureTcpTimestampsAsync(CancellationToken token = default)
    {
        var current = await GetTcpTimestampStateAsync(token).ConfigureAwait(false);
        if (current == TcpTimestampState.Enabled)
        {
            return "TCP timestamps already enabled.";
        }
        if (current == TcpTimestampState.Disabled)
        {
            var changed = await RunAsync("netsh.exe", ["interface", "tcp", "set", "global", "timestamps=enabled"], token).ConfigureAwait(false);
            if (changed.ExitCode != 0) throw new InvalidOperationException($"Could not enable TCP timestamps: {changed.Output}");
            if (await GetTcpTimestampStateAsync(token).ConfigureAwait(false) != TcpTimestampState.Enabled)
                throw new InvalidOperationException("TCP timestamps remained disabled after the compatibility setup.");
            return "TCP timestamps enabled for strategy compatibility.";
        }
        return "TCP timestamp state could not be identified; no system setting was changed.";
    }

    public static async Task<TcpTimestampState> GetTcpTimestampStateAsync(CancellationToken token = default)
    {
        var current = await RunAsync("netsh.exe", ["interface", "tcp", "show", "global"], token).ConfigureAwait(false);
        var timestampLine = current.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => ContainsAny(line, "timestamps", "метки времени"));
        if (timestampLine is null) return TcpTimestampState.Unknown;
        if (ContainsAny(timestampLine, "enabled", "включено", "включены")) return TcpTimestampState.Enabled;
        if (ContainsAny(timestampLine, "disabled", "выключено", "отключено", "выключены")) return TcpTimestampState.Disabled;
        return TcpTimestampState.Unknown;
    }

    public static async Task<WindowsNetworkPrerequisitesSnapshot> InspectAsync(CancellationToken token = default)
    {
        var conflicts = new List<string>();
        foreach (var name in new[] { "IntelConnectivityNetworkService", "Intel Connectivity Network Service" })
        {
            var result = await RunAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["query", name], token).ConfigureAwait(false);
            if (result.ExitCode == 0 && result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
                conflicts.Add(name);
        }
        return new(IsAdministrator(), await IsBaseFilteringEngineRunningAsync(token).ConfigureAwait(false),
            await GetTcpTimestampStateAsync(token).ConfigureAwait(false), conflicts);
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static async Task<bool> IsServiceRunningAsync(string name, CancellationToken token)
    {
        var result = await RunAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["query", name], token).ConfigureAwait(false);
        return result.ExitCode == 0 && result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(file)}.");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        return (process.ExitCode, ((await stdout.ConfigureAwait(false)) + " " + (await stderr.ConfigureAwait(false))).Trim());
    }
}
