using System.Diagnostics;
using System.IO;
using Rafferty.Core;

namespace Rafferty.UI;

internal static class SelfUpdateInstaller
{
    public static Process StartUpdater(string downloadedExecutable)
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Current executable path is unavailable.");
        var start = new ProcessStartInfo
        {
            FileName = downloadedExecutable,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(current);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the update helper.");
    }

    public static async Task ApplyAsync(string targetExecutable, int previousProcessId, CancellationToken token = default)
    {
        var downloaded = Environment.ProcessPath ?? throw new InvalidOperationException("Downloaded executable path is unavailable.");
        targetExecutable = Path.GetFullPath(targetExecutable);
        downloaded = Path.GetFullPath(downloaded);
        if (string.Equals(targetExecutable, downloaded, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Update source and target paths must be different.");

        try
        {
            using var previous = Process.GetProcessById(previousProcessId);
            await previous.WaitForExitAsync(token).WaitAsync(TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // The previous process already exited.
        }

        string? backup = null;
        var readyMarker = Path.Combine(Path.GetTempPath(), $"Rafferty-update-{Guid.NewGuid():N}.ready");
        try
        {
            backup = await UpdateInstaller.ReplaceExecutableAsync(downloaded, targetExecutable, token).ConfigureAwait(false);

            var restart = new ProcessStartInfo { FileName = targetExecutable, UseShellExecute = true };
            restart.ArgumentList.Add("--post-update-ready");
            restart.ArgumentList.Add(readyMarker);
            using var updated = Process.Start(restart) ?? throw new InvalidOperationException("Updated Rafferty could not be restarted.");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (!File.Exists(readyMarker) && !updated.HasExited && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(200, token).ConfigureAwait(false);
            if (!File.Exists(readyMarker))
            {
                if (!updated.HasExited)
                {
                    updated.Kill(true);
                    await updated.WaitForExitAsync(token).ConfigureAwait(false);
                }
                throw new InvalidOperationException("Updated Rafferty did not confirm a successful startup.");
            }
            UpdateInstaller.Commit(backup);
            ScheduleSelfDeletion(downloaded);
        }
        catch
        {
            if (backup is not null)
            {
                UpdateInstaller.Rollback(targetExecutable, backup);
                _ = Process.Start(new ProcessStartInfo { FileName = targetExecutable, UseShellExecute = true });
            }
            throw;
        }
        finally
        {
            if (File.Exists(readyMarker)) File.Delete(readyMarker);
        }
    }

    private static void ScheduleSelfDeletion(string path)
    {
        path = Path.GetFullPath(path);
        var escapedPath = path.Replace("'", "''", StringComparison.Ordinal);
        var command = $"$p='{escapedPath}'; for($i=0;$i -lt 30;$i++){{ Start-Sleep -Milliseconds 500; Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue; if(-not (Test-Path -LiteralPath $p)){{ break }} }}";
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-WindowStyle");
        start.ArgumentList.Add("Hidden");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);
        Process.Start(start);
    }
}
