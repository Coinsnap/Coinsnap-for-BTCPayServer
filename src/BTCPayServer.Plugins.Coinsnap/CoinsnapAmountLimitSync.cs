using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Coinsnap;

/// <summary>Applies <see cref="CoinsnapAmountLimits"/> whenever a store is saved, and to every store at startup.</summary>
internal sealed class CoinsnapAmountLimitSync : EventHostedServiceBase
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);
    private readonly IServiceProvider _provider;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CoinsnapAmountLimitSync> _logger;

    private sealed record SyncStore(string StoreId);

    internal CoinsnapAmountLimitSync(
        EventAggregator eventAggregator,
        IServiceProvider provider,
        IMemoryCache cache,
        ILogger<CoinsnapAmountLimitSync> logger) : base(eventAggregator, logger)
    {
        _provider = provider;
        _cache = cache;
        _logger = logger;
    }

    protected override void SubscribeToEvents() => Subscribe<StoreEvent.Updated>();

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);
        try
        {
            foreach (var store in await _provider.GetRequiredService<StoreRepository>().GetStores())
                PushEvent(new SyncStore(store.Id));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not list stores to align their Lightning amount limits with Coinsnap");
        }
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        var storeId = evt switch
        {
            StoreEvent.Updated e => e.StoreId,
            SyncStore e => e.StoreId,
            _ => null
        };
        if (storeId is null)
            return;
        try
        {
            await ApplyAsync(storeId, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not align the Lightning amount limits of store {StoreId} with Coinsnap", storeId);
        }
    }

    private async Task ApplyAsync(string storeId, CancellationToken cancellationToken)
    {
        var stores = _provider.GetRequiredService<StoreRepository>();
        var store = await stores.FindStore(storeId);
        if (store is null)
            return;

        var handlers = _provider.GetRequiredService<PaymentMethodHandlerDictionary>();
        var config = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(PaymentTypes.LN.GetPaymentMethodId("BTC"), handlers);
        decimal? maximum = null;
        if (CoinsnapLnurlRequestFilter.TryGetAddress(config?.GetExternalLightningUrl(), out var address))
        {
            var metadata = await GetMetadataAsync(address!, cancellationToken);
            if (metadata.MaxSendable is not { } maxSendable)
                return;
            maximum = CoinsnapAmountLimits.ToBitcoin(maxSendable);
        }

        var blob = store.GetStoreBlob();
        if (!CoinsnapAmountLimits.Apply(blob, maximum) || !store.SetStoreBlob(blob))
            return;
        await stores.UpdateStore(store);
        _logger.LogInformation("Aligned the Lightning amount limits of store {StoreId} with Coinsnap ({Maximum} BTC)",
            storeId, maximum?.ToString() ?? "none");
    }

    private async Task<CoinsnapLnurlPayResponse> GetMetadataAsync(CoinsnapLightningAddress address, CancellationToken cancellationToken)
    {
        var key = $"coinsnap-amount-limits:{address.Value}";
        if (_cache.TryGetValue(key, out CoinsnapLnurlPayResponse? cached) && cached is not null)
            return cached;

        var service = new CoinsnapLnurlService(
            new CoinsnapHttpClient(_provider.GetRequiredService<IHttpClientFactory>().CreateClient(CoinsnapConstants.HttpClientName)),
            _provider.GetRequiredService<ICoinsnapBolt11Parser>(),
            _provider.GetRequiredService<ICoinsnapInvoiceStateRepository>(),
            _provider.GetRequiredService<ICoinsnapClock>(),
            _provider.GetRequiredService<ILogger<CoinsnapLnurlService>>());
        var metadata = await service.GetMetadataAsync(address, cancellationToken);
        _cache.Set(key, metadata, CacheDuration);
        return metadata;
    }
}
