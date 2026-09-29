namespace Rafferty.Core;

public sealed record StrategyDatabase(
    int SchemaVersion,
    string Source,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<Strategy> Strategies);

public sealed record Strategy(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> Targets,
    IReadOnlyList<string> Protocols,
    IReadOnlyList<int> Ports,
    IReadOnlyList<string> Arguments,
    bool Experimental = false,
    string? Attribution = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidDataException($"Invalid strategy id: {Id}");
        }

        if (Arguments.Count == 0 || Arguments.Any(argument => string.IsNullOrWhiteSpace(argument) || !argument.StartsWith("--", StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"Strategy {Id} contains invalid process arguments.");
        }

        if (Arguments.Any(argument => argument.Contains('\0')))
        {
            throw new InvalidDataException($"Strategy {Id} contains a null byte.");
        }
    }
}

