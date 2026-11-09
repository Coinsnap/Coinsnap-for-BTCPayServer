using System.Net;
using System.Security.Cryptography;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging.Abstractions;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

internal sealed class TestClock : ICoinsnapClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
}

internal sealed class MemoryStateStore : ICoinsnapStateStore
{
    private CoinsnapPersistedInvoiceStates? _state;

    public Task<CoinsnapPersistedInvoiceStates?> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Clone(_state));

    public Task SaveAsync(CoinsnapPersistedInvoiceStates state, CancellationToken cancellationToken = default)
    {
        _state = Clone(state);
        return Task.CompletedTask;
    }

    private static CoinsnapPersistedInvoiceStates? Clone(CoinsnapPersistedInvoiceStates? state) =>
        state is null ? null : new CoinsnapPersistedInvoiceStates
        {
            Version = state.Version,
            Invoices = state.Invoices.Select(s => s.Clone()).ToList()
        };
}

internal sealed class FakeBolt11Parser : ICoinsnapBolt11Parser
{
    private readonly Dictionary<string, CoinsnapBolt11> _invoices = new(StringComparer.Ordinal);
    public Network? RequiredNetwork { get; set; }

    public void Add(
        string raw,
        string paymentHash,
        long amountMsat,
        DateTimeOffset expiresAt,
        string? descriptionHash = null)
    {
        _invoices[raw] = new CoinsnapBolt11(
            raw,
            paymentHash,
            LightMoney.MilliSatoshis(amountMsat),
            expiresAt,
            descriptionHash);
    }

    public CoinsnapBolt11 Parse(string paymentRequest, Network network)
    {
        if (RequiredNetwork is not null && network != RequiredNetwork)
            throw new FormatException("wrong network");
        return _invoices.TryGetValue(paymentRequest, out var parsed)
            ? parsed
            : throw new FormatException("unknown fake BOLT11");
    }
}

internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();
    public List<Uri> Requests { get; } = [];

    public void EnqueueJson(string json, HttpStatusCode status = HttpStatusCode.OK, Action<HttpResponseMessage>? configure = null)
    {
        _responses.Enqueue((_, _) =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            configure?.Invoke(response);
            return Task.FromResult(response);
        });
    }

    public void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
        _responses.Enqueue(response);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        if (_responses.Count == 0)
            throw new InvalidOperationException("No scripted HTTP response remains.");
        return _responses.Dequeue()(request, cancellationToken);
    }
}

internal sealed class SingleHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;
    public SingleHttpClientFactory(HttpClient client) => _client = client;
    public HttpClient CreateClient(string name) => _client;
}

internal static class TestData
{
    public static string Binding(string storeId) => $"b-{storeId}";

    public const string MainnetBolt11 =
        "lnbc2500u1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpuaztrnwngzn3kdzw5hydlzf03qdgm2hdq27cqv3agm2awhz5se903vruatfhq77w3ls4evs3ch9zw97j25emudupq63nyw24cg27h2rspfj9srp";

    public static readonly string Preimage = Convert.ToHexString(Enumerable.Repeat((byte)0x2a, 32).ToArray()).ToLowerInvariant();
    public static readonly string PaymentHash = Convert.ToHexString(
        SHA256.HashData(Convert.FromHexString(Preimage))).ToLowerInvariant();

    public static string Metadata(
        string callback = "https://coinsnap.app/lnurlp/yourname/invoice",
        long min = 1000,
        long max = 10_000_000,
        string tag = "payRequest",
        string metadata = "[[\"text/plain\",\"Pay to yourname@coinsnap.app\"]]") =>
        $$"""{"tag":"{{tag}}","callback":"{{callback}}","minSendable":{{min}},"maxSendable":{{max}},"metadata":{{System.Text.Json.JsonSerializer.Serialize(metadata)}}}""";

    public static CoinsnapInvoiceState State(
        TestClock clock,
        string raw = "bolt-original",
        string? identity = null,
        string? address = null,
        string? hash = null) => new()
    {
        PaymentHash = hash ?? PaymentHash,
        Bolt11 = raw,
        VerifyUrl = $"https://coinsnap.app/verify/{hash ?? PaymentHash}",
        LightningAddress = address ?? "yourname@coinsnap.app",
        ConnectionIdentity = identity ?? "store:store-a",
        StoreId = identity?.StartsWith("store:", StringComparison.Ordinal) is true ? identity[6..] : "store-a",
        CreatedAt = clock.UtcNow,
        ExpiresAt = clock.UtcNow.AddMinutes(15)
    };

    public static async Task<(CoinsnapLnurlService Service, CoinsnapInvoiceStateRepository Repository)> Service(
        ScriptedHttpHandler handler,
        FakeBolt11Parser parser,
        TestClock clock,
        MemoryStateStore? store = null)
    {
        var repository = new CoinsnapInvoiceStateRepository(store ?? new MemoryStateStore(), clock);
        await repository.EnsureLoadedAsync();
        var service = new CoinsnapLnurlService(
            new CoinsnapHttpClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) }),
            parser,
            repository,
            clock,
            NullLogger<CoinsnapLnurlService>.Instance);
        return (service, repository);
    }
}

internal sealed class FakeStoreConfigLookup : ICoinsnapStoreConfigStore
{
    public Dictionary<string, string> ConnectionStrings { get; } = new(StringComparer.Ordinal);
    public List<string> Removed { get; } = [];
    public HashSet<string> FailingStores { get; } = new(StringComparer.Ordinal);
    public Exception? Failure { get; set; }

    public Task<IReadOnlyCollection<string>> GetStoreIdsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<string>>(ConnectionStrings.Keys.ToArray());

    public Task RemoveLightningConnectionAsync(string storeId, CancellationToken cancellationToken = default)
    {
        ConnectionStrings.Remove(storeId);
        Removed.Add(storeId);
        return Task.CompletedTask;
    }

    public static FakeStoreConfigLookup Declaring(string storeId, string address)
    {
        var lookup = new FakeStoreConfigLookup();
        lookup.ConnectionStrings[storeId] = $"type=coinsnap;ln-address={address};store-id={storeId};";
        return lookup;
    }

    public Task<string?> GetLightningConnectionStringAsync(string storeId, CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
            return Task.FromException<string?>(Failure);
        if (FailingStores.Contains(storeId))
            return Task.FromException<string?>(new InvalidOperationException($"store {storeId} unavailable"));
        return Task.FromResult(ConnectionStrings.TryGetValue(storeId, out var value) ? value : null);
    }
}

/// <summary>Deterministic stand-in for the server-keyed binding; real HMAC behaviour has its own tests.</summary>
internal sealed class FakeStoreBinding : ICoinsnapStoreBinding
{
    public string Sign(string storeId) => TestData.Binding(storeId);
    public bool Verify(string storeId, string? binding) => binding == Sign(storeId);
    public Task RotateAsync(string storeId) => Task.CompletedTask;
    public IReadOnlyCollection<string> BoundStores => [];
}
