using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MulletHopWaiverKiosk;

internal sealed record KioskNetworkDetails(
    string ConnectionName,
    string AdapterDescription,
    string IpAddress,
    string SubnetMask,
    string DefaultGateway)
{
    public static KioskNetworkDetails Unavailable { get; } = new(
        "No active private-network connection found",
        "Unavailable",
        "Unavailable",
        "Unavailable",
        "Unavailable");
}

internal static class KioskNetworkDetailsProvider
{
    public static KioskNetworkDetails GetCurrent()
    {
        try
        {
            var candidates = new List<(NetworkInterface Adapter,
                UnicastIPAddressInformation Address, string Gateway)>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or
                        NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var properties = adapter.GetIPProperties();
                var gateway = properties.GatewayAddresses
                    .Select(item => item.Address)
                    .FirstOrDefault(address =>
                        address.AddressFamily == AddressFamily.InterNetwork &&
                        !address.Equals(IPAddress.Any))
                    ?.ToString() ?? string.Empty;
                foreach (var address in properties.UnicastAddresses.Where(item =>
                             item.Address.AddressFamily == AddressFamily.InterNetwork &&
                             !IPAddress.IsLoopback(item.Address) &&
                             !item.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)))
                {
                    candidates.Add((adapter, address, gateway));
                }
            }

            var selected = candidates
                .OrderByDescending(item => !string.IsNullOrWhiteSpace(item.Gateway))
                .ThenByDescending(item => item.Adapter.NetworkInterfaceType is
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                .ThenBy(item => item.Adapter.Name, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault();
            if (selected.Adapter is null)
                return KioskNetworkDetails.Unavailable;

            return new KioskNetworkDetails(
                selected.Adapter.Name,
                selected.Adapter.Description,
                selected.Address.Address.ToString(),
                selected.Address.IPv4Mask?.ToString() ?? "Unavailable",
                string.IsNullOrWhiteSpace(selected.Gateway) ? "Unavailable" : selected.Gateway);
        }
        catch (Exception ex)
        {
            KioskLog.Write("Could not read the kiosk network connection: " + ex.Message);
            return KioskNetworkDetails.Unavailable;
        }
    }
}
