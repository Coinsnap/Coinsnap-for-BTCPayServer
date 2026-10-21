using System.Security.Cryptography;
using System.Text;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging.Abstractions;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

/// <summary>
/// Audit finding G: checkout must not stall on every invoice when Coinsnap is slow or down. Metadata
/// is reused briefly, and repeated network failures make invoice creation fail fast for a while.
/// </summary>
public class AvailabilityTests
{
    [Fact]
    public async Task MetadataIsReusedWithinItsLifetime()
    {
        var f = Fixture();
        f.Http.EnqueueJson(TestData.Metadata());
        f.Http.EnqueueJson(Callback(1));
        f.Http.EnqueueJson(Callback(2));

        await Create(f);
        await Create(f);

        Assert.Equal(3, f.Http.Requests.Count);
    }

    [Fact]
    public async Task MetadataIsFetchedAgainOnceItExpires()
    {
        var f = Fixture();
        f.Http.EnqueueJson(TestData.Metadata());
        f.Http.EnqueueJson(Callback(1));
        f.Http.EnqueueJson(TestData.Metadata());
        f.Http.EnqueueJson(Callback(2));

        await Create(f);
        f.Clock.UtcNow += CoinsnapAvailability.MetadataTtl + TimeSpan.FromSeconds(1);
        await Create(f);

        Assert.Equal(4, f.Http.Requests.Count);
    }

    [Fact]
    public async Task RepeatedNetworkFailuresMakeCreationFailFastForAWhile()
    {
        var f = Fixture();
        for (var i = 0; i < CoinsnapAvailability.FailuresToOpen; i++)
            f.Http.Enqueue((_, _) => throw new HttpRequestException("connection refused"));
        for (var i = 0; i < CoinsnapAvailability.FailuresToOpen; i++)
            await Assert.ThrowsAsync<HttpRequestException>(() => Create(f));
        var requestsWhenOpened = f.Http.Requests.Count;

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Create(f));

        Assert.Contains("unreachable moments ago", error.Message);
        Assert.Equal(requestsWhenOpened, f.Http.Requests.Count);

        f.Clock.UtcNow += CoinsnapAvailability.OpenFor + TimeSpan.FromSeconds(1);
        f.Http.EnqueueJson(TestData.Metadata());
        f.Http.EnqueueJson(Callback(1));
        await Create(f);
    }

    [Fact]
    public async Task ARefusedAnswerDoesNotTripTheBreaker()
    {
        var f = Fixture();
        for (var i = 0; i < CoinsnapAvailability.FailuresToOpen; i++)
        {
            f.Http.EnqueueJson(TestData.Metadata());
            f.Http.EnqueueJson("{\"status\":\"ERROR\",\"reason\":\"Amount not allowed\"}");
            f.Clock.UtcNow += CoinsnapAvailability.MetadataTtl + TimeSpan.FromSeconds(1);
            await Assert.ThrowsAsync<FormatException>(() => Create(f));
        }

        f.Http.EnqueueJson(TestData.Metadata());
        f.Http.EnqueueJson(Callback(1));
        f.Clock.UtcNow += CoinsnapAvailability.MetadataTtl + TimeSpan.FromSeconds(1);
        await Create(f);
    }

    [Fact]
    public async Task ASuccessResetsTheFailureCount()
    {
        var f = Fixture();
        f.Http.Enqueue((_, _) => throw new HttpRequestException("blip"));
        f.Http.Enqueue((_, _) => throw new HttpRequestException("blip"));
        f.Http.EnqueueJson(TestData.Metadata());
        f.Http.EnqueueJson(Callback(1));
        f.Http.Enqueue((_, _) => throw new HttpRequestException("blip"));
        f.Http.Enqueue((_, _) => throw new HttpRequestException("blip"));
        f.Http.EnqueueJson(TestData.Metadata());
        f.Http.EnqueueJson(Callback(2));

        await Assert.ThrowsAsync<HttpRequestException>(() => Create(f));
        await Assert.ThrowsAsync<HttpRequestException>(() => Create(f));
        await Create(f);
        await Assert.ThrowsAsync<HttpRequestException>(() => Create(f));
        await Assert.ThrowsAsync<HttpRequestException>(() => Create(f));
        await Create(f);
    }

    [Fact]
    public async Task AnAddressCoinsnapRejectsDoesNotTripTheBreaker()
    {
        var f = Fixture();
        for (var i = 0; i < CoinsnapAvailability.FailuresToOpen + 1; i++)
        {
            f.Http.EnqueueJson("{}", System.Net.HttpStatusCode.NotFound);
            await Assert.ThrowsAsync<HttpRequestException>(() => Create(f));
        }

        f.Http.EnqueueJson(TestData.Metadata());
        f.Http.EnqueueJson(Callback(1));
        await Create(f);
    }

    [Fact]
    public async Task OneAddressesOutageDoesNotPauseAnother()
    {
        var f = Fixture();
        for (var i = 0; i < CoinsnapAvailability.FailuresToOpen; i++)
        {
            f.Http.Enqueue((_, _) => throw new HttpRequestException("connection refused"));
            await Assert.ThrowsAsync<HttpRequestException>(() => Create(f, "broken@coinsnap.app"));
        }
        await Assert.ThrowsAsync<HttpRequestException>(() => Create(f, "broken@coinsnap.app"));

        f.Http.EnqueueJson(TestData.Metadata());
        f.Http.EnqueueJson(Callback(1));
        await Create(f);
    }

    /// <summary>Names may be case-sensitive at Coinsnap; one account's callback must never serve another.</summary>
    [Fact]
    public async Task AddressesDifferingOnlyInCaseDoNotShareMetadata()
    {
        var f = Fixture();
        f.Http.EnqueueJson(TestData.Metadata());
        f.Http.EnqueueJson(Callback(1));
        f.Http.EnqueueJson(TestData.Metadata());
        f.Http.EnqueueJson(Callback(2));

        await Create(f, "Alice@coinsnap.app");
        await Create(f, "alice@coinsnap.app");

        Assert.Equal(4, f.Http.Requests.Count);
    }

    /// <summary>Metadata is cached only after it produced a valid invoice.</summary>
    [Fact]
    public async Task MetadataThatFailsIsNotReused()
    {
        var f = Fixture();
        f.Http.EnqueueJson(TestData.Metadata(min: 200_000_000, max: 250_000_000));
        await Assert.ThrowsAnyAsync<Exception>(() => Create(f));

        f.Http.EnqueueJson(TestData.Metadata());
        f.Http.EnqueueJson(Callback(1));
        await Create(f);

        Assert.Equal(3, f.Http.Requests.Count);
    }

    [Fact]
    public async Task CachedMetadataIsDroppedWhenCreationFails()
    {
        var f = Fixture();
        f.Http.EnqueueJson(TestData.Metadata());
        f.Http.EnqueueJson(Callback(1));
        await Create(f);

        f.Http.EnqueueJson("{}", System.Net.HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<HttpRequestException>(() => Create(f));

        f.Http.EnqueueJson(TestData.Metadata());
        f.Http.EnqueueJson(Callback(2));
        await Create(f);

        Assert.Equal(5, f.Http.Requests.Count);
    }

    private sealed record AvailabilityFixture(TestClock Clock, ScriptedHttpHandler Http, CoinsnapLnurlService Service);

    private static AvailabilityFixture Fixture()
    {
        var clock = new TestClock();
        var parser = new FakeBolt11Parser();
        for (var n = 1; n <= 2; n++)
            parser.Add($"bolt-{n}", Hash(n), 25_000, clock.UtcNow.AddMinutes(15));
        var http = new ScriptedHttpHandler();
        var repository = new CoinsnapInvoiceStateRepository(new MemoryStateStore(), clock);
        repository.EnsureLoadedAsync().GetAwaiter().GetResult();
        var service = new CoinsnapLnurlService(
            new CoinsnapHttpClient(new HttpClient(http) { Timeout = TimeSpan.FromSeconds(2) }),
            // The BOLT11 expiry is checked against the request, so keep it current as the clock moves.
            new ShiftingParser(parser, clock),
            repository,
            clock,
            NullLogger<CoinsnapLnurlService>.Instance,
            new CoinsnapAvailability());
        return new AvailabilityFixture(clock, http, service);
    }

    private static Task<LightningInvoice> Create(AvailabilityFixture f, string address = "yourname@coinsnap.app") =>
        f.Service.CreateInvoiceAsync(
        CoinsnapLightningAddress.Parse(address), "store:a", "a", Network.Main,
        LightMoney.MilliSatoshis(25_000), TimeSpan.FromMinutes(15), null, CancellationToken.None);

    private static string Callback(int n) =>
        $$"""{"pr":"bolt-{{n}}","verify":"https://coinsnap.app/verify/{{Hash(n)}}"}""";

    private static string Hash(int n) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"invoice-{n}"))).ToLowerInvariant();

    /// <summary>Reports each BOLT11 as expiring 15 minutes from the current test time.</summary>
    private sealed class ShiftingParser(FakeBolt11Parser inner, TestClock clock) : ICoinsnapBolt11Parser
    {
        public CoinsnapBolt11 Parse(string paymentRequest, Network network)
        {
            var parsed = inner.Parse(paymentRequest, network);
            return parsed with { ExpiresAt = clock.UtcNow.AddMinutes(15) };
        }
    }
}
