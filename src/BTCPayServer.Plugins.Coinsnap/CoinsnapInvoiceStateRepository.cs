using System.Collections.Concurrent;
using System.Security.Cryptography;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.AspNetCore.DataProtection;

namespace BTCPayServer.Plugins.Coinsnap;

internal interface ICoinsnapStateStore
{
    Task<CoinsnapPersistedInvoiceStates?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CoinsnapPersistedInvoiceStates state, CancellationToken cancellationToken = default);
}

/// <summary>
/// Persists invoice state in one BTCPay setting. Preimages are encrypted with BTCPay's Data Protection
/// keys, so a database dump or another plugin reading the setting sees only ciphertext.
/// </summary>
internal sealed class BtcpayCoinsnapStateStore : ICoinsnapStateStore
{
    private const string EncryptedPrefix = "enc:v1:";

    private readonly ISettingsRepository _settings;
    private readonly IDataProtector _protector;

    public BtcpayCoinsnapStateStore(ISettingsRepository settings, IDataProtectionProvider dataProtection)
    {
        _settings = settings;
        _protector = dataProtection.CreateProtector("BTCPayServer.Plugins.Coinsnap.Preimage.v1");
    }

    public async Task<CoinsnapPersistedInvoiceStates?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _settings.GetSettingAsync<CoinsnapPersistedInvoiceStates>(CoinsnapConstants.SettingsName);
        foreach (var state in stored?.Invoices ?? [])
            state.Preimage = Reveal(state);
        return stored;
    }

    public Task SaveAsync(CoinsnapPersistedInvoiceStates state, CancellationToken cancellationToken = default)
    {
        var sealedCopy = new CoinsnapPersistedInvoiceStates
        {
            Version = state.Version,
            Invoices = state.Invoices.Select(Seal).ToList()
        };
        return _settings.UpdateSetting(sealedCopy, CoinsnapConstants.SettingsName);
    }

    private CoinsnapInvoiceState Seal(CoinsnapInvoiceState state)
    {
        var copy = state.Clone();
        if (!string.IsNullOrEmpty(copy.Preimage))
            copy.Preimage = EncryptedPrefix + _protector.Protect(copy.Preimage);
        return copy;
    }

    // Older entries are plaintext and are encrypted on the next save. One that can no longer be
    // decrypted, e.g. after the key ring was lost, is marked unsettled so the server is asked again.
    private string? Reveal(CoinsnapInvoiceState state)
    {
        if (state.Preimage is not { } stored || !stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            return state.Preimage;
        try
        {
            return _protector.Unprotect(stored[EncryptedPrefix.Length..]);
        }
        catch (CryptographicException)
        {
            state.Settled = false;
            state.PaidAt = null;
            return null;
        }
    }
}

internal interface ICoinsnapInvoiceStateRepository
{
    event Action<CoinsnapInvoiceState>? Settled;
    Task EnsureLoadedAsync(CancellationToken cancellationToken = default);
    Task AddAsync(CoinsnapInvoiceState state, CancellationToken cancellationToken = default);
    Task MarkSettledAsync(string paymentHash, string preimage, DateTimeOffset paidAt, CancellationToken cancellationToken = default);
    Task RemoveAsync(string paymentHash, string connectionIdentity, CancellationToken cancellationToken = default);
    bool TryGet(string paymentHash, string connectionIdentity, out CoinsnapInvoiceState state);
    IReadOnlyCollection<CoinsnapInvoiceState> Snapshot(string? connectionIdentity = null);
}

internal sealed class CoinsnapInvoiceStateRepository : ICoinsnapInvoiceStateRepository
{
    private readonly ICoinsnapStateStore _store;
    private readonly ICoinsnapClock _clock;
    private readonly ConcurrentDictionary<string, CoinsnapInvoiceState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile bool _loaded;

    public CoinsnapInvoiceStateRepository(ICoinsnapStateStore store, ICoinsnapClock? clock = null)
    {
        _store = store;
        _clock = clock ?? new SystemCoinsnapClock();
    }

    public event Action<CoinsnapInvoiceState>? Settled;

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
            return;
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
                return;
            var persisted = await _store.LoadAsync(cancellationToken);
            if (persisted?.Version is 1 && persisted.Invoices is not null)
            {
                // Entries past retention are dropped here rather than waiting for the poller.
                var now = _clock.UtcNow;
                foreach (var state in persisted.Invoices.Where(IsStructurallyValid))
                {
                    if (!CoinsnapRetention.IsPastRetention(state, now))
                        _states[state.PaymentHash] = state.Clone();
                }
            }
            _loaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task AddAsync(CoinsnapInvoiceState state, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!IsStructurallyValid(state))
            throw new ArgumentException("Coinsnap invoice state is incomplete.", nameof(state));

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            EnsureCapacity(state);
            if (_states.TryGetValue(state.PaymentHash, out var existing))
            {
                if (!existing.ConnectionIdentity.Equals(state.ConnectionIdentity, StringComparison.Ordinal))
                    throw new InvalidOperationException("A Coinsnap payment hash is already owned by another store connection.");
                if (existing.Settled)
                    throw new InvalidOperationException("A settled Coinsnap payment hash cannot be tracked again.");
            }

            _states[state.PaymentHash] = state.Clone();
            await SaveSnapshotAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task MarkSettledAsync(
        string paymentHash,
        string preimage,
        DateTimeOffset paidAt,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!CoinsnapLnurlService.IsValidPreimage(preimage, paymentHash))
            throw new ArgumentException("The Coinsnap settlement preimage does not match the payment hash.", nameof(preimage));

        CoinsnapInvoiceState? settled = null;
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (!_states.TryGetValue(paymentHash, out var current) || current.Settled)
                return;
            settled = current.Clone();
            settled.Settled = true;
            settled.Preimage = preimage;
            settled.PaidAt = paidAt;
            _states[paymentHash] = settled;
            await SaveSnapshotAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
        Settled?.Invoke(settled!.Clone());
    }

    public async Task RemoveAsync(
        string paymentHash,
        string connectionIdentity,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (!_states.TryGetValue(paymentHash, out var current) ||
                !current.ConnectionIdentity.Equals(connectionIdentity, StringComparison.Ordinal))
                return;
            if (_states.TryRemove(paymentHash, out _))
                await SaveSnapshotAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void EnsureCapacity(CoinsnapInvoiceState incoming)
    {
        if (_states.ContainsKey(incoming.PaymentHash))
            return;
        var now = _clock.UtcNow;
        var payableForStore = _states.Values.Count(s =>
            s.ConnectionIdentity.Equals(incoming.ConnectionIdentity, StringComparison.Ordinal) &&
            CoinsnapRetention.IsPayable(s, now));
        if (payableForStore >= CoinsnapRetention.MaxPayablePerStore)
            throw new InvalidOperationException(
                $"This store already has {payableForStore} unpaid Coinsnap Lightning invoices; try again once some expire.");

        var storeEntries = _states.Values
            .Where(s => s.ConnectionIdentity.Equals(incoming.ConnectionIdentity, StringComparison.Ordinal))
            .ToList();
        var storeCount = storeEntries.Count;
        if (storeCount >= CoinsnapRetention.MaxTrackedPerStore)
        {
            storeCount -= Evict(storeEntries, storeCount - CoinsnapRetention.MaxTrackedPerStore + 1, now);
            if (storeCount >= CoinsnapRetention.MaxTrackedPerStore)
                throw new InvalidOperationException(
                    "This store is tracking too many Coinsnap Lightning invoices; try again once some expire.");
        }

        if (_states.Count >= CoinsnapRetention.MaxTracked)
        {
            Evict(_states.Values.ToList(), _states.Count - CoinsnapRetention.MaxTracked + 1, now);
            // Still full: refuse only stores holding at least their fair share, so flooded stores block only themselves.
            if (_states.Count >= CoinsnapRetention.MaxTracked && storeCount >= FairShare(incoming.ConnectionIdentity))
                throw new InvalidOperationException("The server is tracking too many Coinsnap Lightning invoices; try again shortly.");
        }
    }

    private int FairShare(string connectionIdentity)
    {
        var stores = _states.Values.Select(s => s.ConnectionIdentity).Append(connectionIdentity)
            .Distinct(StringComparer.Ordinal).Count();
        return Math.Max(1, CoinsnapRetention.MaxTracked / stores);
    }

    private int Evict(IEnumerable<CoinsnapInvoiceState> candidates, int wanted, DateTimeOffset now)
    {
        var evicted = 0;
        foreach (var stale in candidates
                     .Where(s => CoinsnapRetention.IsEvictable(s, now))
                     .OrderBy(s => s.ExpiresAt)
                     .Take(wanted))
        {
            if (_states.TryRemove(stale.PaymentHash, out _))
                evicted++;
        }
        return evicted;
    }

    public bool TryGet(string paymentHash, string connectionIdentity, out CoinsnapInvoiceState state)
    {
        state = default!;
        if (!_loaded || !_states.TryGetValue(paymentHash, out var found) ||
            !found.ConnectionIdentity.Equals(connectionIdentity, StringComparison.Ordinal))
            return false;
        state = found.Clone();
        return true;
    }

    public IReadOnlyCollection<CoinsnapInvoiceState> Snapshot(string? connectionIdentity = null)
    {
        if (!_loaded)
            return [];
        return _states.Values
            .Where(s => connectionIdentity is null || s.ConnectionIdentity.Equals(connectionIdentity, StringComparison.Ordinal))
            .Select(s => s.Clone())
            .ToArray();
    }

    private Task SaveSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = new CoinsnapPersistedInvoiceStates
        {
            Invoices = _states.Values.Select(s => s.Clone()).ToList()
        };
        return _store.SaveAsync(snapshot, cancellationToken);
    }

    private static bool IsStructurallyValid(CoinsnapInvoiceState state) =>
        !string.IsNullOrWhiteSpace(state.PaymentHash) &&
        !string.IsNullOrWhiteSpace(state.Bolt11) &&
        !string.IsNullOrWhiteSpace(state.VerifyUrl) &&
        !string.IsNullOrWhiteSpace(state.LightningAddress) &&
        !string.IsNullOrWhiteSpace(state.ConnectionIdentity) &&
        state.ExpiresAt > state.CreatedAt;
}
