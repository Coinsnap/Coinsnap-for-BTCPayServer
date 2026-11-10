using System.Globalization;

namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>
/// LNURL never asks Coinsnap for an invoice when BTCPay creates one, so without the amount limits the
/// plugin sets, BTCPay creates an invoice above the address's maximum that no wallet can pay.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "ReleaseGate")]
public sealed class AmountLimitTests(GateFixture gate)
{
    [Fact]
    public async Task With_lnurl_enabled_an_invoice_above_the_address_maximum_is_refused()
    {
        var (_, maxSat) = await LnurlDirectory.GetSendableAsync(GateConfig.LightningAddress);
        var store = await gate.Greenfield.CreateStoreAsync("gate-limit-over-max");
        await gate.Greenfield.SetCoinsnapLightningAsync(store, GateConfig.LightningAddress);
        await gate.Greenfield.EnableLnurlAsync(store);

        // The limit is set in the background after the store is saved.
        Exception? refused = null;
        for (var i = 0; i < 30 && refused is null; i++)
        {
            try
            {
                await gate.Greenfield.CreateInvoiceAsync(store, Btc(maxSat + 1), "BTC", "BTC-LN", "BTC-LNURL");
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex) when (ex is GreenfieldValidationException or InvalidOperationException)
            {
                refused = ex;
            }
        }

        Assert.NotNull(refused);
        Assert.Contains("payment method", refused.Message, StringComparison.OrdinalIgnoreCase);

        // Exactly at the maximum both methods are still offered.
        await gate.Greenfield.CreateInvoiceAsync(store, Btc(maxSat), "BTC", "BTC-LN", "BTC-LNURL");
    }

    private static string Btc(long satoshis) => (satoshis / 100_000_000m).ToString("0.00000000", CultureInfo.InvariantCulture);
}
