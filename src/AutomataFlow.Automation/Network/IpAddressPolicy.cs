using System.Net;
using System.Net.Sockets;

namespace AutomataFlow.Automation.Network;

/// <summary>
/// Decides whether an IP address is a publicly routable destination. Everything that is loopback, private,
/// link-local, cloud metadata, multicast, reserved or documentation space is denied. Addresses that embed an
/// IPv4 address (IPv4-mapped, IPv4-compatible, NAT64, 6to4) are unwrapped and checked again.
/// </summary>
public static class IpAddressPolicy
{
    private static readonly (uint Network, int Prefix, string Reason)[] DeniedIPv4 =
    [
        (V4(0, 0, 0, 0), 8, "ipv4-this-network"),
        (V4(10, 0, 0, 0), 8, "ipv4-private"),
        (V4(100, 64, 0, 0), 10, "ipv4-shared-cgnat"),
        (V4(127, 0, 0, 0), 8, "ipv4-loopback"),
        (V4(169, 254, 0, 0), 16, "ipv4-link-local-or-metadata"),
        (V4(172, 16, 0, 0), 12, "ipv4-private"),
        (V4(192, 0, 0, 0), 24, "ipv4-protocol-assignments"),
        (V4(192, 0, 2, 0), 24, "ipv4-documentation"),
        (V4(192, 88, 99, 0), 24, "ipv4-6to4-relay"),
        (V4(192, 168, 0, 0), 16, "ipv4-private"),
        (V4(198, 18, 0, 0), 15, "ipv4-benchmarking"),
        (V4(198, 51, 100, 0), 24, "ipv4-documentation"),
        (V4(203, 0, 113, 0), 24, "ipv4-documentation"),
        (V4(224, 0, 0, 0), 4, "ipv4-multicast"),
        (V4(240, 0, 0, 0), 4, "ipv4-reserved"),
        (V4(168, 63, 129, 16), 32, "ipv4-cloud-platform"),
    ];

    /// <summary>Returns <c>null</c> when the address is an allowed public destination, otherwise a stable reason code.</summary>
    public static string? GetDenyReason(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return GetIPv4DenyReason(address);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return "unsupported-address-family";
        }

        if (address.ScopeId != 0)
        {
            return "ipv6-scoped";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return GetIPv4DenyReason(address.MapToIPv4()) is { } mapped ? "ipv4-mapped-" + mapped : null;
        }

        var b = address.GetAddressBytes();

        if (address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any))
        {
            return "ipv6-unspecified";
        }

        if (address.Equals(IPAddress.IPv6Loopback))
        {
            return "ipv6-loopback";
        }

        // ::/96 IPv4-compatible (deprecated) - deny outright.
        if (AllZero(b, 0, 12))
        {
            return "ipv6-ipv4-compatible";
        }

        // 64:ff9b::/96 NAT64 well-known prefix: check the embedded IPv4 address.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && AllZero(b, 4, 8))
        {
            var embedded = new IPAddress(b.AsSpan(12, 4));
            return GetIPv4DenyReason(embedded) is { } nat64 ? "ipv6-nat64-" + nat64 : null;
        }

        // 64:ff9b:1::/48 local-use NAT64.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4] == 0x00 && b[5] == 0x01)
        {
            return "ipv6-nat64-local";
        }

        // 2002::/16 6to4: check the embedded IPv4 address.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            var embedded = new IPAddress(b.AsSpan(2, 4));
            return GetIPv4DenyReason(embedded) is { } sixToFour ? "ipv6-6to4-" + sixToFour : null;
        }

        // 2001::/32 Teredo (embeds obfuscated client addresses) - deny outright.
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)
        {
            return "ipv6-teredo";
        }

        // 2001:db8::/32 documentation.
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8)
        {
            return "ipv6-documentation";
        }

        // 2001:2::/48 benchmarking, 2001:10::/28 ORCHID.
        if (b[0] == 0x20 && b[1] == 0x01 && ((b[2] == 0x00 && b[3] == 0x02 && b[4] == 0x00 && b[5] == 0x00) || (b[2] == 0x00 && (b[3] & 0xf0) == 0x10)))
        {
            return "ipv6-reserved";
        }

        // 3fff::/20 documentation.
        if (b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0x00)
        {
            return "ipv6-documentation";
        }

        // 100::/64 discard-only.
        if (b[0] == 0x01 && b[1] == 0x00 && AllZero(b, 2, 6))
        {
            return "ipv6-discard";
        }

        // fc00::/7 unique local (includes AWS metadata fd00:ec2::254).
        if ((b[0] & 0xfe) == 0xfc)
        {
            return "ipv6-unique-local";
        }

        // fe80::/10 link-local, fec0::/10 deprecated site-local.
        if (b[0] == 0xfe && ((b[1] & 0xc0) == 0x80 || (b[1] & 0xc0) == 0xc0))
        {
            return "ipv6-link-or-site-local";
        }

        // ff00::/8 multicast.
        if (b[0] == 0xff)
        {
            return "ipv6-multicast";
        }

        // Only global unicast (2000::/3) is routable on the public internet.
        if ((b[0] & 0xe0) != 0x20)
        {
            return "ipv6-not-global-unicast";
        }

        return null;
    }

    public static bool IsAllowed(IPAddress address) => GetDenyReason(address) is null;

    private static string? GetIPv4DenyReason(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

        if (value == uint.MaxValue)
        {
            return "ipv4-broadcast";
        }

        foreach (var (network, prefix, reason) in DeniedIPv4)
        {
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            if ((value & mask) == network)
            {
                return reason;
            }
        }

        return null;
    }

    private static bool AllZero(byte[] bytes, int start, int count)
    {
        for (var i = start; i < start + count; i++)
        {
            if (bytes[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static uint V4(byte a, byte b, byte c, byte d) => ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;
}
