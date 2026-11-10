namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>
/// Failure drills against a real BTCPay: the dev host misbehaves while a paid invoice
/// waits for proof. Nothing may be recorded on a bad answer, and once the host answers honestly
/// the payment must be recorded exactly once.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "ReleaseGate")]
public sealed class FaultDrillTests
{
    private static readonly TimeSpan FaultWindow = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan RecoveryBound = TimeSpan.FromMinutes(4);
    private const string ForgedPreimage = "1111111111111111111111111111111111111111111111111111111111111111";

    private readonly GateFixture _gate;

    public FaultDrillTests(GateFixture gate) => _gate = gate;

    // Both the settlement poller and BTCPay's own GetInvoice checks call verify, so hits come from
    // two callers and the assertions below allow for that.
    [Fact]
    public Task Http_429_is_retried_no_sooner_than_retry_after() =>
        DrillAsync("verify-429",
            new { target = "verify", status = 429, headers = new Dictionary<string, string> { ["Retry-After"] = "15" } },
            hits =>
            {
                // Honouring a 15 s Retry-After leaves at most one hit per caller in any 10 s span.
                var busiest = hits.Max(h => hits.Count(o => o.At >= h.At && o.At < h.At + TimeSpan.FromSeconds(10)));
                Assert.True(busiest <= 2, $"{busiest} requests within 10 s despite Retry-After: {FaultProxy.Describe(hits)}");
            });

    [Fact]
    public Task Http_5xx_backs_off() =>
        DrillAsync("verify-503", new { target = "verify", status = 503, body = "" },
            hits =>
            {
                var midpoint = hits[0].At + FaultWindow / 2;
                var early = hits.Count(h => h.At < midpoint);
                Assert.True(early > hits.Count - early, $"Retries did not slow down: {FaultProxy.Describe(hits)}");
            });

    [Fact]
    public Task Invalid_json_is_not_trusted() =>
        DrillAsync("verify-bad-json", new { target = "verify", status = 200, body = "{\"status\":\"OK\",\"settled\":tru" });

    [Fact]
    public Task Slow_response_times_out_and_is_retried() =>
        DrillAsync("verify-timeout", new { target = "verify", delay = 30 });

    [Fact]
    public Task Settled_claim_with_a_forged_preimage_is_rejected() =>
        DrillAsync("verify-forged-preimage",
            new { target = "verify", rewrite = new { settled = true, preimage = ForgedPreimage } });

    [Fact]
    public async Task Settled_claim_for_a_different_bolt11_is_rejected()
    {
        // Minted before the proxy is up: the test process does not trust the gate CA.
        var decoy = await LnurlDirectory.MintAsync(GateConfig.LightningAddress, 140);
        await DrillAsync("verify-mismatched-bolt11",
            new { target = "verify", rewrite = new { settled = true, preimage = ForgedPreimage, pr = decoy } });
    }

    [Fact]
    public Task Not_found_below_the_pruning_threshold_keeps_the_invoice() =>
        DrillAsync("verify-not-found",
            new { target = "verify", status = 200, body = "{\"status\":\"ERROR\",\"reason\":\"Not found\"}" },
            // Not found never drops a payable invoice, so the payment must still settle afterwards.
            window: TimeSpan.FromSeconds(35));

    [Fact]
    public Task Callback_error_leaves_no_lightning_method() =>
        CreationDrillAsync("callback-500", "callback", new { target = "callback", status = 500, body = "" });

    [Fact]
    public async Task Callback_bolt11_for_another_amount_is_rejected()
    {
        var decoy = await LnurlDirectory.MintAsync(GateConfig.LightningAddress, 999);
        await CreationDrillAsync("callback-wrong-amount", "callback", new { target = "callback", rewrite = new { pr = decoy } });
    }

    [Fact]
    public Task Callback_verify_url_on_another_host_is_rejected() =>
        CreationDrillAsync("callback-foreign-verify", "callback",
            new { target = "callback", rewrite = new { verify = "https://evil.example.com/verify/x" } });

    [Fact]
    public Task Metadata_bounds_that_exclude_the_amount_are_rejected() =>
        CreationDrillAsync("metadata-bounds", "metadata",
            new { target = "metadata", rewrite = new { minSendable = 200_000_000, maxSendable = 250_000_000 } });

    private async Task DrillAsync(
        string name, object fault, Action<IReadOnlyList<ProxyHit>>? inspect = null, TimeSpan? window = null)
    {
        var store = await _gate.Greenfield.CreateStoreAsync($"gate-drill-{name}");
        await _gate.Greenfield.SetCoinsnapLightningAsync(store, GateConfig.LightningAddress);

        var proxy = _gate.Proxy;
        proxy.BeginDrill();
        try
        {
            await RunDrillAsync(proxy, store, fault, inspect, window);
        }
        finally
        {
            // A failed drill must not leave its fault on for the tests after it.
            proxy.Clear();
        }
    }

    private async Task RunDrillAsync(
        FaultProxy proxy, string store, object fault, Action<IReadOnlyList<ProxyHit>>? inspect, TimeSpan? window)
    {
        var invoice = await _gate.Greenfield.CreateLightningInvoiceAsync(store, satoshis: 140);
        var bolt11 = await _gate.Greenfield.GetLightningDestinationAsync(store, invoice);

        proxy.Inject(fault);
        var faultedFrom = DateTimeOffset.UtcNow;
        await proxy.WaitUntilFaultedAsync(bolt11, faultedFrom);
        await _gate.Payer.PayAsync(bolt11);

        var during = await _gate.Greenfield.WatchForDeviationAsync(store, invoice, "New", window ?? FaultWindow);
        if (during != "New")
            Assert.Fail(
                $"Invoice became {during} while faulted. Proxy log since the fault: " +
                FaultProxy.Describe(proxy.VerifiesOf(bolt11).Where(h => h.At >= faultedFrom).ToList()) +
                $"\nProxy output:\n{await proxy.LogsAsync()}");
        Assert.Equal(0, await _gate.Greenfield.CountPaymentsAsync(store, invoice));

        // Every pending invoice is polled through the proxy; only this one's requests count.
        var faulted = proxy.VerifiesOf(bolt11).Where(h => h.At >= faultedFrom && h.Faulted).ToList();
        Assert.True(faulted.Count > 0, "The poller never met the fault, so the drill proved nothing.");
        inspect?.Invoke(faulted);

        proxy.Clear();
        Assert.Equal("Settled", await _gate.Greenfield.WaitForStatusAsync(store, invoice, "Settled", RecoveryBound));
        Assert.Equal(1, await _gate.Greenfield.CountPaymentsAsync(store, invoice));
    }

    private async Task CreationDrillAsync(string name, string target, object fault)
    {
        var store = await _gate.Greenfield.CreateStoreAsync($"gate-drill-{name}");
        // Its own address, so metadata the plugin cached for another test cannot hide the fault.
        await _gate.Greenfield.SetCoinsnapLightningAsync(store, $"drill-{name}@{GateConfig.DevHost}");

        var proxy = _gate.Proxy;
        proxy.BeginDrill();
        var since = DateTimeOffset.UtcNow;
        proxy.Inject(fault);
        try
        {
            await RunCreationDrillAsync(proxy, store, target, since);
        }
        finally
        {
            proxy.Clear();
        }
    }

    private async Task RunCreationDrillAsync(FaultProxy proxy, string store, string target, DateTimeOffset since)
    {
        string? destination = null;
        try
        {
            var invoice = await _gate.Greenfield.CreateLightningInvoiceAsync(store, satoshis: 140);
            destination = await _gate.Greenfield.TryGetLightningDestinationAsync(store, invoice);
        }
        catch (GreenfieldValidationException)
        {
            // Refusing the invoice outright is as good as creating it without Lightning.
        }
        Assert.Null(destination);
        Assert.NotEmpty(proxy.Hits(target).Where(h => h.At >= since && h.Faulted));

        // The same store recovers as soon as the host behaves.
        proxy.Clear();
        var healthy = await _gate.Greenfield.CreateLightningInvoiceAsync(store, satoshis: 140);
        Assert.NotNull(await _gate.Greenfield.TryGetLightningDestinationAsync(store, healthy));
    }
}
