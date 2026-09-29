using System.Diagnostics;
using System.IO;
using Rafferty.Core;
using Rafferty.Shared;

namespace Rafferty.UI;

internal sealed class SettingsService
{
    private const string StartupTaskName = "Rafferty";
    private readonly JsonFileStore<UserSettings> _store = new(AppPaths.ConfigFile);

    public async Task<UserSettings> LoadAsync(CancellationToken token = default)
    {
        try
        {
            var settings = await _store.LoadAsync(token).ConfigureAwait(false) ?? new UserSettings();
            return settings with { Language = Localization.Normalize(settings.Language) };
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            return new UserSettings();
        }
    }

    public async Task SaveAsync(UserSettings settings, CancellationToken token = default)
    {
        if (settings.StartWithWindows)
        {
            await RunTaskSchedulerAsync(false, "/Create", "/TN", StartupTaskName, "/TR", $"\"{Environment.ProcessPath}\" --minimized", "/SC", "ONLOGON", "/RL", "HIGHEST", "/F").ConfigureAwait(false);
        }
        else
        {
            await RunTaskSchedulerAsync(true, "/Delete", "/TN", StartupTaskName, "/F").ConfigureAwait(false);
        }
        await _store.SaveAsync(settings, token).ConfigureAwait(false);
    }

    private static async Task RunTaskSchedulerAsync(bool ignoreFailure, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not configure Windows startup.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (!ignoreFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException("Windows startup task could not be configured.");
        }
    }

}
