namespace Rafferty.Core;

public static class StrategyNames
{
    public static string DisplayName(Strategy strategy) => DisplayName(strategy.Id, strategy.Name);

    public static string DisplayName(string? id, string? name = null)
    {
        if (string.Equals(id, "general", StringComparison.OrdinalIgnoreCase))
        {
            return "general";
        }

        var source = string.IsNullOrWhiteSpace(name) ? id ?? string.Empty : name;
        const string prefix = "general (";
        if (source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && source.EndsWith(')'))
        {
            return source[prefix.Length..^1];
        }

        if (source.StartsWith("general--", StringComparison.OrdinalIgnoreCase))
        {
            return source[9..].Replace('-', ' ').ToUpperInvariant();
        }

        return source;
    }
}
