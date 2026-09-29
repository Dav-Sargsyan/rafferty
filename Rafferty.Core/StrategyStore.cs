using System.Text.Json;
using Rafferty.Shared;

namespace Rafferty.Core;

public sealed class StrategyStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StrategyDatabase? _cache;

    public StrategyStore(string path) => _path = path;

    public async Task<StrategyDatabase> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache is not null)
            {
                return _cache;
            }

            if (!File.Exists(_path))
            {
                throw new FileNotFoundException("Strategy database is not installed.", _path);
            }

            await using var stream = File.OpenRead(_path);
            var database = await JsonSerializer.DeserializeAsync<StrategyDatabase>(stream, JsonDefaults.Options, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("Strategy database is empty.");
            Validate(database);
            _cache = database;
            return database;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _cache = null;

    public async Task<Strategy> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var database = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return database.Strategies.FirstOrDefault(strategy => string.Equals(strategy.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Strategy not found: {id}");
    }

    private static void Validate(StrategyDatabase database)
    {
        if (database.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported strategy schema: {database.SchemaVersion}");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var strategy in database.Strategies)
        {
            strategy.Validate();
            if (!ids.Add(strategy.Id))
            {
                throw new InvalidDataException($"Duplicate strategy id: {strategy.Id}");
            }
        }
    }
}

