using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>
/// Reads a Lightning Address's LNURL metadata straight from the server. The expected description
/// hash is derived from what the server actually serves, so the gate makes no assumption about
/// how that metadata is formatted.
/// </summary>
public static class LnurlDirectory
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<string> GetMetadataHashAsync(string lightningAddress)
    {
        var url = MetadataUrl(lightningAddress);
        var payload = await Http.GetStringAsync(url);

        var metadata = JsonDocument.Parse(payload).RootElement.GetProperty("metadata").GetString()
                       ?? throw new InvalidOperationException($"{url} returned no metadata.");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(metadata)))
            .ToLowerInvariant();
    }

    /// <summary>Mints a BOLT11 straight from the server, bypassing BTCPay; a decoy for fault drills.</summary>
    public static async Task<string> MintAsync(string lightningAddress, long satoshis)
    {
        var meta = JsonDocument.Parse(await Http.GetStringAsync(MetadataUrl(lightningAddress))).RootElement;
        var callback = meta.GetProperty("callback").GetString()!;
        var separator = callback.Contains('?') ? '&' : '?';
        var result = JsonDocument.Parse(
            await Http.GetStringAsync($"{callback}{separator}amount={satoshis * 1000}")).RootElement;
        return result.GetProperty("pr").GetString()!;
    }

    public static async Task<(long MinSat, long MaxSat)> GetSendableAsync(string lightningAddress)
    {
        var meta = JsonDocument.Parse(await Http.GetStringAsync(MetadataUrl(lightningAddress))).RootElement;
        return (meta.GetProperty("minSendable").GetInt64() / 1000, meta.GetProperty("maxSendable").GetInt64() / 1000);
    }

    private static string MetadataUrl(string lightningAddress)
    {
        var parts = lightningAddress.Split('@');
        if (parts.Length != 2)
            throw new ArgumentException($"'{lightningAddress}' is not a Lightning Address.");
        return $"https://{parts[1]}/.well-known/lnurlp/{Uri.EscapeDataString(parts[0])}";
    }
}
