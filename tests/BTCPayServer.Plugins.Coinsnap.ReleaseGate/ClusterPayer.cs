using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

public sealed record PaymentResult(string PaymentHash, string PreimageHex);

/// <summary>
/// Pays a BOLT11 from a node on the regtest cluster. The plugin is receive-only, so the gate
/// needs an independent payer to close the loop.
/// </summary>
public sealed class ClusterPayer : IDisposable
{
    private readonly HttpClient _http;

    public ClusterPayer()
    {
        // The cluster nodes serve LND's self-signed certificate.
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(GateConfig.LndRestUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(2)
        };
        _http.DefaultRequestHeaders.Add("Grpc-Metadata-macaroon", GateConfig.LndMacaroonHex);
    }

    public async Task<PaymentResult> PayAsync(string bolt11)
    {
        var body = new { payment_request = bolt11, fee_limit = new { fixed_ = "50" } };

        // LND's REST field is literally "fixed", which is not a usable C# identifier.
        var json = JsonSerializer.Serialize(body).Replace("\"fixed_\"", "\"fixed\"");
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var response = await _http.PostAsync("v1/channels/transactions", content);
        var payload = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"LND returned {(int)response.StatusCode}: {payload}");

        var result = JsonDocument.Parse(payload).RootElement;

        var error = result.TryGetProperty("payment_error", out var e) ? e.GetString() : null;
        if (!string.IsNullOrEmpty(error))
            throw new InvalidOperationException($"Payment failed: {error}");

        var preimage = Convert.FromBase64String(result.GetProperty("payment_preimage").GetString()!);
        var preimageHex = Convert.ToHexString(preimage).ToLowerInvariant();
        var paymentHash = Convert.ToHexString(SHA256.HashData(preimage)).ToLowerInvariant();

        return new PaymentResult(paymentHash, preimageHex);
    }

    /// <summary>
    /// LND refuses to pay its own invoices, so a dev host that mints on the paying node would fail
    /// every payment test one by one. Catch that once, before BTCPay even starts.
    /// </summary>
    public async Task EnsureNotPayeeOfAsync(string bolt11)
    {
        var payer = JsonDocument.Parse(await _http.GetStringAsync("v1/getinfo"))
            .RootElement.GetProperty("identity_pubkey").GetString();
        var payee = JsonDocument.Parse(await _http.GetStringAsync($"v1/payreq/{bolt11}"))
            .RootElement.GetProperty("destination").GetString();
        if (string.Equals(payer, payee, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The dev host mints its invoices on the paying node ({payer}), and LND refuses self-payments. " +
                $"Point COINSNAP_GATE_LND_URL and COINSNAP_GATE_LND_MACAROON at another node, or move the " +
                $"dev host's Lightning Address service to a different node.");
    }

    public void Dispose() => _http.Dispose();
}
