namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>
/// The dev host goes dark (the fault proxy holds every request unanswered) while a customer pays. The payment must not be recorded on faith, pending state must survive a
/// restart, and polling must pick the payment up within its backoff cap once the host returns.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "ReleaseGate")]
public sealed class OutageRecoveryTests
{
    private static readonly TimeSpan OutageObservation = TimeSpan.FromSeconds(45);

    // The poller backs off to at most 2 minutes, plus jitter and BTCPay's own processing.
    private static readonly TimeSpan RecoveryBound = TimeSpan.FromMinutes(4);

    private readonly GateFixture _gate;

    public OutageRecoveryTests(GateFixture gate) => _gate = gate;

    [Fact]
    public async Task Payment_made_during_an_outage_settles_once_after_the_host_returns()
    {
        var store = await _gate.Greenfield.CreateStoreAsync("gate-outage");
        await _gate.Greenfield.SetCoinsnapLightningAsync(store, GateConfig.LightningAddress);

        var invoice = await _gate.Greenfield.CreateLightningInvoiceAsync(store, satoshis: 150);
        var bolt11 = await _gate.Greenfield.GetLightningDestinationAsync(store, invoice);

        _gate.Proxy.BeginDrill();
        _gate.Proxy.Inject(new { target = "*", blackhole = true });
        try
        {
            await _gate.Proxy.WaitUntilFaultedAsync(bolt11, DateTimeOffset.UtcNow.AddSeconds(-1));
            await _gate.Payer.PayAsync(bolt11);

            // The sats arrived, but the plugin cannot prove it yet, so nothing may be recorded.
            await Task.Delay(OutageObservation);
            Assert.Equal("New", await _gate.Greenfield.GetStatusAsync(store, invoice));
            Assert.Equal(0, await _gate.Greenfield.CountPaymentsAsync(store, invoice));

            // Pending state has to be durable, not just held in the poller's memory.
            var pidBefore = _gate.Host.ProcessId;
            await _gate.Host.RestartAsync();
            Assert.NotEqual(pidBefore, _gate.Host.ProcessId);
            Assert.Equal("New", await _gate.Greenfield.GetStatusAsync(store, invoice));
        }
        finally
        {
            _gate.Proxy.Clear();
        }

        Assert.Equal("Settled", await _gate.Greenfield.WaitForStatusAsync(
            store, invoice, "Settled", RecoveryBound));
        Assert.Equal(1, await _gate.Greenfield.CountPaymentsAsync(store, invoice));
    }
}
