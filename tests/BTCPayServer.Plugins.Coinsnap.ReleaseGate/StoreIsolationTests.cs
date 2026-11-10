namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>
/// Two stores may share one Coinsnap address, and neither may ever see or be
/// credited with the other's invoice. A regression here would be silent and would misdirect money.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "ReleaseGate")]
public sealed class StoreIsolationTests
{
    private readonly GateFixture _gate;

    public StoreIsolationTests(GateFixture gate) => _gate = gate;

    [Fact]
    public async Task Paying_one_store_never_credits_another_sharing_the_same_address()
    {
        var address = GateConfig.LightningAddress;

        var storeA = await _gate.Greenfield.CreateStoreAsync("gate-isolation-a");
        await _gate.Greenfield.SetCoinsnapLightningAsync(storeA, address);
        var storeB = await _gate.Greenfield.CreateStoreAsync("gate-isolation-b");
        await _gate.Greenfield.SetCoinsnapLightningAsync(storeB, address);

        var invoiceA = await _gate.Greenfield.CreateLightningInvoiceAsync(storeA, satoshis: 140);
        var invoiceB = await _gate.Greenfield.CreateLightningInvoiceAsync(storeB, satoshis: 150);

        var bolt11A = await _gate.Greenfield.GetLightningDestinationAsync(storeA, invoiceA);
        var bolt11B = await _gate.Greenfield.GetLightningDestinationAsync(storeB, invoiceB);
        Assert.NotEqual(bolt11A, bolt11B);

        await _gate.Payer.PayAsync(bolt11A);

        Assert.Equal("Settled", await _gate.Greenfield.WaitForStatusAsync(
            storeA, invoiceA, "Settled", TimeSpan.FromMinutes(2)));

        // Watch B over a window rather than sampling once, so this cannot pass by racing.
        Assert.Equal("New", await _gate.Greenfield.WatchForDeviationAsync(
            storeB, invoiceB, "New", TimeSpan.FromSeconds(20)));
        Assert.Equal(0, await _gate.Greenfield.CountPaymentsAsync(storeB, invoiceB));

        // B must be isolated, not broken: it still settles on its own payment.
        await _gate.Payer.PayAsync(bolt11B);
        Assert.Equal("Settled", await _gate.Greenfield.WaitForStatusAsync(
            storeB, invoiceB, "Settled", TimeSpan.FromMinutes(2)));

        Assert.Equal(1, await _gate.Greenfield.CountPaymentsAsync(storeA, invoiceA));
        Assert.Equal(1, await _gate.Greenfield.CountPaymentsAsync(storeB, invoiceB));
    }
}
