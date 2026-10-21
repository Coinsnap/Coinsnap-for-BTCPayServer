using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

public class PersistenceAndIsolationTests
{
    [Fact]
    public async Task RestartReloadsInvoiceAndDetectsSettlement()
    {
        var clock = new TestClock();
        var durableStore = new MemoryStateStore();
        var beforeRestart = new CoinsnapInvoiceStateRepository(durableStore, clock);
        await beforeRestart.EnsureLoadedAsync();
        await beforeRestart.AddAsync(TestData.State(clock));

        var afterRestart = new CoinsnapInvoiceStateRepository(durableStore, clock);
        await afterRestart.EnsureLoadedAsync();
        Assert.True(afterRestart.TryGet(TestData.PaymentHash, "store:store-a", out _));

        var parser = Parser(clock);
        var handler = new ScriptedHttpHandler();
        handler.EnqueueJson($$"""{"status":"OK","settled":true,"preimage":"{{TestData.Preimage}}","pr":"bolt-original"}""");
        var service = Service(handler, parser, afterRestart, clock);
        var client = new CoinsnapLnAddressLightningClient(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"),
            "store:store-a",
            "store-a",
            Network.Main,
            service,
            afterRestart,
            FakeStoreConfigLookup.Declaring("store-a", "yourname@coinsnap.app"));

        var invoice = await client.GetInvoice(TestData.PaymentHash);

        Assert.Equal(LightningInvoiceStatus.Paid, invoice!.Status);
        Assert.True(afterRestart.TryGet(TestData.PaymentHash, "store:store-a", out var persisted));
        Assert.True(persisted.Settled);
        Assert.Equal(TestData.Preimage, persisted.Preimage);

        var secondRestart = new CoinsnapInvoiceStateRepository(durableStore, clock);
        await secondRestart.EnsureLoadedAsync();
        Assert.True(secondRestart.TryGet(TestData.PaymentHash, "store:store-a", out var recoveredPaid));
        Assert.True(recoveredPaid.Settled);
    }

    [Fact]
    public async Task AddressChangeKeepsOldInvoiceTrackingAndUsesNewAddressForNewInvoices()
    {
        var clock = new TestClock();
        var repository = new CoinsnapInvoiceStateRepository(new MemoryStateStore());
        await repository.EnsureLoadedAsync();
        await repository.AddAsync(TestData.State(clock, address: "old@coinsnap.app"));

        var newHash = new string('3', 64);
        var parser = Parser(clock);
        parser.Add("bolt-new", newHash, 50_000, clock.UtcNow.AddMinutes(15));
        var handler = new ScriptedHttpHandler();
        handler.EnqueueJson($$"""{"status":"OK","settled":true,"preimage":"{{TestData.Preimage}}","pr":"bolt-original"}""");
        handler.EnqueueJson(TestData.Metadata(callback: "https://coinsnap.app/lnurlp/new/invoice"));
        handler.EnqueueJson($$"""{"pr":"bolt-new","verify":"https://coinsnap.app/verify/{{newHash}}"}""");
        var service = Service(handler, parser, repository, clock);
        var changedClient = new CoinsnapLnAddressLightningClient(
            CoinsnapLightningAddress.Parse("new@coinsnap.app"),
            "store:store-a",
            "store-a",
            Network.Main,
            service,
            repository,
            FakeStoreConfigLookup.Declaring("store-a", "new@coinsnap.app"));

        var oldInvoice = await changedClient.GetInvoice(TestData.PaymentHash);
        Assert.Equal(LightningInvoiceStatus.Paid, oldInvoice!.Status);

        var newInvoice = await changedClient.CreateInvoice(
            new CreateInvoiceParams(LightMoney.MilliSatoshis(50_000), "new", TimeSpan.FromMinutes(15)));
        Assert.Equal(newHash, newInvoice.PaymentHash);
        Assert.True(repository.TryGet(newHash, "store:store-a", out var newState));
        Assert.Equal("new@coinsnap.app", newState.LightningAddress);
        Assert.Contains("/lnurlp/new", handler.Requests[1].AbsolutePath);
    }

    [Fact]
    public async Task MultipleStoresAreIsolated()
    {
        var clock = new TestClock();
        var repository = new CoinsnapInvoiceStateRepository(new MemoryStateStore());
        await repository.EnsureLoadedAsync();
        var hashB = new string('4', 64);
        await repository.AddAsync(TestData.State(clock, identity: "store:a", address: "same@coinsnap.app"));
        await repository.AddAsync(TestData.State(clock, "bolt-b", "store:b", "same@coinsnap.app", hashB));

        var parser = Parser(clock);
        parser.Add("bolt-b", hashB, 30_000, clock.UtcNow.AddMinutes(15));
        var service = Service(new ScriptedHttpHandler(), parser, repository, clock);
        var clientA = Client("store:a", "a", service, repository);
        var clientB = Client("store:b", "b", service, repository);

        var invoicesA = await clientA.ListInvoices();
        var invoicesB = await clientB.ListInvoices();

        Assert.Single(invoicesA);
        Assert.Equal(TestData.PaymentHash, invoicesA[0].PaymentHash);
        Assert.Single(invoicesB);
        Assert.Equal(hashB, invoicesB[0].PaymentHash);
        Assert.Null(await clientA.GetInvoice(hashB));
    }

    [Fact]
    public async Task ListenerOnlyReceivesItsStoreSettlement()
    {
        var clock = new TestClock();
        var repository = new CoinsnapInvoiceStateRepository(new MemoryStateStore());
        await repository.EnsureLoadedAsync();
        var preimageB = Convert.ToHexString(Enumerable.Repeat((byte)0x2b, 32).ToArray()).ToLowerInvariant();
        var hashB = Convert.ToHexString(SHA256.HashData(Convert.FromHexString(preimageB))).ToLowerInvariant();
        await repository.AddAsync(TestData.State(clock, identity: "store:a"));
        await repository.AddAsync(TestData.State(clock, "bolt-b", "store:b", hash: hashB));
        var parser = Parser(clock);
        parser.Add("bolt-b", hashB, 30_000, clock.UtcNow.AddMinutes(15));
        var service = Service(new ScriptedHttpHandler(), parser, repository, clock);
        using var listenerA = await Client("store:a", "a", service, repository).Listen();

        await repository.MarkSettledAsync(hashB, preimageB, clock.UtcNow);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listenerA.WaitInvoice(timeout.Token));
    }

    [Fact]
    public async Task RepositoryRejectsSettlementWithoutMatchingPreimage()
    {
        var clock = new TestClock();
        var repository = new CoinsnapInvoiceStateRepository(new MemoryStateStore());
        await repository.EnsureLoadedAsync();
        await repository.AddAsync(TestData.State(clock));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.MarkSettledAsync(TestData.PaymentHash, new string('0', 64), clock.UtcNow));

        Assert.True(repository.TryGet(TestData.PaymentHash, "store:store-a", out var unchanged));
        Assert.False(unchanged.Settled);
    }

    [Fact]
    public async Task ReceiveOnlyOperationsAreUnsupported()
    {
        var clock = new TestClock();
        var repository = new CoinsnapInvoiceStateRepository(new MemoryStateStore());
        await repository.EnsureLoadedAsync();
        var service = Service(new ScriptedHttpHandler(), Parser(clock), repository, clock);
        var client = Client("store:a", "a", service, repository);

        await Assert.ThrowsAsync<NotSupportedException>(() => client.Pay("bolt", new PayInvoiceParams()));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetBalance());
        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetInfo());
        await Assert.ThrowsAsync<NotSupportedException>(() => client.ListChannels());
    }

    [Fact]
    public void PollIntervalsBackOffAsInvoicesAge()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), CoinsnapSettlementPoller.IntervalForAge(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(2), CoinsnapSettlementPoller.IntervalForAge(TimeSpan.FromSeconds(59)));
        Assert.Equal(TimeSpan.FromSeconds(5), CoinsnapSettlementPoller.IntervalForAge(TimeSpan.FromMinutes(1)));
        Assert.Equal(TimeSpan.FromSeconds(5), CoinsnapSettlementPoller.IntervalForAge(TimeSpan.FromMinutes(4)));
        Assert.Equal(TimeSpan.FromSeconds(10), CoinsnapSettlementPoller.IntervalForAge(TimeSpan.FromMinutes(5)));
        Assert.Equal(TimeSpan.FromSeconds(10), CoinsnapSettlementPoller.IntervalForAge(TimeSpan.FromMinutes(14)));
        Assert.Equal(TimeSpan.FromSeconds(30), CoinsnapSettlementPoller.IntervalForAge(TimeSpan.FromMinutes(15)));
        Assert.Equal(TimeSpan.FromSeconds(30), CoinsnapSettlementPoller.IntervalForAge(TimeSpan.FromHours(1)));
        var jittered = CoinsnapSettlementPoller.AddJitter(TimeSpan.FromSeconds(10));
        Assert.InRange(jittered.TotalSeconds, 8, 12);
    }

    private static FakeBolt11Parser Parser(TestClock clock)
    {
        var parser = new FakeBolt11Parser();
        parser.Add("bolt-original", TestData.PaymentHash, 25_000, clock.UtcNow.AddMinutes(15));
        return parser;
    }

    private static CoinsnapLnurlService Service(
        ScriptedHttpHandler handler,
        FakeBolt11Parser parser,
        ICoinsnapInvoiceStateRepository repository,
        TestClock clock) => new(
        new CoinsnapHttpClient(new HttpClient(handler)),
        parser,
        repository,
        clock,
        NullLogger<CoinsnapLnurlService>.Instance);

    private static CoinsnapLnAddressLightningClient Client(
        string identity,
        string storeId,
        CoinsnapLnurlService service,
        ICoinsnapInvoiceStateRepository repository) => new(
        CoinsnapLightningAddress.Parse("same@coinsnap.app"),
        identity,
        storeId,
        Network.Main,
        service,
        repository,
        FakeStoreConfigLookup.Declaring(storeId, "same@coinsnap.app"));
}
