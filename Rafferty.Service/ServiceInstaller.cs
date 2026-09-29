using System.Diagnostics;
using Rafferty.Shared;

namespace Rafferty.Service;

internal static class ServiceInstaller
{
    public static async Task<int> RunAsync(string action)
    {
        if (action == "install")
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine service executable path.");
            // Stop an older installation before updating its binary path.
            await RunScAsync("stop", AppPaths.ServiceName);
            var create = await RunScAsync("create", AppPaths.ServiceName, "binPath=", $"\"{executable}\"", "start=", "auto", "DisplayName=", "Rafferty Service");
            if (create == 1073)
            {
                var configure = await RunScAsync("config", AppPaths.ServiceName, "binPath=", $"\"{executable}\"", "start=", "auto", "DisplayName=", "Rafferty Service");
                if (configure != 0) return configure;
            }
            else if (create != 0)
            {
                return create;
            }
            await RunScAsync("failure", AppPaths.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/\"\"");
            return await RunScAsync("start", AppPaths.ServiceName);
        }

        await RunScAsync("stop", AppPaths.ServiceName);
        var delete = await RunScAsync("delete", AppPaths.ServiceName);
        return delete == 1060 ? 0 : delete;
    }

    private static async Task<int> RunScAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start sc.exe.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}

