using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Tomte.Core.Drop;

/// <summary>Where a share listens: this PC's address on the local network, and what Windows thinks of that network.</summary>
internal static class DropNetwork
{
    // Adapters that a virtual machine, a container or a VPN adds; they have gateways of their own that phones never reach.
    private static readonly string[] VirtualWords =
        ["Hyper-V", "vEthernet", "WSL", "Docker", "VirtualBox", "VMware", "Virtual", "TAP-", "VPN", "WireGuard", "Tailscale", "ZeroTier", "Npcap"];

    /// <summary>
    /// This PC's IPv4 address on the local network: on an interface that is up, is not loopback or a tunnel, and has an
    /// IPv4 default gateway. Real Ethernet and Wi-Fi come before virtual adapters. Null when there is none.
    /// </summary>
    public static IPAddress? LanAddress()
    {
        var found = new List<(IPAddress Address, int Rank)>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var properties = adapter.GetIPProperties();
            if (!properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            {
                continue;
            }

            bool isVirtual = VirtualWords.Any(w => adapter.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || adapter.Description.Contains(w, StringComparison.OrdinalIgnoreCase));
            bool real = adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet;
            int rank = (isVirtual ? 2 : 0) + (real ? 0 : 1);
            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                // 169.254 is what Windows gives itself when no DHCP server answered: no phone is on it.
                if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && address.GetAddressBytes() is not [169, 254, ..])
                {
                    found.Add((address, rank));
                }
            }
        }

        return found.OrderBy(f => f.Rank).Select(f => f.Address).FirstOrDefault();
    }

    /// <summary>
    /// Windows has the network <paramref name="address"/> is on set to Public, where its firewall keeps other devices
    /// out. False for loopback, an address no interface has, and when the category cannot be read.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(address)));
            if (adapter?.GetIPProperties().GetIPv4Properties() is not { } ipv4)
            {
                return false;
            }

            using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2", $"SELECT NetworkCategory FROM MSFT_NetConnectionProfile WHERE InterfaceIndex = {ipv4.Index}");
            using var profiles = searcher.Get();
            foreach (var profile in profiles)
            {
                using (profile)
                {
                    // NetworkCategory: 0 Public, 1 Private, 2 Domain.
                    if (Convert.ToInt32(profile["NetworkCategory"], System.Globalization.CultureInfo.InvariantCulture) == 0)
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception e) when (e is ManagementException or NetworkInformationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidCastException or FormatException)
        {
        }

        return false;
    }
}
