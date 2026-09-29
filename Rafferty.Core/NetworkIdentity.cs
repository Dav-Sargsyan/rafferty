using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace Rafferty.Core;

public static class NetworkIdentity
{
    public static string GetCurrent()
    {
        var parts = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .OrderBy(adapter => adapter.Id, StringComparer.Ordinal)
            .Select(adapter => $"{adapter.NetworkInterfaceType}|{adapter.GetIPProperties().GatewayAddresses.FirstOrDefault()?.Address}|{string.Join(',', adapter.GetIPProperties().DnsAddresses)}")
            .ToArray();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(';', parts)));
        return Convert.ToHexString(bytes[..12]);
    }
}
