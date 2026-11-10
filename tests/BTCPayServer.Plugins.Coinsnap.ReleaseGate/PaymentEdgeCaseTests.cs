using BTCPayServer.Lightning;
using NBitcoin;

namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

[Collection(GateCollection.Name)]
[Trait("Category", "ReleaseGate")]
public sealed class PaymentEdgeCaseTests
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromMinutes(3);

    private readonly GateFixture _gate;

    public PaymentEdgeCaseTests(GateFixture gate) => _gate = gate;

    [Fact]
    public async Task Invoices_paid_at_the_same_time_each_settle_exactly_once()
    {
        const int count = 10;
        var store = await _gate.Greenfield.CreateStoreAsync("gate-concurrent");
        await _gate.Greenfield.SetCoinsnapLightningAsync(store, GateConfig.LightningAddress);

        var invoices = new List<(string Id, string Bolt11)>();
        for (var i = 0; i < count; i++)
        {
            // Distinct amounts, so a payment credited to the wrong invoice cannot go unnoticed.
            var id = await _gate.Greenfield.CreateLightningInvoiceAsync(store, satoshis: 200 + i);
            invoices.Add((id, await _gate.Greenfield.GetLightningDestinationAsync(store, id)));
        }
        Assert.Equal(count, invoices.Select(i => i.Bolt11).Distinct().Count());

        await Task.WhenAll(invoices.Select(i => _gate.Payer.PayAsync(i.Bolt11)));

        foreach (var (id, _) in invoices)
        {
            Assert.Equal("Settled", await _gate.Greenfield.WaitForStatusAsync(store, id, "Settled", SettleTimeout));
            Assert.Equal(1, await _gate.Greenfield.CountPaymentsAsync(store, id));
        }
    }

    [Fact]
    public async Task Amounts_at_and_above_the_lnurl_maximum()
    {
        var (_, maxSat) = await LnurlDirectory.GetSendableAsync(GateConfig.LightningAddress);
        var store = await _gate.Greenfield.CreateStoreAsync("gate-bounds");
        await _gate.Greenfield.SetCoinsnapLightningAsync(store, GateConfig.LightningAddress);

        var atMax = await _gate.Greenfield.CreateLightningInvoiceAsync(store, maxSat);
        var bolt11 = await _gate.Greenfield.GetLightningDestinationAsync(store, atMax);
        Assert.Equal(maxSat * 1000, BOLT11PaymentRequest.Parse(bolt11, Network.RegTest).MinimumAmount.MilliSatoshi);

        string? aboveMax = null;
        try
        {
            var invoice = await _gate.Greenfield.CreateLightningInvoiceAsync(store, maxSat + 1);
            aboveMax = await _gate.Greenfield.TryGetLightningDestinationAsync(store, invoice);
        }
        catch (GreenfieldValidationException)
        {
        }
        Assert.Null(aboveMax);
    }

    [Theory]
    [InlineData("EUR", "0.10")]
    [InlineData("USD", "0.13")]
    public async Task Fiat_invoice_gets_a_payable_bolt11_and_settles(string currency, string amount)
    {
        var store = await _gate.Greenfield.CreateStoreAsync($"gate-fiat-{currency}");
        await _gate.Greenfield.SetCoinsnapLightningAsync(store, GateConfig.LightningAddress);

        // A converted amount is rarely a whole number of satoshis; the plugin requires one.
        var invoice = await _gate.Greenfield.CreateInvoiceAsync(store, amount, currency);
        var bolt11 = await _gate.Greenfield.GetLightningDestinationAsync(store, invoice);
        Assert.Equal(0, BOLT11PaymentRequest.Parse(bolt11, Network.RegTest).MinimumAmount.MilliSatoshi % 1000);

        await _gate.Payer.PayAsync(bolt11);
        Assert.Equal("Settled", await _gate.Greenfield.WaitForStatusAsync(store, invoice, "Settled", SettleTimeout));
        Assert.Equal(1, await _gate.Greenfield.CountPaymentsAsync(store, invoice));
    }

    [Fact]
    public async Task Paying_an_invoice_the_merchant_invalidated_does_not_revive_it()
    {
        var store = await _gate.Greenfield.CreateStoreAsync("gate-invalidated");
        await _gate.Greenfield.SetCoinsnapLightningAsync(store, GateConfig.LightningAddress);

        var invoice = await _gate.Greenfield.CreateLightningInvoiceAsync(store, satoshis: 190);
        var bolt11 = await _gate.Greenfield.GetLightningDestinationAsync(store, invoice);
        await _gate.Greenfield.MarkInvalidAsync(store, invoice);

        // The BOLT11 is still live at the server, so a customer can still pay it.
        await _gate.Payer.PayAsync(bolt11);

        Assert.Equal("Invalid", await _gate.Greenfield.WatchForDeviationAsync(
            store, invoice, "Invalid", TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Deleting_a_store_with_a_pending_invoice_leaves_others_working_across_a_restart()
    {
        var doomed = await _gate.Greenfield.CreateStoreAsync("gate-deleted");
        await _gate.Greenfield.SetCoinsnapLightningAsync(doomed, GateConfig.LightningAddress);
        var orphan = await _gate.Greenfield.CreateLightningInvoiceAsync(doomed, satoshis: 210);
        var orphanBolt11 = await _gate.Greenfield.GetLightningDestinationAsync(doomed, orphan);

        await _gate.Greenfield.DeleteStoreAsync(doomed);
        await _gate.Payer.PayAsync(orphanBolt11);

        // The plugin still tracks the orphan; it must not break startup or other stores.
        var pidBefore = _gate.Host.ProcessId;
        await _gate.Host.RestartAsync();
        Assert.NotEqual(pidBefore, _gate.Host.ProcessId);

        var survivor = await _gate.Greenfield.CreateStoreAsync("gate-survivor");
        await _gate.Greenfield.SetCoinsnapLightningAsync(survivor, GateConfig.LightningAddress);
        var invoice = await _gate.Greenfield.CreateLightningInvoiceAsync(survivor, satoshis: 220);
        await _gate.Payer.PayAsync(await _gate.Greenfield.GetLightningDestinationAsync(survivor, invoice));

        Assert.Equal("Settled", await _gate.Greenfield.WaitForStatusAsync(survivor, invoice, "Settled", SettleTimeout));
        Assert.Equal(1, await _gate.Greenfield.CountPaymentsAsync(survivor, invoice));
    }
}
