using Rafferty.Shared;

namespace Rafferty.Core;

public static class ServiceStrategyCache
{
    public static IReadOnlyDictionary<string, ServiceStrategyResult> ForNetwork(
        IEnumerable<ServiceStrategyResult>? results,
        string networkFingerprint)
    {
        return (results ?? [])
            .Where(result => string.Equals(result.NetworkFingerprint, networkFingerprint, StringComparison.Ordinal))
            .GroupBy(result => result.ServiceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(result => result.SuccessScore)
                    .ThenByDescending(result => result.LastValidated).First(),
                StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<ServiceStrategyResult> Upsert(
        IEnumerable<ServiceStrategyResult>? existing,
        ServiceStrategyResult result)
    {
        var retained = (existing ?? []).Where(item =>
            !string.Equals(item.ServiceId, result.ServiceId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(item.NetworkFingerprint, result.NetworkFingerprint, StringComparison.Ordinal));
        return retained.Append(result)
            .OrderByDescending(item => item.LastValidated)
            .Take(128)
            .ToArray();
    }
}
