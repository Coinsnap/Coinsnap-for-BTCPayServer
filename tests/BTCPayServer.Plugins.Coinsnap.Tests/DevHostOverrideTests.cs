using BTCPayServer.Lightning;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

public class DevHostOverrideTests
{
    private const string TestHost = "ln.example.test";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnsetOverrideKeepsTheProductionHost(string? value)
    {
        Assert.Equal(CoinsnapConstants.DefaultHost, CoinsnapConstants.ResolveHost(value));
    }

    [Theory]
    [InlineData("https://ln.example.test")]
    [InlineData("ln.example.test/lnurlp")]
    [InlineData("ln.example.test:8443")]
    [InlineData("ln example test")]
    public void MalformedOverrideKeepsTheProductionHost(string value)
    {
        Assert.Equal(CoinsnapConstants.DefaultHost, CoinsnapConstants.ResolveHost(value));
    }

    [Fact]
    public void OverrideAppliesOnlyToDebugBuilds()
    {
#if DEBUG
        Assert.Equal(TestHost, CoinsnapConstants.ResolveHost($"  {TestHost} "));
#else
        Assert.Equal(CoinsnapConstants.DefaultHost, CoinsnapConstants.ResolveHost(TestHost));
#endif
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-endpoint")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:0")]
    [InlineData("10.0.0.5:8443")]
    [InlineData("203.0.113.7:443")]
    public void ConnectOverrideAcceptsOnlyALoopbackEndpoint(string? value)
    {
        Assert.Null(CoinsnapConstants.ResolveConnectOverride(value));
    }

    [Fact]
    public void ConnectOverrideAppliesOnlyToDebugBuilds()
    {
#if DEBUG
        Assert.Equal("127.0.0.1:18443", CoinsnapConstants.ResolveConnectOverride(" 127.0.0.1:18443 ")?.ToString());
        Assert.Equal("[::1]:18443", CoinsnapConstants.ResolveConnectOverride("[::1]:18443")?.ToString());
#else
        Assert.Null(CoinsnapConstants.ResolveConnectOverride("127.0.0.1:18443"));
#endif
    }

    [Fact]
    public void NonMainnetNeedsAnOverriddenHost()
    {
        Assert.False(CoinsnapLightningConnectionStringHandler.IsNetworkAllowed(Network.RegTest, hostOverridden: false));
        Assert.False(CoinsnapLightningConnectionStringHandler.IsNetworkAllowed(Network.TestNet, hostOverridden: false));
        Assert.True(CoinsnapLightningConnectionStringHandler.IsNetworkAllowed(Network.RegTest, hostOverridden: true));
    }

    [Fact]
    public void MainnetIsAllowedWithoutAnOverride()
    {
        Assert.True(CoinsnapLightningConnectionStringHandler.IsNetworkAllowed(Network.Main, hostOverridden: false));
    }
}

public class InvoiceNetworkBindingTests
{
    [Fact]
    public async Task CreatedInvoiceRecordsTheNetworkItWasMintedOn()
    {
        var clock = new TestClock();
        var parser = new FakeBolt11Parser();
        parser.Add("bolt-ok", TestData.PaymentHash, 25_000, clock.UtcNow.AddMinutes(14));
        var handler = new ScriptedHttpHandler();
        handler.EnqueueJson(TestData.Metadata(callback: "https://coinsnap.app/lnurlp/yourname/invoice"));
        handler.EnqueueJson($$"""{"pr":"bolt-ok","verify":"https://coinsnap.app/verify/{{TestData.PaymentHash}}"}""");
        var (service, repository) = await TestData.Service(handler, parser, clock);

        await service.CreateInvoiceAsync(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"),
            "store:store-a",
            "store-a",
            Network.RegTest,
            LightMoney.MilliSatoshis(25_000),
            TimeSpan.FromMinutes(15),
            null,
            CancellationToken.None);

        Assert.True(repository.TryGet(TestData.PaymentHash, "store:store-a", out var state));
        Assert.Equal(Network.RegTest.Name, state.NetworkName);
    }

    [Fact]
    public void PollerVerifiesOnTheInvoicesOwnNetwork()
    {
        Assert.Equal(
            Network.RegTest,
            CoinsnapSettlementPoller.NetworkFor(new CoinsnapInvoiceState { NetworkName = Network.RegTest.Name }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-network")]
    public void PollerFallsBackToMainnetForStateWithoutAUsableNetwork(string? name)
    {
        Assert.Equal(Network.Main, CoinsnapSettlementPoller.NetworkFor(new CoinsnapInvoiceState { NetworkName = name }));
    }
}
