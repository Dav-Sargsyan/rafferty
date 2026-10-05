using System.Text.Json;
using Rafferty.Shared;

namespace Rafferty.Core;

public sealed record GoldenComparisonResult(bool Match, IReadOnlyList<string> Differences);

public static class GoldenStrategyComparer
{
    public static async Task<GoldenComparisonResult> CompareAsync(
        Strategy actual,
        string goldenPath,
        CancellationToken token = default)
    {
        await using var stream = File.OpenRead(goldenPath);
        var database = await JsonSerializer.DeserializeAsync<StrategyDatabase>(stream, JsonDefaults.Options, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Golden Classic strategy database is empty.");
        var expected = database.Strategies.FirstOrDefault(strategy =>
            string.Equals(strategy.Id, actual.Id, StringComparison.OrdinalIgnoreCase));
        if (expected is null)
            return new(false, [$"Golden strategy is not defined for {actual.Id}."]);

        var differences = new List<string>();
        var count = Math.Max(expected.Arguments.Count, actual.Arguments.Count);
        for (var index = 0; index < count; index++)
        {
            var expectedArgument = index < expected.Arguments.Count ? expected.Arguments[index] : "<missing>";
            var actualArgument = index < actual.Arguments.Count ? actual.Arguments[index] : "<missing>";
            if (!string.Equals(expectedArgument, actualArgument, StringComparison.Ordinal))
                differences.Add($"[{index}] expected: {expectedArgument} | actual: {actualArgument}");
        }
        return new(differences.Count == 0, differences);
    }
}
