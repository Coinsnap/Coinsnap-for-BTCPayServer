using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using BTCPayServer.Abstractions.Contracts;

namespace BTCPayServer.Plugins.Coinsnap;

/// <summary>
/// Proves that a connection's store-id was issued to that store. A store's Lightning Address and id
/// can both be public, so a claim built from them alone could be copied into another store's
/// connection; the binding is an HMAC of the store-id and a per-store nonce under a key only this
/// server holds. Rotating a store's nonce revokes every binding issued to it before.
/// </summary>
public interface ICoinsnapStoreBinding
{
    string Sign(string storeId);
    bool Verify(string storeId, string? binding);
    Task RotateAsync(string storeId);

    /// <summary>Stores that have been issued a binding, and so may hold a connection that needs it.</summary>
    IReadOnlyCollection<string> BoundStores { get; }
}

internal sealed class CoinsnapStoreBinding : ICoinsnapStoreBinding
{
    private const string KeySettingsName = "Coinsnap.StoreBindingKey.v1";
    private const string NonceSettingsName = "Coinsnap.StoreBindingNonces.v1";
    private const string Purpose = "coinsnap-store-binding-v2:";

    private readonly ISettingsRepository _settings;
    private readonly object _lock = new();
    // Saves run one at a time and always write the current nonces, so an older snapshot can never land
    // after a newer one and bring a revoked nonce back on restart.
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private byte[]? _key;
    private Dictionary<string, string>? _nonces;

    public CoinsnapStoreBinding(ISettingsRepository settings)
    {
        _settings = settings;
    }

    public IReadOnlyCollection<string> BoundStores
    {
        get
        {
            lock (_lock)
                return Nonces().Keys.ToArray();
        }
    }

    public string Sign(string storeId)
    {
        string nonce;
        var created = false;
        lock (_lock)
        {
            var nonces = Nonces();
            if (!nonces.TryGetValue(storeId, out nonce!))
            {
                nonce = NewNonce();
                nonces[storeId] = nonce;
                created = true;
            }
        }
        if (created)
            SaveCurrentAsync().GetAwaiter().GetResult();
        return Compute(storeId, nonce);
    }

    public bool Verify(string storeId, string? binding)
    {
        if (string.IsNullOrEmpty(binding))
            return false;
        string? nonce;
        lock (_lock)
            Nonces().TryGetValue(storeId, out nonce);
        return nonce is not null &&
               CryptographicOperations.FixedTimeEquals(
                   Encoding.ASCII.GetBytes(Compute(storeId, nonce)), Encoding.ASCII.GetBytes(binding));
    }

    public async Task RotateAsync(string storeId)
    {
        lock (_lock)
            Nonces()[storeId] = NewNonce();
        await SaveCurrentAsync();
    }

    private string Compute(string storeId, string nonce) =>
        Base64Url.EncodeToString(HMACSHA256.HashData(Key(), Encoding.UTF8.GetBytes($"{Purpose}{storeId}:{nonce}")));

    private static string NewNonce() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    private async Task SaveCurrentAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            Dictionary<string, string> current;
            lock (_lock)
                current = new Dictionary<string, string>(Nonces(), StringComparer.Ordinal);
            await _settings.UpdateSetting(new CoinsnapStoreBindingNonces { Nonces = current }, NonceSettingsName);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    // Synchronous because BTCPay validates connection strings synchronously; each loads once per process.
    private Dictionary<string, string> Nonces()
    {
        if (_nonces is not null)
            return _nonces;
        var stored = _settings.GetSettingAsync<CoinsnapStoreBindingNonces>(NonceSettingsName).GetAwaiter().GetResult();
        _nonces = new Dictionary<string, string>(stored?.Nonces ?? [], StringComparer.Ordinal);
        return _nonces;
    }

    private byte[] Key()
    {
        if (_key is { } cached)
            return cached;
        lock (_lock)
        {
            if (_key is not null)
                return _key;
            var stored = _settings.GetSettingAsync<CoinsnapStoreBindingKey>(KeySettingsName).GetAwaiter().GetResult();
            var encoded = stored?.Key;
            if (string.IsNullOrEmpty(encoded))
            {
                encoded = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                _settings.UpdateSetting(new CoinsnapStoreBindingKey { Key = encoded }, KeySettingsName).GetAwaiter().GetResult();
            }
            _key = Convert.FromBase64String(encoded);
            return _key;
        }
    }
}

internal sealed class CoinsnapStoreBindingKey
{
    public string? Key { get; set; }
}

internal sealed class CoinsnapStoreBindingNonces
{
    public Dictionary<string, string> Nonces { get; set; } = new(StringComparer.Ordinal);
}
