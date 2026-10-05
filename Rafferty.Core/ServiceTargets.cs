using Rafferty.Shared;

namespace Rafferty.Core;

public sealed record ServiceTestEndpoint(
    string Id,
    string DisplayName,
    string Url,
    string Feature,
    IReadOnlyList<int>? ExpectedStatusCodes = null,
    string? ExpectedContentType = null,
    string? ExpectedRedirectHost = null,
    string? OptionalBodyMarker = null);

public sealed record ServiceTarget(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Domains,
    IReadOnlyList<string> OptionalIpSets,
    IReadOnlyList<string> Protocols,
    IReadOnlyList<ServiceTestEndpoint> TestEndpoints,
    IReadOnlyList<string> Features,
    IReadOnlyList<int>? Ports = null,
    IReadOnlyList<string>? ClassicStrategyTags = null,
    IReadOnlyList<string>? NextGenStrategyTags = null,
    bool Enabled = false);

public static class ServiceTargetCatalog
{
    public const string YouTube = "youtube";
    public const string Discord = "discord";
    public const string Voice = "voice";
    public const string ChatGpt = "chatgpt";
    public const string Instagram = "instagram";
    public const string TikTok = "tiktok";
    public const string Telegram = "telegram";

    public static IReadOnlyList<ServiceTarget> All { get; } =
    [
        new(YouTube, "YouTube",
            ["youtube.com", "youtu.be", "googlevideo.com", "ytimg.com", "youtubei.googleapis.com"],
            [], ["tcp", "udp", "quic"],
            [
                new("youtube", "YouTube Web", "https://www.youtube.com/generate_204", "web"),
                new("googlevideo", "YouTube Media", "https://redirector.googlevideo.com/report_mapping", "media")
            ], ["web", "media", "quic"], [80, 443], ["tls-split", "quic-fake"], ["lua-tls", "lua-quic"]),
        new(Discord, "Discord",
            ["discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media", "discordcdn.com"],
            [], ["tcp", "udp", "websocket"],
            [
                new("discord-api", "Discord API", "https://discord.com/api/v10/gateway", "api"),
                new("discord-cdn", "Discord CDN", "https://cdn.discordapp.com", "media")
            ], ["api", "gateway", "media"], [80, 443, 2053, 2083, 2087, 2096, 8443], ["tls-split"], ["lua-tls"]),
        new(Voice, "Discord Voice", [], [], ["udp", "stun"], [], ["voice", "stun"], [19294, 50000], ["discord-udp"], ["lua-discord-udp"]),
        new(ChatGpt, "ChatGPT",
            ["chatgpt.com", "openai.com", "oaistatic.com", "oaiusercontent.com", "auth.openai.com", "cdn.openaimerge.com", "ws.chatgpt.com", "auth0.openai.com", "workos.com", "oaistatsig.com"],
            [], ["tcp", "websocket"],
            [
                new("chatgpt-web", "ChatGPT Web", "https://chatgpt.com/", "web"),
                new("chatgpt-api", "OpenAI API", "https://api.openai.com/v1/models", "api"),
                new("chatgpt-static", "ChatGPT Static CDN", "https://cdn.oaistatic.com/", "static")
            ], ["web", "backend", "static", "websocket"], [443], ["tls-multisplit"], ["lua-tls-multidisorder"]),
        new(Instagram, "Instagram",
            ["instagram.com", "cdninstagram.com", "fbcdn.net", "graph.instagram.com", "i.instagram.com", "static.cdninstagram.com"],
            [], ["tcp"],
            [
                new("instagram-web", "Instagram Web", "https://www.instagram.com/", "web"),
                new("instagram-api", "Instagram API", "https://graph.instagram.com/", "api"),
                new("instagram-media", "Instagram Media CDN", "https://static.cdninstagram.com/", "media")
            ], ["web", "api", "media"], [80, 443], ["tls-multisplit", "cdn"], ["lua-tls", "lua-cdn"]),
        new(TikTok, "TikTok",
            ["tiktok.com", "tiktokcdn.com", "tiktokv.com", "tiktokapis.com", "ttwstatic.com", "byteoversea.com", "ibytedtos.com", "muscdn.com", "musical.ly"],
            [], ["tcp", "quic"],
            [
                new("tiktok-web", "TikTok Web", "https://www.tiktok.com/", "web"),
                new("tiktok-api", "TikTok API", "https://open.tiktokapis.com/", "api"),
                new("tiktok-media", "TikTok Media CDN", "https://sf16-website-login.neutral.ttwstatic.com/", "media")
            ], ["web", "api", "media"], [80, 443], ["tls-multisplit", "quic-fake", "media"], ["lua-tls", "lua-quic", "lua-media"]),
        new(Telegram, "Telegram",
            ["telegram.org", "telegram.me", "t.me", "telegra.ph", "tdesktop.com", "telesco.pe"],
            ["149.154.160.0/20", "91.108.4.0/22"], ["tcp", "tls", "mtproto"],
            [
                new("telegram-web", "Telegram Web", "https://web.telegram.org/a/", "web"),
                new("telegram-desktop", "Telegram Desktop / MTProto", "tcp://149.154.167.50:443", "desktop"),
                new("telegram-api", "Telegram API", "https://api.telegram.org/", "api"),
                new("telegram-media", "Telegram Media", "https://telegram.org/img/t_logo.svg", "media")
            ], ["desktop", "web", "api", "media"], [80, 443], ["tls-multisplit", "mtproto-tcp"], ["lua-tls", "lua-mtproto"])
    ];

    public static ServiceTarget Get(string id) => All.FirstOrDefault(target =>
        string.Equals(target.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Unknown service target: {id}");

    public static IReadOnlyCollection<string> EnabledFromSettings(UserSettings settings)
    {
        var result = new List<string>();
        if (settings.CheckYouTube) result.Add(YouTube);
        if (settings.CheckDiscord) result.Add(Discord);
        if (settings.CheckVoice && settings.CheckDiscord) result.Add(Voice);
        if (settings.CheckChatGpt) result.Add(ChatGpt);
        if (settings.CheckInstagram) result.Add(Instagram);
        if (settings.CheckTikTok) result.Add(TikTok);
        if (settings.CheckTelegram) result.Add(Telegram);
        return result;
    }

    public static IReadOnlyList<ServiceTarget> WithSelection(UserSettings settings)
    {
        var enabled = EnabledFromSettings(settings).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All.Select(target => target with { Enabled = enabled.Contains(target.Id) }).ToArray();
    }

    public static IReadOnlyDictionary<string, ServiceReachability> SummarizeTargets(
        IReadOnlyList<DiagnosticResult> results,
        IEnumerable<string> targetIds)
    {
        var summary = new Dictionary<string, ServiceReachability>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in targetIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            summary[id] = Summarize(results, ResultIds(id));
        }
        return summary;
    }

    public static IReadOnlyCollection<string> ResultIds(string targetId)
    {
        if (string.Equals(targetId, Voice, StringComparison.OrdinalIgnoreCase)) return ["discord-stun"];
        if (string.Equals(targetId, Discord, StringComparison.OrdinalIgnoreCase))
            return ["discord-api", "discord-cdn", "discord-gateway"];
        return Get(targetId).TestEndpoints.Select(endpoint => endpoint.Id).ToArray();
    }

    private static ServiceReachability Summarize(IReadOnlyList<DiagnosticResult> results, IEnumerable<string> endpointIds)
    {
        var ids = endpointIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = results.Where(result => ids.Contains(result.Id)).ToArray();
        if (selected.Length == 0) return ServiceReachability.NotTested;
        // Older imported diagnostics have no explicit validation metadata. Their
        // Success state remains compatible, while live checks must opt in.
        var validated = selected.Where(result => result.ServiceValidated ||
            (result.Connectivity == ConnectivityState.NotTested && result.State == DiagnosticState.Success)).ToArray();
        if (validated.Length == selected.Length) return ServiceReachability.Working;
        if (validated.Length > 0 || selected.Any(result => result.TransportReachable || result.Connectivity == ConnectivityState.TransportOnly))
            return ServiceReachability.Degraded;
        return ServiceReachability.Unavailable;
    }
}

public static class ServiceListManager
{
    public static void Apply(IReadOnlyCollection<string> enabledServiceIds)
        => Apply(AppPaths.ListsDirectory, enabledServiceIds);

    public static void Apply(string listsDirectory, IReadOnlyCollection<string> enabledServiceIds)
    {
        Directory.CreateDirectory(listsDirectory);
        var targetPath = Path.Combine(listsDirectory, "active-hostlist.txt");

        var excluded = LoadDomains(Path.Combine(listsDirectory, "list-exclude.txt"))
            .Concat(LoadDomains(Path.Combine(listsDirectory, "list-exclude-user.txt")))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packs = ServiceTargetCatalog.All
            .Where(target => !string.Equals(target.Id, ServiceTargetCatalog.Voice, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(target => target.Id,
                target => LoadDomains(Path.Combine(listsDirectory, "services", $"{target.Id}.txt")).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var allServiceDomains = packs.Values.SelectMany(domains => domains).ToArray();
        var baseDomains = LoadDomains(Path.Combine(listsDirectory, "list-general.txt"))
            .Where(domain => !allServiceDomains.Any(serviceDomain => IsSameOrSubdomain(domain, serviceDomain)));
        var customDomains = LoadDomains(Path.Combine(listsDirectory, "list-general-user.txt"));
        var serviceDomains = enabledServiceIds
            .Where(id => !string.Equals(id, ServiceTargetCatalog.Voice, StringComparison.OrdinalIgnoreCase))
            .SelectMany(id => packs.TryGetValue(id, out var domains) ? domains : [])
            .Concat(baseDomains)
            .Concat(customDomains)
            .Where(domain => !IsExcluded(domain, excluded))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var dedicatedDomains = enabledServiceIds
            .Where(id => packs.ContainsKey(id))
            .SelectMany(id => packs[id])
            .ToArray();
        // Dedicated service profiles are checked before the general profile.
        // Keep their hosts out of the latter so one flow cannot match two
        // different desync methods in the same engine process.
        var generalDomains = baseDomains
            .Concat(customDomains)
            .Where(domain => !IsExcluded(domain, excluded))
            .Where(domain => !dedicatedDomains.Any(dedicated => IsSameOrSubdomain(domain, dedicated)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var target in ServiceTargetCatalog.All.Where(target => target.Id != ServiceTargetCatalog.Voice))
        {
            var activePack = packs[target.Id]
                .Where(domain => enabledServiceIds.Contains(target.Id, StringComparer.OrdinalIgnoreCase))
                .Where(domain => !IsExcluded(domain, excluded))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase);
            WriteAtomic(Path.Combine(listsDirectory, $"active-service-{target.Id}.txt"), activePack);
        }
        WriteAtomic(targetPath, serviceDomains);
        WriteAtomic(Path.Combine(listsDirectory, "active-general-hostlist.txt"), generalDomains);
    }

    private static void WriteAtomic(string targetPath, IEnumerable<string> lines)
    {
        var temporary = targetPath + ".new";
        File.WriteAllLines(temporary, lines);
        File.Move(temporary, targetPath, true);
    }

    private static IEnumerable<string> LoadDomains(string path)
    {
        if (!File.Exists(path)) return [];
        return File.ReadLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'));
    }

    private static bool IsExcluded(string domain, IReadOnlySet<string> excluded) => excluded.Any(item =>
        IsSameOrSubdomain(domain, item));

    private static bool IsSameOrSubdomain(string domain, string root) =>
        domain.Equals(root, StringComparison.OrdinalIgnoreCase)
        || domain.EndsWith("." + root, StringComparison.OrdinalIgnoreCase);
}

public static class ServiceStrategyComposer
{
    private static readonly string[] DefaultTargets =
        [ServiceTargetCatalog.YouTube, ServiceTargetCatalog.Discord, ServiceTargetCatalog.Voice];

    public static IReadOnlyList<string> Compose(
        IReadOnlyList<string> arguments,
        EngineType engineType,
        IReadOnlyCollection<string>? enabledServiceIds)
    {
        var enabled = (enabledServiceIds ?? DefaultTargets).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (enabled.SetEquals(DefaultTargets)) return arguments.ToArray();

        var sections = SplitSections(arguments);
        var output = new List<IReadOnlyList<string>>();
        var tcpTemplate = sections.FirstOrDefault(section =>
            section.Any(argument => FilterIncludesPort(argument, "--filter-tcp=", "443"))
            && section.Any(IsGeneralHostlist));
        var udpTemplate = sections.FirstOrDefault(section =>
            section.Any(argument => FilterIncludesPort(argument, "--filter-udp=", "443"))
            && section.Any(IsGeneralHostlist));
        foreach (var targetId in enabled.Where(id => id is ServiceTargetCatalog.ChatGpt or ServiceTargetCatalog.Instagram or ServiceTargetCatalog.TikTok or ServiceTargetCatalog.Telegram))
        {
            if (tcpTemplate is not null) output.Add(CreateExtension(tcpTemplate, targetId, engineType));
            if (udpTemplate is not null && ServiceTargetCatalog.Get(targetId).Protocols.Contains("quic", StringComparer.OrdinalIgnoreCase))
                output.Add(CreateExtension(udpTemplate, targetId, engineType));
        }
        foreach (var source in sections)
        {
            if (!enabled.Contains(ServiceTargetCatalog.YouTube)
                && source.Any(argument => argument.Contains("list-google.txt", StringComparison.OrdinalIgnoreCase))) continue;
            if (!enabled.Contains(ServiceTargetCatalog.Discord)
                && source.Any(argument => argument.Contains("hostlist-domains=discord.media", StringComparison.OrdinalIgnoreCase))) continue;
            if (!enabled.Contains(ServiceTargetCatalog.Voice)
                && source.Any(argument => argument.Contains("filter-l7=discord,stun", StringComparison.OrdinalIgnoreCase))) continue;
            if (source.Any(argument => argument.Contains("ipset-all.txt", StringComparison.OrdinalIgnoreCase))) continue;

            var section = source.ToList();
            var hostlistIndex = section.FindIndex(argument =>
                argument.Contains("list-general.txt", StringComparison.OrdinalIgnoreCase)
                || argument.Contains("list-general-user.txt", StringComparison.OrdinalIgnoreCase));
            if (hostlistIndex >= 0)
            {
                section.RemoveAll(argument =>
                    argument.Contains("list-general.txt", StringComparison.OrdinalIgnoreCase)
                    || argument.Contains("list-general-user.txt", StringComparison.OrdinalIgnoreCase));
                section.Insert(hostlistIndex, "--hostlist={lists}\\active-general-hostlist.txt");
            }
            output.Add(section);
        }
        return output.SelectMany((section, index) => index == 0 ? section : new[] { "--new" }.Concat(section)).ToArray();
    }

    private static IReadOnlyList<string> CreateExtension(IReadOnlyList<string> template, string targetId, EngineType engineType)
    {
        var result = template
            .Where(argument => !IsGeneralHostlist(argument))
            .Where(argument => engineType != EngineType.NextGen || !argument.StartsWith("--wf-", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var filterIndex = result.FindIndex(argument => argument.StartsWith("--filter-", StringComparison.OrdinalIgnoreCase));
        result.Insert(Math.Max(0, filterIndex + 1), $"--hostlist={{lists}}\\active-service-{targetId}.txt");
        return result;
    }

    private static bool IsGeneralHostlist(string argument) =>
        argument.Contains("list-general.txt", StringComparison.OrdinalIgnoreCase)
        || argument.Contains("list-general-user.txt", StringComparison.OrdinalIgnoreCase)
        || argument.Contains("active-general-hostlist.txt", StringComparison.OrdinalIgnoreCase);

    private static bool FilterIncludesPort(string argument, string prefix, string port) =>
        argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && argument[prefix.Length..].Split(',').Contains(port, StringComparer.Ordinal);

    private static IReadOnlyList<IReadOnlyList<string>> SplitSections(IReadOnlyList<string> arguments)
    {
        var result = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        foreach (var argument in arguments)
        {
            if (argument == "--new") { result.Add(current); current = []; }
            else current.Add(argument);
        }
        result.Add(current);
        return result;
    }
}
