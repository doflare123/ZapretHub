using System.Net;
using System.Net.NetworkInformation;

namespace ZapretHub.Windows;

public static class NetworkIdentity
{
    public static string Current()
    {
        var gateways = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                    or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT)
            .SelectMany(adapter => adapter.GetIPProperties().GatewayAddresses
                .Where(gateway => !gateway.Address.Equals(IPAddress.Any) && !gateway.Address.Equals(IPAddress.IPv6Any))
                .Select(gateway => $"{adapter.Id}/{gateway.Address}"));
        return string.Join(';', gateways.Order(StringComparer.Ordinal));
    }
}
