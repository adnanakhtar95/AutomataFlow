using System.Net;

namespace AutomataFlow.Api.Health;

/// <summary>Parses <c>Proxy:TrustedNetworks</c> (comma-separated CIDR, e.g. <c>172.18.0.0/16</c>). An invalid entry stops startup.</summary>
public static class TrustedProxies
{
    public static IReadOnlyList<IPNetwork> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var networks = new List<IPNetwork>();
        foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IPNetwork.TryParse(entry, out var network))
            {
                throw new InvalidOperationException($"Proxy:TrustedNetworks contains an invalid CIDR entry: '{entry}'.");
            }

            networks.Add(network);
        }

        return networks;
    }
}
