using System.Text.Json;
using Rafferty.Shared;

namespace Rafferty.Core;

public sealed class StrategyStore
{
    private readonly string _path;
    private readonly string? _customPath;
    private readonly string? _nextGenPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StrategyDatabase? _cache;

    public StrategyStore(string path, string? customPath = null, string? nextGenPath = null)
    {
        _path = path;
        _customPath = customPath;
        _nextGenPath = nextGenPath;
    }

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

            var database = await LoadMergedAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task AddOrReplaceAsync(Strategy strategy, CancellationToken cancellationToken = default)
    {
        strategy.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrWhiteSpace(_customPath)) throw new InvalidOperationException("This strategy store does not accept user imports.");
            var current = File.Exists(_customPath)
                ? await LoadFromDiskAsync(_customPath, cancellationToken).ConfigureAwait(false)
                : new StrategyDatabase(1, "User-imported BAT strategies", DateTimeOffset.UtcNow, []);
            var strategies = current.Strategies
                .Where(item => !string.Equals(item.Id, strategy.Id, StringComparison.OrdinalIgnoreCase))
                .Append(strategy)
                .ToArray();
            var updated = current with { UpdatedAt = DateTimeOffset.UtcNow, Strategies = strategies };
            Validate(updated);
            Directory.CreateDirectory(Path.GetDirectoryName(_customPath)!);
            var temporary = _customPath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(updated, JsonDefaults.Options), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _customPath, true);
            _cache = await LoadMergedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StrategyDatabase> LoadMergedAsync(CancellationToken cancellationToken)
    {
        var bundled = await LoadWithRollbackAsync(_path, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(_nextGenPath) && File.Exists(_nextGenPath))
        {
            var nextGen = await LoadWithRollbackAsync(_nextGenPath, cancellationToken).ConfigureAwait(false);
            bundled = bundled with
            {
                Source = $"{bundled.Source}; {nextGen.Source}",
                UpdatedAt = bundled.UpdatedAt > nextGen.UpdatedAt ? bundled.UpdatedAt : nextGen.UpdatedAt,
                Strategies = bundled.Strategies.Concat(nextGen.Strategies).ToArray()
            };
        }
        if (string.IsNullOrWhiteSpace(_customPath) || !File.Exists(_customPath)) return bundled;
        var custom = await LoadFromDiskAsync(_customPath, cancellationToken).ConfigureAwait(false);
        var customIds = custom.Strategies.Select(strategy => strategy.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return bundled with
        {
            Source = $"{bundled.Source}; {custom.Source}",
            UpdatedAt = bundled.UpdatedAt > custom.UpdatedAt ? bundled.UpdatedAt : custom.UpdatedAt,
            Strategies = bundled.Strategies.Where(strategy => !customIds.Contains(strategy.Id)).Concat(custom.Strategies).ToArray()
        };
    }

    private static async Task<StrategyDatabase> LoadFromDiskAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<StrategyDatabase>(stream, JsonDefaults.Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Strategy database is empty.");
    }

    private static async Task<StrategyDatabase> LoadWithRollbackAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var database = await LoadFromDiskAsync(path, cancellationToken).ConfigureAwait(false);
            Validate(database);
            return database;
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            var previous = path + ".previous";
            if (!File.Exists(previous)) throw;
            var rollback = await LoadFromDiskAsync(previous, cancellationToken).ConfigureAwait(false);
            Validate(rollback);
            return rollback;
        }
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

