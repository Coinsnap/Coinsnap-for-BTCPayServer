using BTCPayServer.Lightning;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

public class MetadataAndAmountTests
{
    [Fact]
    public async Task AcceptsValidMetadata()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueJson(TestData.Metadata());
        var (service, _) = await TestData.Service(handler, new FakeBolt11Parser(), new TestClock());

        var metadata = await service.GetMetadataAsync(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"),
            CancellationToken.None);

        Assert.Equal("payRequest", metadata.Tag);
        Assert.Equal(1000, metadata.MinSendable);
        Assert.Equal(10_000_000, metadata.MaxSendable);
    }

    [Fact]
    public async Task RejectsWrongTag()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueJson(TestData.Metadata(tag: "withdrawRequest"));
        var (service, _) = await TestData.Service(handler, new FakeBolt11Parser(), new TestClock());
        await Assert.ThrowsAsync<FormatException>(() => service.GetMetadataAsync(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"), CancellationToken.None));
    }

    [Fact]
    public async Task RejectsMetadataServerError()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueJson("{\"status\":\"ERROR\",\"reason\":\"backend down\"}");
        var (service, _) = await TestData.Service(handler, new FakeBolt11Parser(), new TestClock());
        var ex = await Assert.ThrowsAsync<FormatException>(() => service.GetMetadataAsync(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"), CancellationToken.None));
        Assert.Contains("backend down", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://coinsnap.app/callback")]
    [InlineData("https://example.com/callback")]
    public async Task RejectsMissingOrUnsafeCallback(string callback)
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueJson(TestData.Metadata(callback: callback));
        var (service, _) = await TestData.Service(handler, new FakeBolt11Parser(), new TestClock());
        await Assert.ThrowsAsync<FormatException>(() => service.GetMetadataAsync(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"), CancellationToken.None));
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(2000, 1000)]
    [InlineData(-1, 1000)]
    public async Task RejectsInvalidBounds(long min, long max)
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueJson(TestData.Metadata(min: min, max: max));
        var (service, _) = await TestData.Service(handler, new FakeBolt11Parser(), new TestClock());
        await Assert.ThrowsAsync<FormatException>(() => service.GetMetadataAsync(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"), CancellationToken.None));
    }

    [Fact]
    public void WholeSatoshiAmountsAreAcceptedWithoutRounding()
    {
        CoinsnapLnurlService.ValidateWholeSatoshis(1000);
        CoinsnapLnurlService.ValidateWholeSatoshis(21_000);
        Assert.Throws<FormatException>(() => CoinsnapLnurlService.ValidateWholeSatoshis(1001));
        Assert.Throws<FormatException>(() => CoinsnapLnurlService.ValidateWholeSatoshis(1999));
    }

    [Fact]
    public void EnforcesAdvertisedAmountBounds()
    {
        CoinsnapLnurlService.ValidateAmountBounds(1000, 1000, 2000);
        CoinsnapLnurlService.ValidateAmountBounds(2000, 1000, 2000);
        Assert.Throws<FormatException>(() => CoinsnapLnurlService.ValidateAmountBounds(999, 1000, 2000));
        Assert.Throws<FormatException>(() => CoinsnapLnurlService.ValidateAmountBounds(2001, 1000, 2000));
    }

    [Fact]
    public void MetadataFilterMirrorsDescriptionAndIntersectsBounds()
    {
        var request = new LNURL.LNURLPayRequest
        {
            Metadata = "old",
            MinSendable = new LightMoney(500),
            MaxSendable = new LightMoney(50_000),
            CommentAllowed = 100
        };
        var metadata = new CoinsnapLnurlPayResponse
        {
            Metadata = "coinsnap-metadata",
            MinSendable = 1000,
            MaxSendable = 10_000,
            CommentAllowed = 20
        };

        CoinsnapLnurlRequestFilter.ApplyCoinsnapParameters(request, metadata);

        Assert.Equal("coinsnap-metadata", request.Metadata);
        Assert.Equal(1000, request.MinSendable!.MilliSatoshi);
        Assert.Equal(10_000, request.MaxSendable!.MilliSatoshi);
        Assert.Equal(20, request.CommentAllowed);
    }
}
