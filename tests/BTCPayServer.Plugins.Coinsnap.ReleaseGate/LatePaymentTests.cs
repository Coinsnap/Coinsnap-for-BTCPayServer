using BTCPayServer.Lightning;
using NBitcoin;

namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>
/// The plugin only accepts a BOLT11 that expires with BTCPay's invoice (audit finding A). A BOLT11
/// that outlived it could be paid after BTCPay stopped watching, and the sats would arrive with no
/// BTCPay record; now a late payment is refused by the network instead.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "ReleaseGate")]
public sealed class LatePaymentTests
{
    private static readonly TimeSpan InvoiceExpiry = TimeSpan.FromSeconds(60);

    private readonly GateFixture _gate;

    public LatePaymentTests(GateFixture gate) => _gate = gate;

    [Fact]
    public async Task The_bolt11_expires_with_the_invoice_so_a_late_payment_is_refused()
    {
        var store = await _gate.Greenfield.CreateStoreAsync(
            "gate-late-payment", invoiceExpiration: InvoiceExpiry);
        await _gate.Greenfield.SetCoinsnapLightningAsync(store, GateConfig.LightningAddress);

        var invoice = await _gate.Greenfield.CreateLightningInvoiceAsync(store, satoshis: 180);
        var bolt11 = await _gate.Greenfield.GetLightningDestinationAsync(store, invoice);
        var bolt11Expiry = BOLT11PaymentRequest.Parse(bolt11, Network.RegTest).ExpiryDate;

        // Within the plugin's two-minute tolerance of BTCPay's own expiry, never hours past it.
        Assert.True(bolt11Expiry <= DateTimeOffset.UtcNow + InvoiceExpiry + TimeSpan.FromMinutes(2),
            $"The BOLT11 expires at {bolt11Expiry:O}, long after the invoice.");

        Assert.Equal("Expired", await _gate.Greenfield.WaitForStatusAsync(
            store, invoice, "Expired", InvoiceExpiry + TimeSpan.FromMinutes(1)));
        var wait = bolt11Expiry - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _gate.Payer.PayAsync(bolt11));
        Assert.Equal(0, await _gate.Greenfield.CountPaymentsAsync(store, invoice));
        Assert.Equal("Expired", await _gate.Greenfield.GetStatusAsync(store, invoice));
    }
}
