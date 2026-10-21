namespace BTCPayServer.Plugins.Coinsnap.Tests;

public class PluginDependencyTests
{
    [Theory]
    [InlineData("2.4.5", true)]
    [InlineData("2.4.9", true)]
    [InlineData("2.4.4", false)]
    [InlineData("2.4.3", false)]
    [InlineData("2.5.0", false)]
    [InlineData("3.0.0", false)]
    public void LoadsOnlyOnTestedBtcpayVersions(string btcpay, bool loads)
    {
        var dependency = new CoinsnapPlugin().Dependencies.Single(d => d.Identifier == nameof(BTCPayServer));

        Assert.Equal(loads, dependency.ParseCondition().IsFulfilled(Version.Parse(btcpay)));
    }
}
