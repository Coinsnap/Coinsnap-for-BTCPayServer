using System.Net;
using BTCPayServer.Lightning;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

public class SettlementTests
{
    [Fact]
    public async Task UnsettledValidInvoiceIsPending()
    {
        var fixture = await Fixture("{\"status\":\"OK\",\"settled\":false,\"preimage\":null,\"pr\":\"bolt-original\"}");
        var result = await fixture.Service.VerifyAsync(fixture.State, Network.Main, CancellationToken.None);
        Assert.Equal(CoinsnapVerificationOutcome.Pending, result.Outcome);
        Assert.Equal(LightningInvoiceStatus.Unpaid, result.Invoice!.Status);
    }

    [Fact]
    public async Task SettledInvoiceIsPaidOnlyWithValidPreimage()
    {
        var fixture = await Fixture($$"""{"status":"OK","settled":true,"preimage":"{{TestData.Preimage}}","pr":"bolt-original"}""");
        var result = await fixture.Service.VerifyAsync(fixture.State, Network.Main, CancellationToken.None);
        Assert.Equal(CoinsnapVerificationOutcome.Paid, result.Outcome);
        Assert.Equal(LightningInvoiceStatus.Paid, result.Invoice!.Status);
        Assert.Equal(TestData.Preimage, result.Invoice.Preimage);
        Assert.Equal(result.Invoice.Amount, result.Invoice.AmountReceived);
    }

    [Fact]
    public async Task ExpiredAndUnsettledMapsExpired()
    {
        var fixture = await Fixture("{\"status\":\"OK\",\"settled\":false,\"preimage\":null,\"pr\":\"bolt-original\"}");
        fixture.Clock.UtcNow = fixture.State.ExpiresAt.AddSeconds(1);
        var result = await fixture.Service.VerifyAsync(fixture.State, Network.Main, CancellationToken.None);
        Assert.Equal(CoinsnapVerificationOutcome.Expired, result.Outcome);
        Assert.Equal(LightningInvoiceStatus.Expired, result.Invoice!.Status);
    }

    [Fact]
    public async Task ClientKeepsExpiredInvoiceAvailableDuringCleanupGrace()
    {
        var clock = new TestClock();
        var parser = Parser(clock);
        var handler = new ScriptedHttpHandler();
        handler.EnqueueJson("{\"status\":\"OK\",\"settled\":false,\"preimage\":null,\"pr\":\"bolt-original\"}");
        var (service, repository) = await TestData.Service(handler, parser, clock);
        var state = TestData.State(clock);
        await repository.AddAsync(state);
        clock.UtcNow = state.ExpiresAt.AddSeconds(1);
        var client = new CoinsnapLnAddressLightningClient(
            CoinsnapLightningAddress.Parse("yourname@coinsnap.app"),
            "store:store-a",
            "store-a",
            Network.Main,
            service,
            repository,
            FakeStoreConfigLookup.Declaring("store-a", "yourname@coinsnap.app"));

        var invoice = await client.GetInvoice(TestData.PaymentHash);

        Assert.Equal(LightningInvoiceStatus.Expired, invoice!.Status);
        Assert.True(repository.TryGet(TestData.PaymentHash, "store:store-a", out _));
    }

    /// <summary>Unknown to the server is not unknown to the store: BTCPay must keep watching it.</summary>
    [Fact]
    public async Task NotFoundMapsUnknownButStillReportsTheUnpaidInvoice()
    {
        var fixture = await Fixture("{\"status\":\"ERROR\",\"reason\":\"Not found\"}");
        var result = await fixture.Service.VerifyAsync(fixture.State, Network.Main, CancellationToken.None);
        Assert.Equal(CoinsnapVerificationOutcome.Unknown, result.Outcome);
        Assert.Equal(LightningInvoiceStatus.Unpaid, result.Invoice!.Status);
        Assert.Equal(TestData.PaymentHash, result.Invoice.PaymentHash);
    }

    [Fact]
    public async Task ServerErrorLeavesStatePendingForRetry()
    {
        var fixture = await Fixture("{\"status\":\"ERROR\",\"reason\":\"Internal server error\"}");
        var result = await fixture.Service.VerifyAsync(fixture.State, Network.Main, CancellationToken.None);
        Assert.Equal(CoinsnapVerificationOutcome.Retry, result.Outcome);
        Assert.Equal(LightningInvoiceStatus.Unpaid, result.Invoice!.Status);
    }

    [Fact]
    public async Task Http429HonorsRetryAfterWithoutChangingState()
    {
        var fixture = await Fixture(
            "{}",
            HttpStatusCode.TooManyRequests,
            response => response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42)));
        var result = await fixture.Service.VerifyAsync(fixture.State, Network.Main, CancellationToken.None);
        Assert.Equal(CoinsnapVerificationOutcome.Retry, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(42), result.RetryAfter);
    }

    [Fact]
    public async Task TimeoutLeavesStatePendingForRetry()
    {
        var clock = new TestClock();
        var parser = Parser(clock);
        var handler = new ScriptedHttpHandler();
        handler.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout")));
        var (service, repository) = await TestData.Service(handler, parser, clock);
        var state = TestData.State(clock);
        await repository.AddAsync(state);
        var result = await service.VerifyAsync(state, Network.Main, CancellationToken.None);
        Assert.Equal(CoinsnapVerificationOutcome.Retry, result.Outcome);
    }

    [Fact]
    public async Task InvalidJsonNeverMapsPaid()
    {
        var fixture = await Fixture("not json");
        var result = await fixture.Service.VerifyAsync(fixture.State, Network.Main, CancellationToken.None);
        Assert.Equal(CoinsnapVerificationOutcome.Retry, result.Outcome);
        Assert.NotEqual(LightningInvoiceStatus.Paid, result.Invoice!.Status);
    }

    [Fact]
    public async Task InvalidPreimageNeverMapsPaidEvenAfterExpiry()
    {
        var fixture = await Fixture("{\"status\":\"OK\",\"settled\":true,\"preimage\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"pr\":\"bolt-original\"}");
        fixture.Clock.UtcNow = fixture.State.ExpiresAt.AddSeconds(1);
        var result = await fixture.Service.VerifyAsync(fixture.State, Network.Main, CancellationToken.None);
        Assert.Equal(CoinsnapVerificationOutcome.Retry, result.Outcome);
        Assert.Equal(LightningInvoiceStatus.Unpaid, result.Invoice!.Status);
    }

    [Fact]
    public async Task ReturnedBolt11HashMismatchNeverMapsPaid()
    {
        var clock = new TestClock();
        var parser = Parser(clock);
        parser.Add("bolt-wrong", new string('1', 64), 25_000, clock.UtcNow.AddMinutes(15));
        var response = $$"""{"status":"OK","settled":true,"preimage":"{{TestData.Preimage}}","pr":"bolt-wrong"}""";
        var fixture = await Fixture(response, parser: parser, clock: clock);
        var result = await fixture.Service.VerifyAsync(fixture.State, Network.Main, CancellationToken.None);
        Assert.Equal(CoinsnapVerificationOutcome.Retry, result.Outcome);
        Assert.NotEqual(LightningInvoiceStatus.Paid, result.Invoice!.Status);
    }

    [Fact]
    public void CryptographicPreimageCheckIsExact()
    {
        Assert.True(CoinsnapLnurlService.IsValidPreimage(TestData.Preimage, TestData.PaymentHash));
        Assert.False(CoinsnapLnurlService.IsValidPreimage(new string('0', 64), TestData.PaymentHash));
        Assert.False(CoinsnapLnurlService.IsValidPreimage("not-hex", TestData.PaymentHash));
    }

    private static FakeBolt11Parser Parser(TestClock clock)
    {
        var parser = new FakeBolt11Parser();
        parser.Add("bolt-original", TestData.PaymentHash, 25_000, clock.UtcNow.AddMinutes(15));
        return parser;
    }

    private static async Task<SettlementFixture> Fixture(
        string json,
        HttpStatusCode status = HttpStatusCode.OK,
        Action<HttpResponseMessage>? configure = null,
        FakeBolt11Parser? parser = null,
        TestClock? clock = null)
    {
        clock ??= new TestClock();
        parser ??= Parser(clock);
        var handler = new ScriptedHttpHandler();
        handler.EnqueueJson(json, status, configure);
        var (service, repository) = await TestData.Service(handler, parser, clock);
        var state = TestData.State(clock);
        await repository.AddAsync(state);
        return new SettlementFixture(service, state, clock);
    }

    private sealed record SettlementFixture(CoinsnapLnurlService Service, CoinsnapInvoiceState State, TestClock Clock);
}
