using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging.Abstractions;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

public class TenantIsolationTests
{
    private const string VictimConnectionString =
        "type=coinsnap;ln-address=victim@coinsnap.app;store-id=victim;binding=b-victim;";

    [Fact]
    public async Task CancelInvoiceCannotDeleteAnotherStoresInvoice()
    {
        var clock = new TestClock();
        var repository = new CoinsnapInvoiceStateRepository(new MemoryStateStore());
        await repository.EnsureLoadedAsync();
        await repository.AddAsync(TestData.State(clock, identity: "store:victim"));
        var attacker = Client("store:attacker", "attacker", Service(repository, clock), repository);

        await attacker.CancelInvoice(TestData.PaymentHash);

        Assert.True(repository.TryGet(TestData.PaymentHash, "store:victim", out _));
    }

    [Fact]
    public async Task CancelInvoiceStillDeletesTheOwningStoresInvoice()
    {
        var clock = new TestClock();
        var repository = new CoinsnapInvoiceStateRepository(new MemoryStateStore());
        await repository.EnsureLoadedAsync();
        await repository.AddAsync(TestData.State(clock, identity: "store:victim"));
        var owner = Client("store:victim", "victim", Service(repository, clock), repository);

        await owner.CancelInvoice(TestData.PaymentHash);

        Assert.False(repository.TryGet(TestData.PaymentHash, "store:victim", out _));
    }

    [Fact]
    public async Task ReAddingASettledInvoiceIsRejected()
    {
        var clock = new TestClock();
        var repository = new CoinsnapInvoiceStateRepository(new MemoryStateStore());
        await repository.EnsureLoadedAsync();
        await repository.AddAsync(TestData.State(clock, identity: "store:victim"));
        await repository.MarkSettledAsync(TestData.PaymentHash, TestData.Preimage, clock.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.AddAsync(TestData.State(clock, identity: "store:victim")));

        Assert.True(repository.TryGet(TestData.PaymentHash, "store:victim", out var still));
        Assert.True(still.Settled);
        Assert.Equal(TestData.Preimage, still.Preimage);
    }

    /// <summary>
    /// The victim's address and store-id can both be public, so a claimant can copy the exact pair.
    /// Without the victim's binding, which only the victim's managers can obtain, it gets no client.
    /// </summary>
    [Theory]
    [InlineData("type=coinsnap;ln-address=victim@coinsnap.app;store-id=victim;")]
    [InlineData("type=coinsnap;ln-address=victim@coinsnap.app;store-id=victim;binding=b-attacker;")]
    [InlineData("type=coinsnap;ln-address=victim@coinsnap.app;store-id=victim;binding=;")]
    [InlineData("type=coinsnap;ln-address=attacker@coinsnap.app;store-id=victim;")]
    public async Task ACopiedStoreClaimWithoutTheStoresBindingGetsNoClient(string forgedConnection)
    {
        var (repository, connectionHandler) = await Fixture();

        var forged = connectionHandler.Create(forgedConnection, Network.Main, out var error);

        Assert.Null(forged);
        Assert.Contains("not issued for this store", error);
        Assert.True(repository.TryGet(TestData.PaymentHash, "store:victim", out _));
    }

    /// <summary>A valid binding for the claimed store still needs that store to declare the address.</summary>
    [Fact]
    public async Task AValidBindingWithAnotherAddressStillCannotReachTheStoresInvoices()
    {
        var (repository, connectionHandler) = await Fixture();

        var client = (ILightningClient)connectionHandler.Create(
            "type=coinsnap;ln-address=attacker@coinsnap.app;store-id=victim;binding=b-victim;",
            Network.Main,
            out _)!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListInvoices());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CancelInvoice(TestData.PaymentHash));
        Assert.True(repository.TryGet(TestData.PaymentHash, "store:victim", out _));
    }

    [Fact]
    public async Task VerifiedStoreIdSeesItsOwnInvoices()
    {
        var (_, connectionHandler) = await Fixture();

        var owner = connectionHandler.Create(VictimConnectionString, Network.Main, out var error);

        Assert.Null(error);
        var invoices = await ((ILightningClient)owner!).ListInvoices();
        Assert.Single(invoices);
        Assert.Equal(TestData.PaymentHash, invoices[0].PaymentHash);
    }

    [Fact]
    public async Task UnresolvableStoreClaimFailsClosed()
    {
        var (_, connectionHandler) = await Fixture(lookup => lookup.Failure = new InvalidOperationException("database down"));

        var client = (ILightningClient)connectionHandler.Create(VictimConnectionString, Network.Main, out _)!;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListInvoices());
        Assert.Equal("database down", error.Message);
    }

    private static async Task<(CoinsnapInvoiceStateRepository Repository, CoinsnapLightningConnectionStringHandler Handler)> Fixture(
        Action<FakeStoreConfigLookup>? configure = null)
    {
        var clock = new TestClock();
        var repository = new CoinsnapInvoiceStateRepository(new MemoryStateStore());
        await repository.EnsureLoadedAsync();
        await repository.AddAsync(TestData.State(clock, identity: "store:victim", address: "victim@coinsnap.app"));

        var lookup = new FakeStoreConfigLookup();
        lookup.ConnectionStrings["victim"] = VictimConnectionString;
        configure?.Invoke(lookup);

        var handler = new CoinsnapLightningConnectionStringHandler(
            new SingleHttpClientFactory(new HttpClient(new ScriptedHttpHandler())),
            Parser(clock),
            repository,
            clock,
            lookup,
            new FakeStoreBinding(),
            NullLoggerFactory.Instance);
        return (repository, handler);
    }

    private static FakeBolt11Parser Parser(TestClock clock)
    {
        var parser = new FakeBolt11Parser();
        parser.Add("bolt-original", TestData.PaymentHash, 25_000, clock.UtcNow.AddMinutes(15));
        return parser;
    }

    private static CoinsnapLnurlService Service(ICoinsnapInvoiceStateRepository repository, TestClock clock) => new(
        new CoinsnapHttpClient(new HttpClient(new ScriptedHttpHandler())),
        Parser(clock),
        repository,
        clock,
        NullLogger<CoinsnapLnurlService>.Instance);

    private static CoinsnapLnAddressLightningClient Client(
        string identity,
        string storeId,
        CoinsnapLnurlService service,
        ICoinsnapInvoiceStateRepository repository) => new(
        CoinsnapLightningAddress.Parse("attacker@coinsnap.app"),
        identity,
        storeId,
        Network.Main,
        service,
        repository,
        FakeStoreConfigLookup.Declaring(storeId, "attacker@coinsnap.app"));
}
