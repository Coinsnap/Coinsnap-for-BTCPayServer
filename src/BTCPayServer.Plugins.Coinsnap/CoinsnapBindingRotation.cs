using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Coinsnap;

/// <summary>
/// Revokes a store's binding whenever who may manage the store changes, so a manager who is removed
/// or demoted cannot reuse a binding they once saw. The store's own connection is re-saved with the
/// new binding, so the merchant has nothing to do.
/// </summary>
public sealed class CoinsnapBindingRotation : EventHostedServiceBase
{
    private readonly ICoinsnapStoreBinding _binding;
    private readonly IServiceProvider _provider;
    private readonly ILogger<CoinsnapBindingRotation> _logger;

    internal CoinsnapBindingRotation(
        EventAggregator eventAggregator,
        ICoinsnapStoreBinding binding,
        IServiceProvider provider,
        ILogger<CoinsnapBindingRotation> logger) : base(eventAggregator, logger)
    {
        _binding = binding;
        _provider = provider;
        _logger = logger;
    }

    protected override void SubscribeToEvents()
    {
        Subscribe<StoreUserEvent.Removed>();
        Subscribe<StoreUserEvent.Updated>();
        Subscribe<StoreRoleEvent.Updated>();
        Subscribe<StoreRoleEvent.Removed>();
        // A deleted or disabled user's memberships are gone by now, so every bound store is rotated.
        Subscribe<UserEvent.Deleted>();
        Subscribe<UserEvent.DisabledChanged>();
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        // A server-wide role has no store; its change can affect any store holding a binding.
        var stores = evt switch
        {
            StoreUserEvent e => new[] { e.StoreId },
            StoreRoleEvent e when !string.IsNullOrEmpty(e.StoreId) => new[] { e.StoreId },
            StoreRoleEvent or UserEvent => _binding.BoundStores.ToArray(),
            _ => Array.Empty<string>()
        };
        foreach (var storeId in stores)
            await RotateWithRetryAsync(storeId, cancellationToken);
    }

    private async Task RotateWithRetryAsync(string storeId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await RotateAsync(storeId);
                return;
            }
            catch (Exception ex) when (attempt < 3 && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Rotating the Coinsnap binding of store {StoreId} failed, retrying", storeId);
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Could not rotate the Coinsnap binding of store {StoreId}; rotate it with POST /api/v1/stores/{StoreId}/coinsnap/rotate-binding",
                    storeId, storeId);
                return;
            }
        }
    }

    /// <summary>Issues the store a new binding and re-saves its Coinsnap connection with it.</summary>
    public async Task<string?> RotateAsync(string storeId)
    {
        await _binding.RotateAsync(storeId);

        // Resolved lazily, as in BtcpayCoinsnapStoreConfigLookup: the handlers depend on this plugin.
        var stores = _provider.GetRequiredService<StoreRepository>();
        var handlers = _provider.GetRequiredService<PaymentMethodHandlerDictionary>();
        var store = await stores.FindStore(storeId);
        if (store is null)
            return null;
        var paymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var config = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(paymentMethodId, handlers);
        if (config?.ConnectionString is not { } current)
            return null;

        Dictionary<string, string> values;
        try
        {
            values = LightningConnectionStringHelper.ExtractValues(current, out var type);
            if (!type.Equals(CoinsnapConstants.ConnectionType, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        catch (FormatException)
        {
            return null;
        }
        // Only a connection this store was issued is renewed; anything else stays revoked.
        if (!values.TryGetValue("store-id", out var claimed) || claimed != storeId ||
            !values.TryGetValue("ln-address", out var address))
            return null;

        config.ConnectionString = GreenfieldCoinsnapController.Build(address, storeId, _binding.Sign(storeId));
        store.SetPaymentMethodConfig(handlers[paymentMethodId], config);
        await stores.UpdateStore(store);
        _logger.LogInformation("Rotated the Coinsnap binding of store {StoreId}", storeId);
        return config.ConnectionString;
    }
}
