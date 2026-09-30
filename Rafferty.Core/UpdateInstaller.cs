using System.Security.Cryptography;

namespace Rafferty.Core;

public static class UpdateInstaller
{
    public static async Task<string> ReplaceExecutableAsync(string downloaded, string target, CancellationToken token = default)
    {
        downloaded = Path.GetFullPath(downloaded);
        target = Path.GetFullPath(target);
        if (!File.Exists(downloaded)) throw new FileNotFoundException("Downloaded update was not found.", downloaded);
        if (!File.Exists(target)) throw new FileNotFoundException("Current Rafferty executable was not found.", target);
        var backup = target + ".update-backup";
        try
        {
            File.Copy(target, backup, true);
            File.Copy(downloaded, target, true);
            if (!await FilesMatchAsync(downloaded, target, token).ConfigureAwait(false))
                throw new IOException("Updated executable verification failed after replacement.");
            return backup;
        }
        catch
        {
            Rollback(target, backup);
            throw;
        }
    }

    public static void Commit(string backup)
    {
        if (File.Exists(backup)) File.Delete(backup);
    }

    public static void Rollback(string target, string backup)
    {
        if (!File.Exists(backup)) return;
        File.Copy(backup, target, true);
        File.Delete(backup);
    }

    private static async Task<bool> FilesMatchAsync(string left, string right, CancellationToken token)
    {
        await using var leftStream = File.OpenRead(left);
        await using var rightStream = File.OpenRead(right);
        var leftHash = await SHA256.HashDataAsync(leftStream, token).ConfigureAwait(false);
        var rightHash = await SHA256.HashDataAsync(rightStream, token).ConfigureAwait(false);
        return leftHash.AsSpan().SequenceEqual(rightHash);
    }
}
