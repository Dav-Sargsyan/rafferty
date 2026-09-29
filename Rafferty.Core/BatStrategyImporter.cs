using System.Text.RegularExpressions;

namespace Rafferty.Core;

public static partial class BatStrategyImporter
{
    public static Strategy Import(string id, string name, string batText, string? attribution = null)
    {
        ArgumentNullException.ThrowIfNull(batText);
        var logical = JoinContinuedLines(batText);
        var match = WinwsCommand().Match(logical);
        if (!match.Success)
        {
            throw new InvalidDataException("No winws command was found in the BAT content.");
        }

        var arguments = Tokenize(match.Groups["args"].Value)
            .Where(token => token.StartsWith("--", StringComparison.Ordinal))
            .Select(NormalizeVariables)
            .ToArray();
        if (arguments.Length == 0)
        {
            throw new InvalidDataException("The winws command has no supported arguments.");
        }

        var protocols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ports = new HashSet<int>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in arguments)
        {
            if (argument.StartsWith("--filter-tcp", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("--wf-tcp", StringComparison.OrdinalIgnoreCase))
            {
                protocols.Add("tcp");
                AddPorts(argument, ports);
            }
            if (argument.StartsWith("--filter-udp", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("--wf-udp", StringComparison.OrdinalIgnoreCase))
            {
                protocols.Add("udp");
                AddPorts(argument, ports);
            }
            if (argument.Contains("quic", StringComparison.OrdinalIgnoreCase))
            {
                protocols.Add("quic");
                targets.Add("youtube");
            }
            if (argument.Contains("discord", StringComparison.OrdinalIgnoreCase) || argument.Contains("stun", StringComparison.OrdinalIgnoreCase))
            {
                protocols.Add("stun");
                targets.Add("discord-voice");
            }
            if (argument.Contains("list-google", StringComparison.OrdinalIgnoreCase))
            {
                targets.Add("youtube");
            }
            if (argument.Contains("list-general", StringComparison.OrdinalIgnoreCase))
            {
                targets.Add("general");
            }
        }

        return new Strategy(
            id,
            name,
            "Imported from a winws BAT command and normalized into structured arguments.",
            targets.Count == 0 ? ["general"] : targets.ToArray(),
            protocols.Count == 0 ? ["tcp"] : protocols.ToArray(),
            ports.Order().ToArray(),
            arguments,
            Attribution: attribution);
    }

    private static string JoinContinuedLines(string text) => Regex.Replace(text, @"\^\s*\r?\n", " ");

    private static IEnumerable<string> Tokenize(string commandLine)
    {
        foreach (Match match in CommandToken().Matches(commandLine))
        {
            var value = match.Value.Trim();
            yield return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
        }
    }

    private static string NormalizeVariables(string value) => value
        .Replace("%BIN%", "{engine}\\", StringComparison.OrdinalIgnoreCase)
        .Replace("%LISTS%", "{lists}\\", StringComparison.OrdinalIgnoreCase)
        // The upstream default is the inert port 12 until Game Filter is explicitly enabled.
        .Replace("%GameFilterTCP%", "12", StringComparison.OrdinalIgnoreCase)
        .Replace("%GameFilterUDP%", "12", StringComparison.OrdinalIgnoreCase)
        .Replace("^!", "!", StringComparison.Ordinal)
        .Replace("\"", string.Empty, StringComparison.Ordinal);

    private static void AddPorts(string argument, HashSet<int> ports)
    {
        var value = argument[(argument.IndexOf('=') + 1)..];
        foreach (var part in value.Split(','))
        {
            var start = part.Split('-', 2)[0];
            if (int.TryParse(start, out var port) && port is > 0 and <= 65535)
            {
                ports.Add(port);
            }
        }
    }

    [GeneratedRegex("(?im)(?:^|\\s)(?:start\\s+[^\\r\\n]*?\\s+)?(?:\"[^\"]*winws(?:2)?\\.exe\"|\\S*winws(?:2)?\\.exe)(?<args>.*)$")]
    private static partial Regex WinwsCommand();

    [GeneratedRegex("\"[^\"]*\"|\\S+")]
    private static partial Regex CommandToken();
}
