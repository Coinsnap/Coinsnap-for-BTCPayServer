using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging.Abstractions;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

/// <summary>
/// Tracking identity must be bound to the store, never to the Lightning Address. An
/// address-derived identity changes when the merchant edits their address, and settlement of any
/// invoice still in flight is then dropped by the listener, which filters on that identity.
/// Observed on regtest: the sats arrived, the poller marked the state settled, and BTCPay left the
/// invoice unpaid until it expired.
/// </summary>
public class StoreBindingTests
{
    private const string StoreConnectionString =
        "type=coinsnap;ln-address=yourname@coinsnap.app;store-id=merchant-store;binding=b-merchant-store;";

    [Fact]
    public async Task ConnectionStringWithoutAStoreIdIsRejected()
    {
        var handler = await Fixture();

        var client = handler.Create("type=coinsnap;ln-address=yourname@coinsnap.app;", Network.Main, out var error);

        Assert.Null(client);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task TheRejectionSaysAStoreIdIsRequired()
    {
        var handler = await Fixture();

        handler.Create("type=coinsnap;ln-address=yourname@coinsnap.app;", Network.Main, out var error);

        Assert.Contains("store-id", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>BTCPay builds the client to validate a connection before saving it, so nothing is declared yet.</summary>
    [Fact]
    public async Task AFreshStoreCanBeSetUp()
    {
        var handler = await Fixture();

        var client = handler.Create(
            "type=coinsnap;ln-address=yourname@coinsnap.app;store-id=new-store;binding=b-new-store;",
            Network.Main,
            out var error);

        Assert.Null(error);
        Assert.NotNull(client);
    }

    [Fact]
    public async Task AnAddressChangeCanBeSaved()
    {
        var handler = await Fixture();

        var client = handler.Create(
            "type=coinsnap;ln-address=yourname.usd@coinsnap.app;store-id=merchant-store;binding=b-merchant-store;",
            Network.Main,
            out var error);

        Assert.Null(error);
        Assert.NotNull(client);
    }

    /// <summary>An unverifiable claim must fail closed, not fall back to address tracking.</summary>
    [Fact]
    public async Task AStoreIdThatDoesNotDeclareThisConnectionCannotCreateInvoices()
    {
        var handler = await Fixture();

        var client = handler.Create(
            "type=coinsnap;ln-address=someone-else@coinsnap.app;store-id=merchant-store;binding=b-merchant-store;",
            Network.Main,
            out _);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client!.CreateInvoice(LightMoney.Satoshis(1_000), "test", TimeSpan.FromMinutes(15)));
        Assert.Contains("does not declare", error.Message);
    }

    [Fact]
    public async Task AVerifiedStoreIdIsAcceptedAndTracksByStore()
    {
        var handler = await Fixture();

        var client = handler.Create(StoreConnectionString, Network.Main, out var error);

        Assert.Null(error);
        Assert.NotNull(client);
        Assert.Equal(
            "store:merchant-store",
            CoinsnapLightningConnectionStringHandler.ConnectionIdentity(
                CoinsnapLightningAddress.Parse("yourname@coinsnap.app"), "merchant-store"));
    }

    /// <summary>The identity a store tracks under must not move when its address is edited.</summary>
    [Fact]
    public void IdentityIsUnchangedWhenTheAddressChanges()
    {
        var before = CoinsnapLightningConnectionStringHandler.ConnectionIdentity(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"), "merchant-store");
        var after = CoinsnapLightningConnectionStringHandler.ConnectionIdentity(
            CoinsnapLightningAddress.Parse("yourname.usd@coinsnap.app"), "merchant-store");

        Assert.Equal(before, after);
    }

    private static async Task<CoinsnapLightningConnectionStringHandler> Fixture()
    {
        var clock = new TestClock();
        var repository = new CoinsnapInvoiceStateRepository(new MemoryStateStore());
        await repository.EnsureLoadedAsync();

        var lookup = new FakeStoreConfigLookup();
        lookup.ConnectionStrings["merchant-store"] = StoreConnectionString;

        return new CoinsnapLightningConnectionStringHandler(
            new SingleHttpClientFactory(new HttpClient(new ScriptedHttpHandler())),
            new FakeBolt11Parser(),
            repository,
            clock,
            lookup,
            new FakeStoreBinding(),
            NullLoggerFactory.Instance);
    }
}
