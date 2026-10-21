using BTCPayServer.Lightning;
using System.Security.Cryptography;
using System.Text;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

public class InvoiceCreationTests
{
    [Fact]
    public async Task CreatesAndPersistsExactInvoiceWithRequestedExpiry()
    {
        var clock = new TestClock();
        var parser = new FakeBolt11Parser();
        parser.Add("bolt-ok", TestData.PaymentHash, 25_000, clock.UtcNow.AddMinutes(14));
        var handler = new ScriptedHttpHandler();
        handler.EnqueueJson(TestData.Metadata(callback: "https://coinsnap.app/lnurlp/yourname/invoice?source=lnurl"));
        handler.EnqueueJson($$"""{"pr":"bolt-ok","verify":"https://coinsnap.app/verify/{{TestData.PaymentHash}}"}""");
        var (service, repository) = await TestData.Service(handler, parser, clock);

        var invoice = await service.CreateInvoiceAsync(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"),
            "store:store-a",
            "store-a",
            Network.Main,
            LightMoney.MilliSatoshis(25_000),
            TimeSpan.FromSeconds(899.1),
            null,
            CancellationToken.None);

        Assert.Equal(TestData.PaymentHash, invoice.Id);
        Assert.Equal(25_000, invoice.Amount!.MilliSatoshi);
        var callback = handler.Requests[1];
        Assert.Contains("source=lnurl", callback.Query);
        Assert.Contains("amount=25000", callback.Query);
        Assert.Contains("expiry=900", callback.Query);
        Assert.True(repository.TryGet(TestData.PaymentHash, "store:store-a", out var state));
        Assert.Equal("yourname@coinsnap.app", state.LightningAddress);
        Assert.Equal("https://coinsnap.app/verify/" + TestData.PaymentHash, state.VerifyUrl);
    }

    [Fact]
    public async Task RejectsNonWholeSatoshiBeforeCallingBackend()
    {
        var handler = new ScriptedHttpHandler();
        var (service, _) = await TestData.Service(handler, new FakeBolt11Parser(), new TestClock());
        await Assert.ThrowsAsync<FormatException>(() => service.CreateInvoiceAsync(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"), "store:a", "a", Network.Main,
            LightMoney.MilliSatoshis(1001), TimeSpan.FromMinutes(15), null, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RejectsWrongReturnedAmount()
    {
        var clock = new TestClock();
        var parser = new FakeBolt11Parser();
        parser.Add("bolt-wrong", TestData.PaymentHash, 26_000, clock.UtcNow.AddMinutes(15));
        var handler = InvoiceScript("bolt-wrong");
        var (service, _) = await TestData.Service(handler, parser, clock);
        await Assert.ThrowsAsync<FormatException>(() => service.CreateInvoiceAsync(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"), "store:a", "a", Network.Main,
            LightMoney.MilliSatoshis(25_000), TimeSpan.FromMinutes(15), null, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsWrongNetwork()
    {
        var clock = new TestClock();
        var parser = new FakeBolt11Parser { RequiredNetwork = Network.TestNet };
        parser.Add("bolt-testnet", TestData.PaymentHash, 25_000, clock.UtcNow.AddMinutes(15));
        var handler = InvoiceScript("bolt-testnet");
        var (service, _) = await TestData.Service(handler, parser, clock);
        await Assert.ThrowsAsync<FormatException>(() => service.CreateInvoiceAsync(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"), "store:a", "a", Network.Main,
            LightMoney.MilliSatoshis(25_000), TimeSpan.FromMinutes(15), null, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsAlreadyExpiredInvoice()
    {
        var clock = new TestClock();
        var parser = new FakeBolt11Parser();
        parser.Add("bolt-expired", TestData.PaymentHash, 25_000, clock.UtcNow.AddSeconds(-1));
        var handler = InvoiceScript("bolt-expired");
        var (service, _) = await TestData.Service(handler, parser, clock);
        await Assert.ThrowsAsync<FormatException>(() => service.CreateInvoiceAsync(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"), "store:a", "a", Network.Main,
            LightMoney.MilliSatoshis(25_000), TimeSpan.FromMinutes(15), null, CancellationToken.None));
    }

    /// <summary>
    /// A BOLT11 that outlives BTCPay's invoice can be paid after BTCPay stops watching, so the sats
    /// arrive with no BTCPay record; one that dies early fails while checkout still counts down.
    /// </summary>
    [Theory]
    [InlineData(24 * 60)]
    [InlineData(30 * 24 * 60)]
    [InlineData(18)]
    [InlineData(12)]
    [InlineData(1)]
    public async Task RejectsABolt11ExpiringOutsideTheRequestedWindow(int bolt11Minutes)
    {
        var clock = new TestClock();
        var parser = new FakeBolt11Parser();
        parser.Add("bolt-window", TestData.PaymentHash, 25_000, clock.UtcNow.AddMinutes(bolt11Minutes));
        var (service, repository) = await TestData.Service(InvoiceScript("bolt-window"), parser, clock);

        var error = await Assert.ThrowsAsync<FormatException>(() => service.CreateInvoiceAsync(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"), "store:a", "a", Network.Main,
            LightMoney.MilliSatoshis(25_000), TimeSpan.FromMinutes(15), null, CancellationToken.None));

        Assert.Contains("expiring at", error.Message);
        Assert.Empty(repository.Snapshot());
    }

    [Theory]
    [InlineData(-110)]
    [InlineData(0)]
    [InlineData(110)]
    public async Task AcceptsABolt11WithinTheRequestedWindow(int offsetSeconds)
    {
        var clock = new TestClock();
        var parser = new FakeBolt11Parser();
        parser.Add("bolt-window", TestData.PaymentHash, 25_000, clock.UtcNow.AddMinutes(15).AddSeconds(offsetSeconds));
        var (service, _) = await TestData.Service(InvoiceScript("bolt-window"), parser, clock);

        var invoice = await service.CreateInvoiceAsync(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"), "store:a", "a", Network.Main,
            LightMoney.MilliSatoshis(25_000), TimeSpan.FromMinutes(15), null, CancellationToken.None);

        Assert.Equal(TestData.PaymentHash, invoice.PaymentHash);
    }

    [Fact]
    public async Task RejectsMissingVerifyUrl()
    {
        var clock = new TestClock();
        var parser = new FakeBolt11Parser();
        parser.Add("bolt-ok", TestData.PaymentHash, 25_000, clock.UtcNow.AddMinutes(15));
        var handler = new ScriptedHttpHandler();
        handler.EnqueueJson(TestData.Metadata());
        handler.EnqueueJson("{\"pr\":\"bolt-ok\"}");
        var (service, _) = await TestData.Service(handler, parser, clock);
        await Assert.ThrowsAsync<FormatException>(() => service.CreateInvoiceAsync(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"), "store:a", "a", Network.Main,
            LightMoney.MilliSatoshis(25_000), TimeSpan.FromMinutes(15), null, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsMismatchedDescriptionHash()
    {
        var clock = new TestClock();
        var parser = new FakeBolt11Parser();
        parser.Add("bolt-hash", TestData.PaymentHash, 25_000, clock.UtcNow.AddMinutes(15), "provider-hash");
        var handler = InvoiceScript("bolt-hash");
        var (service, _) = await TestData.Service(handler, parser, clock);
        await Assert.ThrowsAsync<FormatException>(() => service.CreateInvoiceAsync(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"), "store:a", "a", Network.Main,
            LightMoney.MilliSatoshis(25_000), TimeSpan.FromMinutes(15), "btcpay-hash", CancellationToken.None));
    }

    [Fact]
    public void ComputesHashForCurrentBtcpayDescriptionHashOnlyContract()
    {
        const string metadata = "[[\"text/identifier\",\"yourname@coinsnap.app\"]]";
        var request = new CreateInvoiceParams(
            LightMoney.MilliSatoshis(25_000), metadata, TimeSpan.FromMinutes(15))
        {
            DescriptionHashOnly = true
        };
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(metadata))).ToLowerInvariant();

        Assert.Equal(expected, CoinsnapLnAddressLightningClient.ExpectedDescriptionHash(request));
    }

    [Fact]
    public void RejectsNonPositiveOrHugeExpiry()
    {
        Assert.Throws<FormatException>(() => CoinsnapLnurlService.ToExpirySeconds(TimeSpan.Zero));
        Assert.Throws<FormatException>(() => CoinsnapLnurlService.ToExpirySeconds(TimeSpan.FromDays(30_000)));
    }

    private static ScriptedHttpHandler InvoiceScript(string bolt11)
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueJson(TestData.Metadata());
        handler.EnqueueJson($$"""{"pr":"{{bolt11}}","verify":"https://coinsnap.app/verify/{{TestData.PaymentHash}}"}""");
        return handler;
    }
}
