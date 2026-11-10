using BTCPayServer.Lightning;
using NBitcoin;

namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

[Collection(GateCollection.Name)]
[Trait("Category", "ReleaseGate")]
public sealed class PaymentLifecycleTests
{
    private readonly GateFixture _gate;

    public PaymentLifecycleTests(GateFixture gate) => _gate = gate;

    [Fact]
    public async Task Invoice_paid_from_the_cluster_settles_in_btcpay()
    {
        var store = await _gate.Greenfield.CreateStoreAsync("gate-lifecycle");
        await _gate.Greenfield.SetCoinsnapLightningAsync(store, GateConfig.LightningAddress);

        var invoice = await _gate.Greenfield.CreateLightningInvoiceAsync(store, satoshis: 100);
        var bolt11 = await _gate.Greenfield.GetLightningDestinationAsync(store, invoice);

        var parsed = BOLT11PaymentRequest.Parse(bolt11, Network.RegTest);
        Assert.Equal(100_000, parsed.MinimumAmount.MilliSatoshi);

        var payment = await _gate.Payer.PayAsync(bolt11);
        Assert.Equal(parsed.PaymentHash!.ToString(), payment.PaymentHash);

        var status = await _gate.Greenfield.WaitForStatusAsync(
            store, invoice, "Settled", TimeSpan.FromMinutes(2));

        Assert.Equal("Settled", status);
    }

    [Fact]
    public async Task Malformed_address_is_rejected_before_a_store_can_use_it()
    {
        var store = await _gate.Greenfield.CreateStoreAsync("gate-validation");

        var error = await Assert.ThrowsAsync<GreenfieldValidationException>(
            () => _gate.Greenfield.SetCoinsnapLightningAsync(store, "not-an-address"));

        Assert.Contains("Lightning Address", error.Message);
    }

    [Fact]
    public async Task Address_outside_the_allowlisted_domain_is_rejected()
    {
        var store = await _gate.Greenfield.CreateStoreAsync("gate-domain");

        var error = await Assert.ThrowsAsync<GreenfieldValidationException>(
            () => _gate.Greenfield.SetCoinsnapLightningAsync(store, "bob@evil.example.com"));

        Assert.Contains(GateConfig.DevHost, error.Message);
    }
}
