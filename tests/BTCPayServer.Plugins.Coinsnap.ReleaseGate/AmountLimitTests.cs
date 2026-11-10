using System.Globalization;
using BTCPayServer.Lightning;
using NBitcoin;

namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>
/// LNURL never asks Coinsnap for an invoice when BTCPay creates one, so without the amount limits the
/// plugin sets, BTCPay creates an invoice above the address's maximum that no wallet can pay.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "ReleaseGate")]
public sealed class AmountLimitTests(GateFixture gate)
{
    private const string Lightning = "BTC-LN";
    private const string Lnurl = "BTC-LNURL";

    [Fact]
    public async Task With_lnurl_enabled_an_invoice_above_the_address_maximum_is_refused()
    {
        var maxSat = await MaxSatAsync();
        var store = await CoinsnapStoreWithLnurlAsync("gate-limit-over-max");
        await WaitForLimitAsync(store, Lightning, Btc(maxSat));

        var refused = await Assert.ThrowsAnyAsync<Exception>(
            () => gate.Greenfield.CreateInvoiceAsync(store, BtcText(maxSat + 1), "BTC", Lightning, Lnurl));

        Assert.Contains("payment method", refused.Message, StringComparison.OrdinalIgnoreCase);
        // Exactly at the maximum both methods are still offered.
        await gate.Greenfield.CreateInvoiceAsync(store, BtcText(maxSat), "BTC", Lightning, Lnurl);
    }

    [Fact]
    public async Task The_limits_appear_in_the_store_settings_at_the_address_maximum()
    {
        var maxSat = await MaxSatAsync();
        var store = await CoinsnapStoreWithLnurlAsync("gate-limit-settings");

        await WaitForLimitAsync(store, Lightning, Btc(maxSat));
        await WaitForLimitAsync(store, Lnurl, Btc(maxSat));

        var limits = await gate.Greenfield.GetAmountLimitsAsync(store);
        Assert.All(new[] { Lightning, Lnurl }, method =>
        {
            Assert.Equal("BTC", limits[method].Currency);
            Assert.False(limits[method].Above);
        });
    }

    [Fact]
    public async Task A_merchants_own_limit_is_kept()
    {
        var maxSat = await MaxSatAsync();
        var store = await CoinsnapStoreWithLnurlAsync("gate-limit-merchant");
        await WaitForLimitAsync(store, Lightning, Btc(maxSat));

        await gate.Greenfield.SetAmountLimitAsync(store, Lightning, 50m, "USD", above: false);
        // Saving the store runs the plugin again; give it time to (wrongly) overwrite.
        await Task.Delay(TimeSpan.FromSeconds(5));

        var limits = await gate.Greenfield.GetAmountLimitsAsync(store);
        Assert.Equal((50m, "USD", false), limits[Lightning]);
        Assert.Equal(Btc(maxSat), limits[Lnurl].Amount);
    }

    [Fact]
    public async Task Leaving_coinsnap_removes_only_the_plugins_limits()
    {
        var maxSat = await MaxSatAsync();
        var store = await CoinsnapStoreWithLnurlAsync("gate-limit-leave");
        await WaitForLimitAsync(store, Lnurl, Btc(maxSat));
        await gate.Greenfield.SetAmountLimitAsync(store, Lightning, 50m, "USD", above: false);

        await gate.Greenfield.RemoveLightningAsync(store);

        var limits = await gate.Greenfield.GetAmountLimitsAsync(store);
        for (var i = 0; i < 30 && limits.ContainsKey(Lnurl); i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            limits = await gate.Greenfield.GetAmountLimitsAsync(store);
        }
        Assert.DoesNotContain(Lnurl, limits.Keys);
        Assert.Equal((50m, "USD", false), limits[Lightning]);
    }

    [Fact]
    public async Task Limits_missing_while_btcpay_was_down_are_set_at_startup()
    {
        var maxSat = await MaxSatAsync();
        var store = await CoinsnapStoreWithLnurlAsync("gate-limit-startup");
        await WaitForLimitAsync(store, Lightning, Btc(maxSat));

        await gate.Host.StopAsync();
        await GateDatabase.ClearAmountLimitsAsync(store);
        await gate.Host.StartAsync();

        await WaitForLimitAsync(store, Lightning, Btc(maxSat));
        await WaitForLimitAsync(store, Lnurl, Btc(maxSat));
    }

    [Fact]
    public async Task With_on_chain_an_invoice_above_the_maximum_is_created_without_lightning()
    {
        var maxSat = await MaxSatAsync();
        var store = await CoinsnapStoreWithLnurlAsync("gate-limit-onchain");
        var xpub = new ExtKey().Neuter().GetWif(Network.RegTest).ToString();
        await gate.Greenfield.SetOnChainAsync(store, xpub);
        await WaitForLimitAsync(store, Lnurl, Btc(maxSat));

        var invoice = await gate.Greenfield.CreateInvoiceAsync(store, BtcText(maxSat + 1), "BTC", "BTC-CHAIN", Lightning, Lnurl);

        var methods = await gate.Greenfield.GetInvoicePaymentMethodsAsync(store, invoice);
        Assert.Contains("BTC-CHAIN", methods);
        Assert.DoesNotContain(Lightning, methods);
        Assert.DoesNotContain(Lnurl, methods);
    }

    [Fact]
    public async Task An_invoice_at_the_address_maximum_is_paid_and_settles()
    {
        var maxSat = await MaxSatAsync();
        var store = await CoinsnapStoreWithLnurlAsync("gate-limit-pay-max");
        await WaitForLimitAsync(store, Lightning, Btc(maxSat));

        var invoice = await gate.Greenfield.CreateInvoiceAsync(store, BtcText(maxSat), "BTC", Lightning);
        var bolt11 = await gate.Greenfield.GetLightningDestinationAsync(store, invoice);
        Assert.Equal(maxSat * 1000, BOLT11PaymentRequest.Parse(bolt11, Network.RegTest).MinimumAmount.MilliSatoshi);

        await gate.Payer.PayAsync(bolt11);

        Assert.Equal("Settled", await gate.Greenfield.WaitForStatusAsync(store, invoice, "Settled", TimeSpan.FromMinutes(2)));
    }

    private async Task<string> CoinsnapStoreWithLnurlAsync(string name)
    {
        var store = await gate.Greenfield.CreateStoreAsync(name);
        await gate.Greenfield.SetCoinsnapLightningAsync(store, GateConfig.LightningAddress);
        await gate.Greenfield.EnableLnurlAsync(store);
        return store;
    }

    private static async Task<long> MaxSatAsync() =>
        (await LnurlDirectory.GetSendableAsync(GateConfig.LightningAddress)).MaxSat;

    // The plugin sets the limits in the background after the store is saved.
    private async Task WaitForLimitAsync(string store, string method, decimal btc)
    {
        for (var i = 0; i < 30; i++)
        {
            if ((await gate.Greenfield.GetAmountLimitsAsync(store)).TryGetValue(method, out var limit) && limit.Amount == btc)
                return;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        Assert.Fail($"Store {store} never got a {method} limit of {btc} BTC.");
    }

    private static decimal Btc(long satoshis) => satoshis / 100_000_000m;

    private static string BtcText(long satoshis) => Btc(satoshis).ToString("0.00000000", CultureInfo.InvariantCulture);
}
