using System.Text.Json;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.AspNetCore.DataProtection;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

/// <summary>
/// Audit findings 3 and 4: preimages are encrypted at rest, and the tracked set is bounded and
/// pruned so a flood through one store cannot grow it, or block other stores, without limit.
/// </summary>
public class StateStorageTests
{
    [Fact]
    public async Task PreimagesAreStoredEncryptedAndLoadAsPlaintext()
    {
        var settings = new JsonSettings();
        var store = new BtcpayCoinsnapStateStore(settings, new EphemeralDataProtectionProvider());

        await store.SaveAsync(Persisted(Settled("store:a")));

        Assert.DoesNotContain(TestData.Preimage, settings.Raw(CoinsnapConstants.SettingsName));
        Assert.Contains("enc:v1:", settings.Raw(CoinsnapConstants.SettingsName));
        var loaded = await store.LoadAsync();
        Assert.Equal(TestData.Preimage, loaded!.Invoices.Single().Preimage);
        Assert.True(loaded.Invoices.Single().Settled);
    }

    [Fact]
    public async Task APlaintextPreimageFromAnEarlierVersionStillLoads()
    {
        var settings = new JsonSettings();
        await settings.UpdateSetting(Persisted(Settled("store:a")), CoinsnapConstants.SettingsName);
        var store = new BtcpayCoinsnapStateStore(settings, new EphemeralDataProtectionProvider());

        var loaded = await store.LoadAsync();

        Assert.Equal(TestData.Preimage, loaded!.Invoices.Single().Preimage);
    }

    /// <summary>A lost key ring must not leave an invoice "settled" without proof; it is re-verified instead.</summary>
    [Fact]
    public async Task AnUndecryptablePreimageSendsTheInvoiceBackToVerification()
    {
        var settings = new JsonSettings();
        await new BtcpayCoinsnapStateStore(settings, new EphemeralDataProtectionProvider())
            .SaveAsync(Persisted(Settled("store:a")));

        var loaded = await new BtcpayCoinsnapStateStore(settings, new EphemeralDataProtectionProvider()).LoadAsync();

        var state = loaded!.Invoices.Single();
        Assert.False(state.Settled);
        Assert.Null(state.Preimage);
    }

    [Fact]
    public async Task AStoreOverItsPendingCapGetsNoNewInvoice()
    {
        var clock = new TestClock();
        var repository = new CoinsnapInvoiceStateRepository(new MemoryStateStore(), clock);
        await repository.EnsureLoadedAsync();
        for (var i = 0; i < CoinsnapRetention.MaxPayablePerStore; i++)
            await repository.AddAsync(Pending(clock, "store:flooded", i));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.AddAsync(Pending(clock, "store:flooded", CoinsnapRetention.MaxPayablePerStore)));

        Assert.Contains("unpaid Coinsnap Lightning invoices", error.Message);
        await repository.AddAsync(Pending(clock, "store:other", 100_000));
    }

    [Fact]
    public async Task SettledInvoicesDoNotCountAgainstThePendingCap()
    {
        var clock = new TestClock();
        var repository = new CoinsnapInvoiceStateRepository(new MemoryStateStore(), clock);
        await repository.EnsureLoadedAsync();
        for (var i = 0; i < CoinsnapRetention.MaxPayablePerStore; i++)
            await repository.AddAsync(Pending(clock, "store:busy", i));
        var first = Pending(clock, "store:busy", 0);
        await repository.MarkSettledAsync(first.PaymentHash, PreimageFor(0), clock.UtcNow);

        await repository.AddAsync(Pending(clock, "store:busy", CoinsnapRetention.MaxPayablePerStore));
    }

    /// <summary>A flood of invoices nobody pays must not keep the store blocked once they can no longer be paid.</summary>
    [Fact]
    public async Task ExpiredInvoicesStopCountingAgainstTheCap()
    {
        var clock = new TestClock();
        var repository = new CoinsnapInvoiceStateRepository(new MemoryStateStore(), clock);
        await repository.EnsureLoadedAsync();
        for (var i = 0; i < CoinsnapRetention.MaxPayablePerStore; i++)
            await repository.AddAsync(Pending(clock, "store:flooded", i));

        clock.UtcNow += TimeSpan.FromMinutes(16);
        await repository.AddAsync(Pending(clock, "store:flooded", CoinsnapRetention.MaxPayablePerStore));

        // The expired ones are still tracked, for late confirmation.
        Assert.Equal(CoinsnapRetention.MaxPayablePerStore + 1, repository.Snapshot().Count);
    }

    /// <summary>Invoices still in their re-check hour are never evicted; the store is refused instead.</summary>
    [Fact]
    public async Task AFullStoreIsRefusedRatherThanLosingInvoicesStillBeingRechecked()
    {
        var clock = new TestClock();
        var repository = await Preloaded(clock, ("store:flooded", CoinsnapRetention.MaxTrackedPerStore, clock.UtcNow.AddMinutes(-30)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.AddAsync(Pending(clock, "store:flooded", 900_000)));

        Assert.Contains("tracking too many", error.Message);
        Assert.Equal(CoinsnapRetention.MaxTrackedPerStore, repository.Snapshot().Count);
    }

    [Fact]
    public async Task AFullStoreEvictsOnlyItsOwnEntriesPastTheRecheckHour()
    {
        var clock = new TestClock();
        var repository = await Preloaded(clock,
            ("store:flooded", CoinsnapRetention.MaxTrackedPerStore, clock.UtcNow.AddHours(-2)),
            ("store:other", 10, clock.UtcNow.AddHours(-2)));

        await repository.AddAsync(Pending(clock, "store:flooded", 900_000));

        var byStore = repository.Snapshot().GroupBy(s => s.ConnectionIdentity).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(CoinsnapRetention.MaxTrackedPerStore, byStore["store:flooded"]);
        Assert.Equal(10, byStore["store:other"]);
    }

    [Fact]
    public async Task AnotherStoreIsUnaffectedByAFullStore()
    {
        var clock = new TestClock();
        var repository = await Preloaded(clock, ("store:flooded", CoinsnapRetention.MaxTrackedPerStore, clock.UtcNow.AddMinutes(-30)));

        await repository.AddAsync(Pending(clock, "store:other", 900_001));
    }

    [Fact]
    public async Task ManyStoresAtTheirPendingCapDoNotBlockAnotherStore()
    {
        var clock = new TestClock();
        var flooded = Enumerable.Range(0, 12)
            .Select(i => ($"store:flooded-{i}", CoinsnapRetention.MaxPayablePerStore, clock.UtcNow.AddMinutes(15)))
            .ToArray();
        var repository = await Preloaded(clock, flooded);

        await repository.AddAsync(Pending(clock, "store:other", 900_002));
    }

    /// <summary>When the server ceiling is full of entries that cannot be evicted, flooded stores block only themselves.</summary>
    [Fact]
    public async Task AFullServerStillAdmitsAStoreBelowItsFairShare()
    {
        var clock = new TestClock();
        var perStore = CoinsnapRetention.MaxTracked / 8;
        var flooded = Enumerable.Range(0, 8)
            .Select(i => ($"store:flooded-{i}", perStore, clock.UtcNow.AddMinutes(-30)))
            .ToArray();
        var repository = await Preloaded(clock, flooded);

        await repository.AddAsync(Pending(clock, "store:light", 900_003));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.AddAsync(Pending(clock, "store:flooded-0", 900_004)));
        Assert.Contains("server is tracking too many", error.Message);
        Assert.Equal(CoinsnapRetention.MaxTracked + 1, repository.Snapshot().Count);
    }

    private static async Task<CoinsnapInvoiceStateRepository> Preloaded(
        TestClock clock, params (string Identity, int Count, DateTimeOffset ExpiresAt)[] groups)
    {
        var durable = new MemoryStateStore();
        var n = 0;
        var invoices = new List<CoinsnapInvoiceState>();
        foreach (var (identity, count, expiresAt) in groups)
            for (var i = 0; i < count; i++)
                invoices.Add(WithExpiry(Pending(clock, identity, n++), expiresAt));
        await durable.SaveAsync(new CoinsnapPersistedInvoiceStates { Invoices = invoices });
        var repository = new CoinsnapInvoiceStateRepository(durable, clock);
        await repository.EnsureLoadedAsync();
        return repository;
    }

    [Fact]
    public async Task EntriesPastRetentionAreDroppedOnLoad()
    {
        var clock = new TestClock();
        var durable = new MemoryStateStore();
        var now = clock.UtcNow;
        await durable.SaveAsync(new CoinsnapPersistedInvoiceStates
        {
            Invoices =
            [
                WithExpiry(Pending(clock, "store:a", 1), now.AddMinutes(10)),
                WithExpiry(Pending(clock, "store:a", 2), now.AddHours(-23)),
                WithExpiry(Pending(clock, "store:a", 3), now.AddHours(-25)),
                WithExpiry(SettledAt(clock, 4), now.AddMinutes(-30)),
                WithExpiry(SettledAt(clock, 5), now.AddMinutes(-90))
            ]
        });
        var repository = new CoinsnapInvoiceStateRepository(durable, clock);

        await repository.EnsureLoadedAsync();

        var kept = repository.Snapshot().Select(s => s.PaymentHash).Order().ToList();
        Assert.Equal(new[] { Hash(1), Hash(2), Hash(4) }.Order(), kept);
    }

    private static CoinsnapPersistedInvoiceStates Persisted(params CoinsnapInvoiceState[] states) =>
        new() { Invoices = states.ToList() };

    private static CoinsnapInvoiceState Settled(string identity)
    {
        var state = TestData.State(new TestClock(), identity: identity);
        state.Settled = true;
        state.Preimage = TestData.Preimage;
        state.PaidAt = state.CreatedAt;
        return state;
    }

    private static CoinsnapInvoiceState Pending(TestClock clock, string identity, int n) =>
        TestData.State(clock, raw: $"bolt-{n}", identity: identity, hash: Hash(n));

    private static CoinsnapInvoiceState SettledAt(TestClock clock, int n)
    {
        var state = Pending(clock, "store:a", n);
        state.Settled = true;
        state.Preimage = PreimageFor(n);
        return state;
    }

    private static CoinsnapInvoiceState WithExpiry(CoinsnapInvoiceState state, DateTimeOffset expiresAt)
    {
        state.CreatedAt = expiresAt.AddMinutes(-15);
        state.ExpiresAt = expiresAt;
        return state;
    }

    // Distinct, valid preimage/hash pairs so MarkSettledAsync's proof check passes.
    private static string PreimageFor(int n) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"preimage-{n}"))).ToLowerInvariant();

    private static string Hash(int n) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Convert.FromHexString(PreimageFor(n)))).ToLowerInvariant();

    /// <summary>Round-trips through JSON like the real settings table, so stored copies are independent.</summary>
    private sealed class JsonSettings : ISettingsRepository
    {
        private readonly Dictionary<string, string> _values = new();

        public string Raw(string name) => _values[name];

        public Task<T?> GetSettingAsync<T>(string? name = null) where T : class =>
            Task.FromResult(_values.TryGetValue(name!, out var json) ? JsonSerializer.Deserialize<T>(json) : null);

        public Task UpdateSetting<T>(T obj, string? name = null) where T : class
        {
            _values[name!] = JsonSerializer.Serialize(obj);
            return Task.CompletedTask;
        }

        public Task<T> WaitSettingsChanged<T>(CancellationToken cancellationToken = default) where T : class =>
            throw new NotSupportedException();
    }
}
