using BTCPayServer.Lightning;
using NBitcoin;

namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>
/// Changing a store's address must not strand invoices already in flight,
/// and new invoices must come from the new address.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "ReleaseGate")]
public sealed class AddressChangeTests
{
    private readonly GateFixture _gate;

    public AddressChangeTests(GateFixture gate) => _gate = gate;

    [Fact]
    public async Task Old_invoice_still_settles_while_new_invoices_use_the_new_address()
    {
        var first = GateConfig.LightningAddress;
        var second = GateConfig.SecondLightningAddress;
        Assert.NotEqual(first, second);

        // Without this the description-hash assertions below would be vacuous if the server ever
        // served identical metadata for different addresses.
        var firstHash = await LnurlDirectory.GetMetadataHashAsync(first);
        var secondHash = await LnurlDirectory.GetMetadataHashAsync(second);
        Assert.NotEqual(firstHash, secondHash);

        var store = await _gate.Greenfield.CreateStoreAsync("gate-address-change");
        await _gate.Greenfield.SetCoinsnapLightningAsync(store, first);

        var oldInvoice = await _gate.Greenfield.CreateLightningInvoiceAsync(store, satoshis: 160);
        var oldBolt11 = await _gate.Greenfield.GetLightningDestinationAsync(store, oldInvoice);

        // The description hash commits to the LNURL metadata, which names the address. On regtest
        // both addresses reach one node, so this is the only honest proof of which minted it.
        Assert.Equal(firstHash, DescriptionHashOf(oldBolt11));

        await _gate.Greenfield.SetCoinsnapLightningAsync(store, second);

        var newInvoice = await _gate.Greenfield.CreateLightningInvoiceAsync(store, satoshis: 170);
        var newBolt11 = await _gate.Greenfield.GetLightningDestinationAsync(store, newInvoice);

        Assert.Equal(secondHash, DescriptionHashOf(newBolt11));

        // The in-flight invoice was minted by the old address and must still settle.
        await _gate.Payer.PayAsync(oldBolt11);
        Assert.Equal("Settled", await _gate.Greenfield.WaitForStatusAsync(
            store, oldInvoice, "Settled", TimeSpan.FromMinutes(2)));

        await _gate.Payer.PayAsync(newBolt11);
        Assert.Equal("Settled", await _gate.Greenfield.WaitForStatusAsync(
            store, newInvoice, "Settled", TimeSpan.FromMinutes(2)));

        Assert.Equal(1, await _gate.Greenfield.CountPaymentsAsync(store, oldInvoice));
        Assert.Equal(1, await _gate.Greenfield.CountPaymentsAsync(store, newInvoice));
    }

    private static string DescriptionHashOf(string bolt11) =>
        BOLT11PaymentRequest.Parse(bolt11, Network.RegTest)
            .DescriptionHash!.ToString().ToLowerInvariant();
}
