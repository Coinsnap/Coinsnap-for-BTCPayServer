using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Coinsnap;

internal interface ICoinsnapStoreConfigLookup
{
    Task<string?> GetLightningConnectionStringAsync(string storeId, CancellationToken cancellationToken = default);
}

internal interface ICoinsnapStoreConfigStore : ICoinsnapStoreConfigLookup
{
    Task<IReadOnlyCollection<string>> GetStoreIdsAsync(CancellationToken cancellationToken = default);
    Task RemoveLightningConnectionAsync(string storeId, CancellationToken cancellationToken = default);
}

internal static class CoinsnapStoreClaim
{
    /// <summary>A claimed store-id is trusted only when that store's own Lightning configuration is
    /// this same Coinsnap connection. Otherwise the caller is asserting an identity it does not hold.</summary>
    internal static bool IsVerified(
        string? configuredConnectionString,
        CoinsnapLightningAddress address,
        string storeId)
    {
        if (string.IsNullOrWhiteSpace(configuredConnectionString))
            return false;

        Dictionary<string, string> values;
        string type;
        try
        {
            values = LightningConnectionStringHelper.ExtractValues(configuredConnectionString, out type);
        }
        catch (FormatException)
        {
            return false;
        }

        if (!type.Equals(CoinsnapConstants.ConnectionType, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!values.TryGetValue("ln-address", out var configuredAddress) ||
            !CoinsnapLightningAddress.TryParse(configuredAddress, out var parsed, out _) ||
            !parsed!.Value.Equals(address.Value, StringComparison.Ordinal))
            return false;

        return values.TryGetValue("store-id", out var configuredStoreId) &&
               configuredStoreId.Equals(storeId, StringComparison.Ordinal);
    }

    /// <summary>A binding is copyable, so a Coinsnap connection is valid only in the store it names.</summary>
    internal static bool NamesAnotherStore(string? connectionString, string savingStoreId)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;
        try
        {
            var values = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
            return type.Equals(CoinsnapConstants.ConnectionType, StringComparison.OrdinalIgnoreCase) &&
                   values.TryGetValue("store-id", out var storeId) &&
                   !storeId.Equals(savingStoreId, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal sealed class BtcpayCoinsnapStoreConfigLookup : ICoinsnapStoreConfigStore
{
    private readonly IServiceProvider _provider;

    // Resolved lazily: the payment-method handlers depend on the lightning client factory, which
    // depends on this plugin's connection-string handler.
    public BtcpayCoinsnapStoreConfigLookup(IServiceProvider provider)
    {
        _provider = provider;
    }

    public async Task<string?> GetLightningConnectionStringAsync(
        string storeId,
        CancellationToken cancellationToken = default)
    {
        var stores = _provider.GetRequiredService<StoreRepository>();
        var handlers = _provider.GetRequiredService<PaymentMethodHandlerDictionary>();
        var store = await stores.FindStore(storeId);
        if (store is null)
            return null;
        var paymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        return store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(paymentMethodId, handlers)?.ConnectionString;
    }

    public async Task<IReadOnlyCollection<string>> GetStoreIdsAsync(CancellationToken cancellationToken = default)
    {
        var stores = await _provider.GetRequiredService<StoreRepository>().GetStores();
        return stores.Select(s => s.Id).ToArray();
    }

    public async Task RemoveLightningConnectionAsync(string storeId, CancellationToken cancellationToken = default)
    {
        var stores = _provider.GetRequiredService<StoreRepository>();
        var store = await stores.FindStore(storeId);
        if (store is null)
            return;
        store.SetPaymentMethodConfig(PaymentTypes.LN.GetPaymentMethodId("BTC"), null);
        await stores.UpdateStore(store);
    }
}
