using System.Text.Json;
using BTCPayServer.Lightning;
using NBitcoin;

namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>
/// BTCPay's own LNURL features (the checkout's LNURL and the store's Lightning Address) create their
/// BOLT11 through the plugin too. A wallet pays only if the BOLT11's description hash matches the
/// metadata it was shown, and the plugin swaps BTCPay's metadata for Coinsnap's, so check that here.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "ReleaseGate")]
public sealed class BtcpayLnurlTests(GateFixture gate)
{
    private readonly HttpClient _public = new() { BaseAddress = gate.Host.BaseAddress };

    [Fact]
    public async Task Paying_through_the_checkout_lnurl_settles_the_invoice()
    {
        var store = await gate.Greenfield.CreateStoreAsync("gate-lnurl-checkout");
        await gate.Greenfield.SetCoinsnapLightningAsync(store, GateConfig.LightningAddress);
        await gate.Greenfield.EnableLnurlAsync(store);
        var invoice = await gate.Greenfield.CreateInvoiceAsync(store, "0.00000150", "BTC", "BTC-LN", "BTC-LNURL");

        var payRequest = await GetJsonAsync($"BTC/lnurl/pay/i/{invoice}");
        Assert.Equal("payRequest", payRequest.GetProperty("tag").GetString());
        Assert.Equal(150_000, payRequest.GetProperty("minSendable").GetInt64());

        var bolt11 = await RequestInvoiceAsync(payRequest, 150_000);
        await PayAndExpectSettledAsync(store, invoice, bolt11);
    }

    [Fact]
    public async Task Paying_the_stores_lightning_address_creates_and_settles_an_invoice()
    {
        var store = await gate.Greenfield.CreateStoreAsync("gate-lnurl-address");
        await gate.Greenfield.SetCoinsnapLightningAsync(store, GateConfig.LightningAddress);
        await gate.Greenfield.EnableLnurlAsync(store);
        var username = $"gate{Guid.NewGuid():N}"[..16];
        await gate.Greenfield.AddLightningAddressAsync(store, username);

        var payRequest = await GetJsonAsync($".well-known/lnurlp/{username}");
        Assert.Equal("payRequest", payRequest.GetProperty("tag").GetString());

        var bolt11 = await RequestInvoiceAsync(payRequest, 120_000);
        var invoice = Assert.Single(await gate.Greenfield.ListInvoicesAsync(store)).Id;
        await PayAndExpectSettledAsync(store, invoice, bolt11);
    }

    private async Task<string> RequestInvoiceAsync(JsonElement payRequest, long amountMsat)
    {
        var metadata = payRequest.GetProperty("metadata").GetString()!;
        var callback = payRequest.GetProperty("callback").GetString()!;
        var separator = callback.Contains('?') ? '&' : '?';
        var response = await GetJsonAsync($"{callback}{separator}amount={amountMsat}");

        Assert.False(response.TryGetProperty("status", out var status) && status.GetString() == "ERROR",
            $"LNURL callback refused: {response}");
        var bolt11 = response.GetProperty("pr").GetString()!;
        var parsed = BOLT11PaymentRequest.Parse(bolt11, Network.RegTest);
        Assert.Equal(amountMsat, parsed.MinimumAmount.MilliSatoshi);
        Assert.True(parsed.VerifyDescriptionHash(metadata),
            "The BOLT11's description hash does not match the LNURL metadata, so wallets would refuse to pay.");
        return bolt11;
    }

    private async Task PayAndExpectSettledAsync(string store, string invoice, string bolt11)
    {
        await gate.Payer.PayAsync(bolt11);
        Assert.Equal("Settled", await gate.Greenfield.WaitForStatusAsync(store, invoice, "Settled", TimeSpan.FromMinutes(2)));
        Assert.Equal(1, await gate.Greenfield.CountAllPaymentsAsync(store, invoice));
    }

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        var body = await _public.GetStringAsync(url);
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
