using System.Net;
using System.Net.Sockets;

namespace BTCPayServer.Plugins.Coinsnap;

internal static class CoinsnapNetworkPolicy
{
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes switch
            {
                [0, ..] => false,
                [10, ..] => false,
                [100, >= 64 and <= 127, ..] => false,
                [127, ..] => false,
                [169, 254, ..] => false,
                [172, >= 16 and <= 31, ..] => false,
                [192, 0, 0, ..] => false,
                [192, 0, 2, ..] => false,
                [192, 88, 99, ..] => false,
                [192, 168, ..] => false,
                [198, 18 or 19, ..] => false,
                [198, 51, 100, ..] => false,
                [203, 0, 113, ..] => false,
                [>= 224, ..] => false,
                _ => true
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            if (address.Equals(IPAddress.IPv6None) ||
                address.Equals(IPAddress.IPv6Any) ||
                address.Equals(IPAddress.IPv6Loopback) ||
                address.IsIPv6LinkLocal ||
                address.IsIPv6Multicast ||
                address.IsIPv6SiteLocal)
            {
                return false;
            }

            // fc00::/7 (unique local) and 2001:db8::/32 (documentation).
            if ((bytes[0] & 0xfe) == 0xfc ||
                bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
            {
                return false;
            }

            // Transition ranges reach IPv4 through the IPv6 address, so judge the IPv4 they embed.
            // 64:ff9b::/96 (NAT64) carries it in the last four bytes, 2002::/16 (6to4) in bytes 2-5.
            if (bytes is [0x00, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0, ..])
                return IsPublicAddress(new IPAddress(bytes[12..16]));
            if (bytes is [0x20, 0x02, ..])
                return IsPublicAddress(new IPAddress(bytes[2..6]));
            // 64:ff9b:1::/48 (local NAT64) and 2001::/32 (Teredo) tunnel to addresses we cannot judge.
            if (bytes is [0x00, 0x64, 0xff, 0x9b, 0x00, 0x01, ..] or [0x20, 0x01, 0x00, 0x00, ..])
                return false;
            return true;
        }

        return false;
    }

    public static async ValueTask<Stream> ConnectPublicAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        if (!endpoint.Host.Equals(CoinsnapConstants.Host, StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("Coinsnap attempted to connect to a non-allowlisted host.");

        if (CoinsnapConstants.ConnectOverride is { } local)
        {
            var proxy = new Socket(local.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await proxy.ConnectAsync(local, cancellationToken);
                return new NetworkStream(proxy, ownsSocket: true);
            }
            catch
            {
                proxy.Dispose();
                throw;
            }
        }

        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new HttpRequestException("Coinsnap resolved to a private or reserved IP address.");

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (ex is OperationCanceledException)
                    throw;
                lastError = ex;
            }
        }

        throw new HttpRequestException("Could not connect to the allowlisted Coinsnap host.", lastError);
    }
}
