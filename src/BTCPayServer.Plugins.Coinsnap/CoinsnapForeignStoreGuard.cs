using BTCPayServer.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Coinsnap;

/// <summary>
/// Removes a store's Coinsnap connection when it names another store, so a copied binding cannot give
/// one store a client for another store's invoices.
/// </summary>
internal sealed class CoinsnapForeignStoreGuard : IHostedService
{
    private readonly EventAggregator _events;
    private readonly ICoinsnapStoreConfigStore _stores;
    private readonly ILogger<CoinsnapForeignStoreGuard> _logger;
    private IEventAggregatorSubscription? _subscription;

    internal CoinsnapForeignStoreGuard(
        EventAggregator events,
        ICoinsnapStoreConfigStore stores,
        ILogger<CoinsnapForeignStoreGuard> logger)
    {
        _events = events;
        _stores = stores;
        _logger = logger;
    }

    internal async Task EnforceAsync(string storeId, CancellationToken cancellationToken = default)
    {
        var connection = await _stores.GetLightningConnectionStringAsync(storeId, cancellationToken);
        if (!CoinsnapStoreClaim.NamesAnotherStore(connection, storeId))
            return;

        await _stores.RemoveLightningConnectionAsync(storeId, cancellationToken);
        _logger.LogWarning(
            "Removed the Lightning connection of store {StoreId}: it was a Coinsnap connection issued for another store",
            storeId);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Inline, not queued: Publish runs this before the saving request returns.
        _subscription = _events.Subscribe<StoreEvent.Updated>(e => TryEnforceAsync(e.StoreId, CancellationToken.None).GetAwaiter().GetResult());

        IReadOnlyCollection<string> storeIds;
        try
        {
            storeIds = await _stores.GetStoreIdsAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Could not list stores to check for Coinsnap connections issued for another store");
            return;
        }

        foreach (var storeId in storeIds)
            await TryEnforceAsync(storeId, cancellationToken);
    }

    private async Task TryEnforceAsync(string storeId, CancellationToken cancellationToken)
    {
        try
        {
            await EnforceAsync(storeId, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex,
                "Could not check the Lightning connection of store {StoreId} for a Coinsnap connection issued for another store",
                storeId);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }
}
