using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HandRaise.Infrastructure.Windows.Hardware;

public static class NetworkAddressHelper
{
    public static string? GetPreferredLanIpv4Address()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                             ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                             ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .OrderByDescending(GetInterfaceScore)
                .ToList();

            foreach (var ni in interfaces)
            {
                var ipProps = ni.GetIPProperties();
                var unicastAddresses = ipProps.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork &&
                                !IPAddress.IsLoopback(u.Address) &&
                                !u.Address.ToString().StartsWith("169.254.", StringComparison.OrdinalIgnoreCase))
                    .Select(u => u.Address.ToString())
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(unicastAddresses))
                {
                    return unicastAddresses;
                }
            }
        }
        catch
        {
            // Fallback no bloqueante
        }

        return null;
    }

    public static int GetInterfaceScore(NetworkInterface ni)
    {
        var name = ni.Name.ToLowerInvariant();
        var desc = ni.Description.ToLowerInvariant();

        if (name.Contains("vethernet", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("wsl", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("docker", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("tailscale", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("zerotier", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("tap", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("vpn", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("hyper-v", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("vmware", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("virtualbox", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("wsl", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("tailscale", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("zerotier", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("tap", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("vpn", StringComparison.OrdinalIgnoreCase))
        {
            return 10;
        }

        if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
        {
            return 100;
        }

        if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
        {
            return 90;
        }

        return 50;
    }
}
