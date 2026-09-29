namespace Rafferty.Core;

public sealed record NetworkProfile(
    string NetworkId,
    string? LastStrategyId,
    IReadOnlyList<string> BackupStrategyIds,
    bool HasIpv4,
    bool HasIpv6,
    IReadOnlyList<string> DnsServers,
    DateTimeOffset? LastSuccessfulTest,
    double ReliabilityScore);

