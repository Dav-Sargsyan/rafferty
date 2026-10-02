using Rafferty.Shared;

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
    string? Attribution = null,
    EngineType EngineType = EngineType.Classic,
    IReadOnlyList<string>? LuaFiles = null,
    IReadOnlyList<string>? RequiredFiles = null)
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

        if (EngineType == EngineType.Auto)
        {
            throw new InvalidDataException($"Strategy {Id} must target a concrete engine.");
        }

        if ((LuaFiles ?? []).Concat(RequiredFiles ?? []).Any(path => string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains("..", StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"Strategy {Id} contains an unsafe runtime resource path.");
        }
    }
}

