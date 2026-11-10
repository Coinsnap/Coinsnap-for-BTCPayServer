using BTCPayServer.Lightning;
using NBitcoin;

namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>
/// An invoice must survive a restart while still payable, and a paid
/// invoice must stay paid exactly once. This is the part hand-testing skips.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "ReleaseGate")]
public sealed class RestartRecoveryTests
{
    private readonly GateFixture _gate;

    public RestartRecoveryTests(GateFixture gate) => _gate = gate;

    [Fact]
    public async Task Unpaid_invoice_keeps_its_bolt11_across_a_restart_and_still_settles()
    {
        var store = await _gate.Greenfield.CreateStoreAsync("gate-restart-unpaid");
        await _gate.Greenfield.SetCoinsnapLightningAsync(store, GateConfig.LightningAddress);

        var invoice = await _gate.Greenfield.CreateLightningInvoiceAsync(store, satoshis: 120);
        var before = await _gate.Greenfield.GetLightningDestinationAsync(store, invoice);

        // Assert the restart actually happened; otherwise a no-op RestartAsync would leave these
        // tests green while proving nothing.
        var pidBefore = _gate.Host.ProcessId;
        await _gate.Host.RestartAsync();
        Assert.NotEqual(pidBefore, _gate.Host.ProcessId);

        var after = await _gate.Greenfield.GetLightningDestinationAsync(store, invoice);
        Assert.Equal(before, after);

        var hash = BOLT11PaymentRequest.Parse(after, Network.RegTest).PaymentHash!.ToString();
        Assert.Equal("New", await _gate.Greenfield.GetStatusAsync(store, invoice));

        var payment = await _gate.Payer.PayAsync(after);
        Assert.Equal(hash, payment.PaymentHash);

        Assert.Equal("Settled", await _gate.Greenfield.WaitForStatusAsync(
            store, invoice, "Settled", TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public async Task Paid_invoice_stays_paid_exactly_once_across_a_restart()
    {
        var store = await _gate.Greenfield.CreateStoreAsync("gate-restart-paid");
        await _gate.Greenfield.SetCoinsnapLightningAsync(store, GateConfig.LightningAddress);

        var invoice = await _gate.Greenfield.CreateLightningInvoiceAsync(store, satoshis: 130);
        var bolt11 = await _gate.Greenfield.GetLightningDestinationAsync(store, invoice);

        await _gate.Payer.PayAsync(bolt11);
        Assert.Equal("Settled", await _gate.Greenfield.WaitForStatusAsync(
            store, invoice, "Settled", TimeSpan.FromMinutes(2)));
        Assert.Equal(1, await _gate.Greenfield.CountPaymentsAsync(store, invoice));

        var pidBefore = _gate.Host.ProcessId;
        await _gate.Host.RestartAsync();
        Assert.NotEqual(pidBefore, _gate.Host.ProcessId);

        Assert.Equal("Settled", await _gate.Greenfield.GetStatusAsync(store, invoice));
        Assert.Equal(1, await _gate.Greenfield.CountPaymentsAsync(store, invoice));
    }
}
