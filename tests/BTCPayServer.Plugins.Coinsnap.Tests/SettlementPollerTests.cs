using System.Net;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging.Abstractions;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

/// <summary>
/// The poller must never lose track of an invoice that can still be paid or confirmed: not on a
/// remote "Not found" (audit finding B), and not on the first "expired" answer (finding C).
/// </summary>
public class SettlementPollerTests
{
    private const string NotFound = "{\"status\":\"ERROR\",\"reason\":\"Not found\"}";
    private const string Unsettled = "{\"status\":\"OK\",\"settled\":false,\"preimage\":null,\"pr\":\"bolt-original\"}";
    private static readonly string Settled =
        $$"""{"status":"OK","settled":true,"preimage":"{{TestData.Preimage}}","pr":"bolt-original"}""";

    [Fact]
    public async Task RepeatedNotFoundNeverDropsAPayableInvoice()
    {
        var f = await Fixture();
        for (var i = 0; i < 10; i++)
            f.Http.EnqueueJson(NotFound);

        for (var i = 0; i < 10; i++)
        {
            await f.Poller.PollOnceAsync(CancellationToken.None);
            f.Clock.UtcNow += TimeSpan.FromSeconds(40);
        }

        Assert.Equal(10, f.Http.Requests.Count);
        Assert.True(f.Repository.TryGet(TestData.PaymentHash, "store:store-a", out var state));
        Assert.False(state.Settled);
    }

    [Fact]
    public async Task APaymentConfirmedAfterAStreakOfNotFoundIsRecorded()
    {
        var f = await Fixture();
        for (var i = 0; i < 5; i++)
            f.Http.EnqueueJson(NotFound);
        f.Http.EnqueueJson(Settled);

        for (var i = 0; i < 6; i++)
        {
            await f.Poller.PollOnceAsync(CancellationToken.None);
            f.Clock.UtcNow += TimeSpan.FromSeconds(40);
        }

        Assert.True(f.Repository.TryGet(TestData.PaymentHash, "store:store-a", out var state));
        Assert.True(state.Settled);
        Assert.Equal(TestData.Preimage, state.Preimage);
    }

    [Fact]
    public async Task AnExpiredInvoiceIsRecheckedAndALateConfirmationIsRecorded()
    {
        var f = await Fixture();
        f.Clock.UtcNow = f.State.ExpiresAt.AddSeconds(5);
        f.Http.EnqueueJson(Unsettled);
        f.Http.EnqueueJson(Settled);

        await f.Poller.PollOnceAsync(CancellationToken.None);
        f.Clock.UtcNow += TimeSpan.FromMinutes(7);
        await f.Poller.PollOnceAsync(CancellationToken.None);

        Assert.Equal(2, f.Http.Requests.Count);
        Assert.True(f.Repository.TryGet(TestData.PaymentHash, "store:store-a", out var state));
        Assert.True(state.Settled);
    }

    [Fact]
    public async Task AnExpiredInvoiceIsDroppedOnlyAfterACheckPastTheGrace()
    {
        var f = await Fixture();
        f.Clock.UtcNow = f.State.ExpiresAt.AddSeconds(5);
        for (var i = 0; i < 20; i++)
            f.Http.EnqueueJson(Unsettled);

        while (f.Clock.UtcNow < f.State.ExpiresAt + TimeSpan.FromMinutes(59))
        {
            await f.Poller.PollOnceAsync(CancellationToken.None);
            Assert.True(f.Repository.TryGet(TestData.PaymentHash, "store:store-a", out _));
            f.Clock.UtcNow += TimeSpan.FromMinutes(7);
        }
        var checksDuringGrace = f.Http.Requests.Count;

        // Past the grace hour and past the last re-check's jittered schedule.
        f.Clock.UtcNow = f.State.ExpiresAt + TimeSpan.FromMinutes(70);
        await f.Poller.PollOnceAsync(CancellationToken.None);

        Assert.True(checksDuringGrace >= 8, $"Only {checksDuringGrace} re-checks during the grace hour.");
        Assert.Equal(checksDuringGrace + 1, f.Http.Requests.Count);
        Assert.False(f.Repository.TryGet(TestData.PaymentHash, "store:store-a", out _));
    }

    [Fact]
    public async Task NotFoundEndsTrackingOnlyAfterTheGrace()
    {
        var f = await Fixture();
        f.Clock.UtcNow = f.State.ExpiresAt + TimeSpan.FromMinutes(30);
        f.Http.EnqueueJson(NotFound);
        f.Http.EnqueueJson(NotFound);

        await f.Poller.PollOnceAsync(CancellationToken.None);
        Assert.True(f.Repository.TryGet(TestData.PaymentHash, "store:store-a", out _));

        f.Clock.UtcNow = f.State.ExpiresAt + TimeSpan.FromMinutes(61);
        await f.Poller.PollOnceAsync(CancellationToken.None);
        Assert.False(f.Repository.TryGet(TestData.PaymentHash, "store:store-a", out _));
    }

    [Fact]
    public async Task AnUnreachableServerPastTheGraceKeepsTheInvoiceUntilTheGiveUpLimit()
    {
        var f = await Fixture();
        f.Clock.UtcNow = f.State.ExpiresAt + TimeSpan.FromHours(2);
        f.Http.EnqueueJson("", HttpStatusCode.ServiceUnavailable);
        f.Http.EnqueueJson("", HttpStatusCode.ServiceUnavailable);

        await f.Poller.PollOnceAsync(CancellationToken.None);
        Assert.True(f.Repository.TryGet(TestData.PaymentHash, "store:store-a", out _));

        f.Clock.UtcNow = f.State.ExpiresAt + TimeSpan.FromHours(25);
        await f.Poller.PollOnceAsync(CancellationToken.None);
        Assert.False(f.Repository.TryGet(TestData.PaymentHash, "store:store-a", out _));
    }

    [Fact]
    public async Task GetInvoiceOnNotFoundKeepsBtcpayWatching()
    {
        var f = await Fixture();
        f.Http.EnqueueJson(NotFound);
        var client = new CoinsnapLnAddressLightningClient(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"),
            "store:store-a",
            "store-a",
            Network.Main,
            f.Service,
            f.Repository,
            FakeStoreConfigLookup.Declaring("store-a", "yourname@coinsnap.app"));

        var invoice = await client.GetInvoice(TestData.PaymentHash);

        Assert.NotNull(invoice);
        Assert.Equal(LightningInvoiceStatus.Unpaid, invoice.Status);
        Assert.True(f.Repository.TryGet(TestData.PaymentHash, "store:store-a", out _));
    }

    private sealed record PollerFixture(
        TestClock Clock,
        ScriptedHttpHandler Http,
        CoinsnapInvoiceStateRepository Repository,
        CoinsnapLnurlService Service,
        CoinsnapSettlementPoller Poller,
        CoinsnapInvoiceState State);

    private static async Task<PollerFixture> Fixture()
    {
        var clock = new TestClock();
        var parser = new FakeBolt11Parser();
        parser.Add("bolt-original", TestData.PaymentHash, 25_000, clock.UtcNow.AddMinutes(15));
        var http = new ScriptedHttpHandler();
        var (service, repository) = await TestData.Service(http, parser, clock);
        var state = TestData.State(clock);
        await repository.AddAsync(state);
        var poller = new CoinsnapSettlementPoller(
            repository,
            new SingleHttpClientFactory(new HttpClient(http) { Timeout = TimeSpan.FromSeconds(2) }),
            parser,
            clock,
            NullLoggerFactory.Instance);
        return new PollerFixture(clock, http, repository, service, poller, state);
    }
}
