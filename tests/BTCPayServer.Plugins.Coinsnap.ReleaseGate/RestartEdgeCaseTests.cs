using BTCPayServer.Lightning;
using NBitcoin;

namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

[Collection(GateCollection.Name)]
[Trait("Category", "ReleaseGate")]
public sealed class RestartEdgeCaseTests
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromMinutes(3);

    private readonly GateFixture _gate;

    public RestartEdgeCaseTests(GateFixture gate) => _gate = gate;

    [Fact]
    public async Task Payment_completing_while_btcpay_is_down_is_recorded_once_it_returns()
    {
        var store = await _gate.Greenfield.CreateStoreAsync("gate-restart-midflight");
        await _gate.Greenfield.SetCoinsnapLightningAsync(store, GateConfig.LightningAddress);
        var invoice = await _gate.Greenfield.CreateLightningInvoiceAsync(store, satoshis: 230);
        var bolt11 = await _gate.Greenfield.GetLightningDestinationAsync(store, invoice);

        // Kill BTCPay the moment the payment leaves, so it settles while nothing is listening.
        var payment = _gate.Payer.PayAsync(bolt11);
        await _gate.Host.StopAsync();
        await payment;
        await _gate.Host.StartAsync();

        Assert.Equal("Settled", await _gate.Greenfield.WaitForStatusAsync(store, invoice, "Settled", SettleTimeout));
        Assert.Equal(1, await _gate.Greenfield.CountPaymentsAsync(store, invoice));
    }

    [Fact]
    public async Task Back_to_back_restarts_keep_one_bolt11_and_one_payment()
    {
        var store = await _gate.Greenfield.CreateStoreAsync("gate-restart-repeat");
        await _gate.Greenfield.SetCoinsnapLightningAsync(store, GateConfig.LightningAddress);
        var invoice = await _gate.Greenfield.CreateLightningInvoiceAsync(store, satoshis: 240);
        var bolt11 = await _gate.Greenfield.GetLightningDestinationAsync(store, invoice);

        for (var i = 0; i < 3; i++)
            await _gate.Host.RestartAsync();

        Assert.Equal(bolt11, await _gate.Greenfield.GetLightningDestinationAsync(store, invoice));
        Assert.Equal("New", await _gate.Greenfield.GetStatusAsync(store, invoice));

        await _gate.Payer.PayAsync(bolt11);
        Assert.Equal("Settled", await _gate.Greenfield.WaitForStatusAsync(store, invoice, "Settled", SettleTimeout));

        for (var i = 0; i < 2; i++)
            await _gate.Host.RestartAsync();

        Assert.Equal("Settled", await _gate.Greenfield.GetStatusAsync(store, invoice));
        Assert.Equal(1, await _gate.Greenfield.CountPaymentsAsync(store, invoice));
    }

    [Fact]
    public async Task Restart_right_after_an_address_change_keeps_both_addresses_straight()
    {
        var first = GateConfig.LightningAddress;
        var second = GateConfig.SecondLightningAddress;
        var secondHash = await LnurlDirectory.GetMetadataHashAsync(second);

        var store = await _gate.Greenfield.CreateStoreAsync("gate-restart-address");
        await _gate.Greenfield.SetCoinsnapLightningAsync(store, first);
        var oldInvoice = await _gate.Greenfield.CreateLightningInvoiceAsync(store, satoshis: 250);
        var oldBolt11 = await _gate.Greenfield.GetLightningDestinationAsync(store, oldInvoice);

        await _gate.Greenfield.SetCoinsnapLightningAsync(store, second);
        await _gate.Host.RestartAsync();

        var newInvoice = await _gate.Greenfield.CreateLightningInvoiceAsync(store, satoshis: 260);
        var newBolt11 = await _gate.Greenfield.GetLightningDestinationAsync(store, newInvoice);
        Assert.Equal(secondHash,
            BOLT11PaymentRequest.Parse(newBolt11, Network.RegTest).DescriptionHash!.ToString().ToLowerInvariant());

        await _gate.Payer.PayAsync(oldBolt11);
        await _gate.Payer.PayAsync(newBolt11);
        Assert.Equal("Settled", await _gate.Greenfield.WaitForStatusAsync(store, oldInvoice, "Settled", SettleTimeout));
        Assert.Equal("Settled", await _gate.Greenfield.WaitForStatusAsync(store, newInvoice, "Settled", SettleTimeout));
        Assert.Equal(1, await _gate.Greenfield.CountPaymentsAsync(store, oldInvoice));
        Assert.Equal(1, await _gate.Greenfield.CountPaymentsAsync(store, newInvoice));
    }
}
