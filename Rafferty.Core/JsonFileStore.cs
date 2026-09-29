using System.Text.Json;
using Rafferty.Shared;

namespace Rafferty.Core;

public sealed class JsonFileStore<T>(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return default;
            }
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(T value, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Store path has no directory.");
            Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            var backup = path + ".bak";
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(path))
            {
                File.Replace(temporary, path, backup, true);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

