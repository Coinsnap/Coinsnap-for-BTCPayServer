using System.Net;
using BTCPayServer.Lightning;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

public class AddressAndSecurityTests
{
    [Fact]
    public void MetadataUriUsesTheStandardWellKnownPath()
    {
        Assert.Equal(
            $"https://{CoinsnapConstants.Host}/.well-known/lnurlp/yourname",
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app").MetadataUri.ToString());
    }

    [Theory]
    [InlineData("yourname@coinsnap.app", "yourname@coinsnap.app")]
    [InlineData("yourname.usd@coinsnap.app", "yourname.usd@coinsnap.app")]
    [InlineData("yourname@COINSNAP.APP", "yourname@coinsnap.app")]
    public void AcceptsSupportedAddresses(string input, string expected)
    {
        Assert.Equal(expected, CoinsnapLightningAddress.Parse(input).Value);
    }

    [Theory]
    [InlineData("someone@example.com")]
    [InlineData("@coinsnap.app")]
    [InlineData("merchant")]
    [InlineData("yourname@@coinsnap.app")]
    [InlineData("merchant..usd@coinsnap.app")]
    [InlineData("merchant;type=x@coinsnap.app")]
    public void RejectsUnsupportedOrMalformedAddresses(string input)
    {
        Assert.False(CoinsnapLightningAddress.TryParse(input, out _, out _));
    }

    [Theory]
    [InlineData("http://coinsnap.app/verify/x")]
    [InlineData("https://example.com/verify/x")]
    [InlineData("https://localhost/verify/x")]
    [InlineData("https://127.0.0.1/verify/x")]
    [InlineData("file:///tmp/x")]
    [InlineData("https://coinsnap.app:444/verify/x")]
    [InlineData("https://user@coinsnap.app/verify/x")]
    public void UrlPolicyRejectsUnsafeTargets(string value)
    {
        Assert.Throws<FormatException>(() => CoinsnapUrlPolicy.ParseAndValidate(value, "test URL"));
    }

    [Theory]
    [InlineData("10.0.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("2001:4860:4860::8888", true)]
    [InlineData("192.88.99.1", false)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("64:ff9b::c0a8:101", false)]
    [InlineData("64:ff9b::808:808", true)]
    [InlineData("2002:a00:1::1", false)]
    [InlineData("2002:7f00:1::1", false)]
    [InlineData("2002:808:808::1", true)]
    [InlineData("64:ff9b:1::1", false)]
    [InlineData("2001:0:4136:e378::1", false)]
    public void DnsPolicyRejectsPrivateAndReservedAddresses(string value, bool expected)
    {
        Assert.Equal(expected, CoinsnapNetworkPolicy.IsPublicAddress(IPAddress.Parse(value)));
    }

    [Fact]
    public async Task RejectsCrossDomainRedirect()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueJson("", HttpStatusCode.Found, r => r.Headers.Location = new Uri("https://evil.example/steal"));
        var client = new CoinsnapHttpClient(new HttpClient(handler));

        await Assert.ThrowsAsync<FormatException>(() =>
            client.GetAsync(new Uri("https://coinsnap.app/lnurlp/yourname"), "test URL", CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AllowsBoundedSameHostRedirect()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueJson("", HttpStatusCode.Found, r => r.Headers.Location = new Uri("/lnurlp/yourname2", UriKind.Relative));
        handler.EnqueueJson("{}", HttpStatusCode.OK);
        var client = new CoinsnapHttpClient(new HttpClient(handler));

        var response = await client.GetAsync(
            new Uri("https://coinsnap.app/lnurlp/yourname"), "test URL", CancellationToken.None);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("https://coinsnap.app/lnurlp/yourname2", response.FinalUri.ToString());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RejectsTooManyRedirects()
    {
        var handler = new ScriptedHttpHandler();
        for (var i = 0; i <= CoinsnapConstants.MaxRedirects; i++)
            handler.EnqueueJson("", HttpStatusCode.Found, r => r.Headers.Location = new Uri("/next", UriKind.Relative));
        var client = new CoinsnapHttpClient(new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(new Uri("https://coinsnap.app/start"), "test URL", CancellationToken.None));
        Assert.Equal(CoinsnapConstants.MaxRedirects + 1, handler.Requests.Count);
    }

    [Fact]
    public async Task RejectsOversizedResponseBeforeParsing()
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[CoinsnapConstants.MaxResponseBytes + 1])
        }));
        var client = new CoinsnapHttpClient(new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync(new Uri("https://coinsnap.app/lnurlp/yourname"), "test URL", CancellationToken.None));
    }

    [Fact]
    public void ProductionBoltParserRejectsWrongNetwork()
    {
        var parser = new CoinsnapBolt11Parser();
        var parsed = parser.Parse(TestData.MainnetBolt11, Network.Main);
        Assert.NotNull(parsed.Amount);
        Assert.Throws<FormatException>(() => parser.Parse(TestData.MainnetBolt11, Network.TestNet));
    }

    [Fact]
    public async Task ConnectionHandlerAcceptsOnlyCoinsnapAddressAndBuildsStoreIdentity()
    {
        var handler = new ScriptedHttpHandler();
        var client = new HttpClient(handler);
        var clock = new TestClock();
        var parser = new FakeBolt11Parser();
        var repository = new CoinsnapInvoiceStateRepository(new MemoryStateStore());
        // The store must declare this connection; an unverified claim is now refused outright.
        var lookup = new FakeStoreConfigLookup();
        lookup.ConnectionStrings["abc"] = "type=coinsnap;ln-address=yourname@coinsnap.app;store-id=abc;binding=b-abc;";
        var connectionHandler = new CoinsnapLightningConnectionStringHandler(
            new SingleHttpClientFactory(client),
            parser,
            repository,
            clock,
            lookup,
            new FakeStoreBinding(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var lightning = connectionHandler.Create(
            "type=coinsnap;ln-address=yourname@coinsnap.app;store-id=abc;binding=b-abc;",
            Network.Main,
            out var error);
        Assert.NotNull(lightning);
        Assert.Null(error);

        Assert.Null(connectionHandler.Create(
            "type=coinsnap;ln-address=someone@example.com;store-id=abc;binding=b-abc;",
            Network.Main,
            out error));
        Assert.Contains("@coinsnap.app", error);

        Assert.Null(connectionHandler.Create(
            "type=coinsnap;ln-address=yourname@coinsnap.app;api-key=secret;",
            Network.Main,
            out error));
        Assert.Contains("not supported", error);

        Assert.Null(connectionHandler.Create(
            "type=coinsnap;ln-address=yourname@coinsnap.app;store-id=abc;binding=b-abc;",
            Network.TestNet,
            out error));
        Assert.Contains("mainnet", error);
        await Task.CompletedTask;
    }
}
