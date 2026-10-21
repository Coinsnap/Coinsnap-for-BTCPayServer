using Microsoft.Extensions.Logging.Abstractions;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

/// <summary>
/// BTCPay 2.4.5 lets only server admins save a connection string without a public `server`, so
/// the plugin names its own host there. It must never accept any other server.
/// </summary>
public class ServerKeyTests
{
    private static readonly string Address = $"yourname@{CoinsnapConstants.Host}";

    [Fact]
    public async Task TheCoinsnapServerIsAccepted()
    {
        var handler = await Fixture();

        var client = handler.Create(Connection($"https://{CoinsnapConstants.Host}"), Network.Main, out var error);

        Assert.Null(error);
        Assert.NotNull(client);
    }

    [Fact]
    public async Task ATrailingSlashAndUpperCaseHostAreAccepted()
    {
        var handler = await Fixture();

        var client = handler.Create(
            Connection($"https://{CoinsnapConstants.Host.ToUpperInvariant()}/"), Network.Main, out var error);

        Assert.Null(error);
        Assert.NotNull(client);
    }

    [Fact]
    public async Task AConnectionWithoutAServerStillWorks()
    {
        var handler = await Fixture();

        var client = handler.Create(
            $"type=coinsnap;ln-address={Address};store-id=merchant-store;binding=b-merchant-store;", Network.Main, out var error);

        Assert.Null(error);
        Assert.NotNull(client);
    }

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("http://{host}")]
    [InlineData("https://{host}:8443")]
    [InlineData("https://{host}/lnurlp")]
    [InlineData("https://user@{host}")]
    [InlineData("https://{host}?x=1")]
    [InlineData("{host}")]
    public async Task AnyOtherServerIsRejected(string server)
    {
        var handler = await Fixture();

        var client = handler.Create(
            Connection(server.Replace("{host}", CoinsnapConstants.Host)), Network.Main, out var error);

        Assert.Null(client);
        Assert.Contains($"https://{CoinsnapConstants.Host}", error);
    }

    private static string Connection(string server) =>
        $"type=coinsnap;server={server};ln-address={Address};store-id=merchant-store;binding=b-merchant-store;";

    private static async Task<CoinsnapLightningConnectionStringHandler> Fixture()
    {
        var repository = new CoinsnapInvoiceStateRepository(new MemoryStateStore());
        await repository.EnsureLoadedAsync();
        return new CoinsnapLightningConnectionStringHandler(
            new SingleHttpClientFactory(new HttpClient(new ScriptedHttpHandler())),
            new FakeBolt11Parser(),
            repository,
            new TestClock(),
            new FakeStoreConfigLookup(),
            new FakeStoreBinding(),
            NullLoggerFactory.Instance);
    }
}
