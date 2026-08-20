using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MulletHop.LocalNetworking;

internal static class LocalNetworkAddress
{
    public static bool IsPrivateOrDirectlyConnectedIpv4(IPAddress address) =>
        IsPrivateIpv4(address) || IsDirectlyConnectedIpv4(address);

    public static bool IsUsableAdapterIpv4(IPAddress address)
    {
        address = Normalize(address);
        if (address.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] is > 0 and < 224 &&
               !(bytes[0] == 255 && bytes[1] == 255 && bytes[2] == 255 && bytes[3] == 255);
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        address = Normalize(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 127 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 169 && bytes[1] == 254);
    }

    private static bool IsDirectlyConnectedIpv4(IPAddress target)
    {
        target = Normalize(target);
        if (!IsUsableAdapterIpv4(target))
            return false;

        var targetValue = ToUInt32(target);
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or
                        NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    var local = Normalize(unicast.Address);
                    if (!IsUsableAdapterIpv4(local))
                        continue;

                    var prefixLength = unicast.PrefixLength;
                    if (prefixLength is < 0 or > 32)
                        continue;
                    var mask = prefixLength == 0
                        ? 0U
                        : uint.MaxValue << (32 - prefixLength);
                    var network = ToUInt32(local) & mask;
                    if ((targetValue & mask) != network)
                        continue;

                    if (prefixLength < 31)
                    {
                        var broadcast = network | ~mask;
                        if (targetValue == network || targetValue == broadcast)
                            return false;
                    }
                    return true;
                }
            }
        }
        catch (NetworkInformationException)
        {
            return false;
        }
        return false;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }
}
