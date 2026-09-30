using System.Text.RegularExpressions;
using Rafferty.Shared;

namespace Rafferty.Core;

public static partial class StrategyRuntimeTransformer
{
    public static IReadOnlyList<string> Transform(IReadOnlyList<string> arguments, EngineRuntimeOptions options)
    {
        ValidatePortRange(options.GameFilterTcp, nameof(options.GameFilterTcp));
        ValidatePortRange(options.GameFilterUdp, nameof(options.GameFilterUdp));

        var sections = SplitSections(arguments);
        var transformed = new List<IReadOnlyList<string>>();
        foreach (var section in sections)
        {
            var gameSection = section.Any(argument => argument is "--filter-tcp=12" or "--filter-udp=12");
            var usesIpSet = section.Any(argument => argument.StartsWith("--ipset=", StringComparison.OrdinalIgnoreCase));
            if (options.IpSetMode == IpSetMode.None && usesIpSet && !(gameSection && options.GameFilterEnabled))
            {
                continue;
            }

            var output = new List<string>(section.Count);
            foreach (var argument in section)
            {
                if ((options.IpSetMode == IpSetMode.Any || (options.IpSetMode == IpSetMode.None && gameSection && options.GameFilterEnabled))
                    && argument.StartsWith("--ipset=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                output.Add(options.GameFilterEnabled ? ReplaceGameSentinel(argument, options) : argument);
            }
            transformed.Add(output);
        }

        return transformed.SelectMany((section, index) => index == 0 ? section : new[] { "--new" }.Concat(section)).ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<string>> SplitSections(IReadOnlyList<string> arguments)
    {
        var result = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        foreach (var argument in arguments)
        {
            if (argument == "--new")
            {
                result.Add(current);
                current = [];
            }
            else
            {
                current.Add(argument);
            }
        }
        result.Add(current);
        return result;
    }

    private static string ReplaceGameSentinel(string argument, EngineRuntimeOptions options)
    {
        if (argument.StartsWith("--wf-tcp=", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("--filter-tcp=", StringComparison.OrdinalIgnoreCase))
        {
            return ReplacePort(argument, "12", options.GameFilterTcp);
        }
        if (argument.StartsWith("--wf-udp=", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("--filter-udp=", StringComparison.OrdinalIgnoreCase))
        {
            return ReplacePort(argument, "12", options.GameFilterUdp);
        }
        return argument;
    }

    private static string ReplacePort(string argument, string sentinel, string replacement)
    {
        var separator = argument.IndexOf('=');
        var values = argument[(separator + 1)..].Split(',');
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] == sentinel) values[index] = replacement;
        }
        return $"{argument[..(separator + 1)]}{string.Join(',', values)}";
    }

    private static void ValidatePortRange(string value, string name)
    {
        if (!PortRange().IsMatch(value)) throw new ArgumentException("Port range must use comma-separated ports or ranges.", name);
        foreach (var part in value.Split(','))
        {
            var bounds = part.Split('-', 2).Select(int.Parse).ToArray();
            if (bounds.Any(port => port is < 1 or > 65535) || bounds.Length == 2 && bounds[0] > bounds[1])
                throw new ArgumentOutOfRangeException(name, "Port range must be between 1 and 65535.");
        }
    }

    [GeneratedRegex(@"^\d+(?:-\d+)?(?:,\d+(?:-\d+)?)*$")]
    private static partial Regex PortRange();
}
