using BTCPayServer.Data;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

/// <summary>
/// LNURL never asks Coinsnap for an invoice when BTCPay creates one, so the plugin keeps BTCPay's own
/// per-method amount limits at the address's maximum. A limit the merchant set stays theirs.
/// </summary>
public class AmountLimitsTests
{
    private static readonly PaymentMethodId Lightning = PaymentTypes.LN.GetPaymentMethodId("BTC");
    private static readonly PaymentMethodId Lnurl = PaymentTypes.LNURL.GetPaymentMethodId("BTC");
    private static readonly PaymentMethodId OnChain = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");

    [Fact]
    public void AStoreWithoutLimitsGetsTheMaximumForLightningAndLnurl()
    {
        var blob = new StoreBlob();

        Assert.True(CoinsnapAmountLimits.Apply(blob, 1m));

        AssertLimit(blob, Lightning, 1m);
        AssertLimit(blob, Lnurl, 1m);
    }

    [Fact]
    public void ALimitThePluginSetFollowsANewMaximum()
    {
        var blob = new StoreBlob();
        CoinsnapAmountLimits.Apply(blob, 1m);

        Assert.True(CoinsnapAmountLimits.Apply(blob, 0.5m));

        AssertLimit(blob, Lightning, 0.5m);
        AssertLimit(blob, Lnurl, 0.5m);
    }

    [Fact]
    public void AnUnchangedMaximumChangesNothing()
    {
        var blob = new StoreBlob();
        CoinsnapAmountLimits.Apply(blob, 1m);

        Assert.False(CoinsnapAmountLimits.Apply(blob, 1m));
    }

    [Fact]
    public void AMerchantsOwnLimitIsKept()
    {
        var blob = new StoreBlob();
        blob.PaymentMethodCriteria.Add(Criteria(Lightning, 0.2m, "BTC"));

        CoinsnapAmountLimits.Apply(blob, 1m);

        AssertLimit(blob, Lightning, 0.2m);
        AssertLimit(blob, Lnurl, 1m);
    }

    [Fact]
    public void ALimitTheMerchantEditedIsNoLongerManaged()
    {
        var blob = new StoreBlob();
        CoinsnapAmountLimits.Apply(blob, 1m);
        blob.PaymentMethodCriteria.Find(c => c.PaymentMethod == Lightning)!.Value = new CurrencyValue { Value = 500m, Currency = "USD" };

        CoinsnapAmountLimits.Apply(blob, 0.5m);
        CoinsnapAmountLimits.Apply(blob, null);

        var lightning = blob.PaymentMethodCriteria.Single(c => c.PaymentMethod == Lightning);
        Assert.Equal(500m, lightning.Value.Value);
        Assert.Equal("USD", lightning.Value.Currency);
    }

    [Fact]
    public void LeavingCoinsnapRemovesOnlyThePluginsLimits()
    {
        var blob = new StoreBlob();
        blob.PaymentMethodCriteria.Add(Criteria(Lightning, 0.2m, "BTC"));
        CoinsnapAmountLimits.Apply(blob, 1m);

        Assert.True(CoinsnapAmountLimits.Apply(blob, null));

        AssertLimit(blob, Lightning, 0.2m);
        Assert.DoesNotContain(blob.PaymentMethodCriteria, c => c.PaymentMethod == Lnurl);
        Assert.False(CoinsnapAmountLimits.Apply(blob, null));
    }

    [Fact]
    public void OtherPaymentMethodsAreLeftAlone()
    {
        var blob = new StoreBlob();
        blob.PaymentMethodCriteria.Add(Criteria(OnChain, 0.001m, "BTC", above: true));

        CoinsnapAmountLimits.Apply(blob, 1m);
        CoinsnapAmountLimits.Apply(blob, null);

        var onChain = Assert.Single(blob.PaymentMethodCriteria);
        Assert.Equal(OnChain, onChain.PaymentMethod);
        Assert.True(onChain.Above);
    }

    [Fact]
    public void TheRecordSurvivesSavingTheBlob()
    {
        var blob = new StoreBlob();
        CoinsnapAmountLimits.Apply(blob, 1m);
        var store = new StoreData();
        store.SetStoreBlob(blob);

        var reloaded = store.GetStoreBlob();

        Assert.True(CoinsnapAmountLimits.Apply(reloaded, 0.5m));
        AssertLimit(reloaded, Lightning, 0.5m);
    }

    [Theory]
    [InlineData(100_000_000_000L, 1)]
    [InlineData(250_000_000L, 0.0025)]
    [InlineData(1_000L, 0.00000001)]
    public void MaxSendableIsConvertedFromMillisatoshisToBitcoin(long maxSendableMsat, double expectedBtc)
    {
        Assert.Equal((decimal)expectedBtc, CoinsnapAmountLimits.ToBitcoin(maxSendableMsat));
    }

    private static PaymentMethodCriteria Criteria(PaymentMethodId method, decimal value, string currency, bool above = false) =>
        new() { PaymentMethod = method, Value = new CurrencyValue { Value = value, Currency = currency }, Above = above };

    private static void AssertLimit(StoreBlob blob, PaymentMethodId method, decimal btc)
    {
        var criteria = Assert.Single(blob.PaymentMethodCriteria, c => c.PaymentMethod == method);
        Assert.False(criteria.Above);
        Assert.Equal("BTC", criteria.Value.Currency);
        Assert.Equal(btc, criteria.Value.Value);
    }
}
