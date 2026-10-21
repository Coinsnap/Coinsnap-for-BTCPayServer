using BTCPayServer.Abstractions.Services;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Payments.LNURLPay;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using LNURLPayRequest = LNURL.LNURLPayRequest;

namespace BTCPayServer.Plugins.Coinsnap;

internal sealed class CoinsnapLnurlRequestFilter : PluginHookFilter<LNURLPayRequest>
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CoinsnapLnurlRequestFilter> _logger;
    private readonly ICoinsnapBolt11Parser _bolt11Parser;
    private readonly ICoinsnapInvoiceStateRepository _states;
    private readonly ICoinsnapClock _clock;

    public CoinsnapLnurlRequestFilter(
        PaymentMethodHandlerDictionary handlers,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILoggerFactory loggerFactory,
        ICoinsnapBolt11Parser bolt11Parser,
        ICoinsnapInvoiceStateRepository states,
        ICoinsnapClock clock)
    {
        _handlers = handlers;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CoinsnapLnurlRequestFilter>();
        _bolt11Parser = bolt11Parser;
        _states = states;
        _clock = clock;
    }

    public override string Hook => "modify-lnurlp-request";

    public override async Task<LNURLPayRequest> Execute(LNURLPayRequest arg)
    {
        try
        {
            if (arg is not StoreLNURLPayRequest { Store: { } store })
                return arg;

            var lightningId = PaymentTypes.LN.GetPaymentMethodId("BTC");
            var configs = store.GetPaymentMethodConfigs<LightningPaymentMethodConfig>(_handlers, onlyEnabled: true);
            if (!configs.TryGetValue(lightningId, out var config) ||
                !TryGetAddress(config.GetExternalLightningUrl(), out var address))
            {
                return arg;
            }

            var metadata = await GetMetadataAsync(address!);
            ApplyCoinsnapParameters(arg, metadata);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not align BTCPay LNURL metadata with Coinsnap");
        }
        return arg;
    }

    internal static bool TryGetAddress(string? connectionString, out CoinsnapLightningAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;
        try
        {
            var values = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
            return type.Equals(CoinsnapConstants.ConnectionType, StringComparison.OrdinalIgnoreCase) &&
                   values.TryGetValue("ln-address", out var raw) &&
                   CoinsnapLightningAddress.TryParse(raw, out address, out _);
        }
        catch
        {
            return false;
        }
    }

    internal static void ApplyCoinsnapParameters(LNURLPayRequest request, CoinsnapLnurlPayResponse metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.Metadata))
            request.Metadata = metadata.Metadata;

        var coinsnapMin = metadata.MinSendable is { } min ? new LightMoney(min) : null;
        var coinsnapMax = metadata.MaxSendable is { } max ? new LightMoney(max) : null;
        var intersectedMin = Max(request.MinSendable, coinsnapMin);
        var intersectedMax = Min(request.MaxSendable, coinsnapMax);
        if (intersectedMin is not null && intersectedMax is not null && intersectedMin <= intersectedMax)
        {
            request.MinSendable = intersectedMin;
            request.MaxSendable = intersectedMax;
        }

        if (metadata.CommentAllowed is { } allowed && allowed >= 0 && request.CommentAllowed > allowed)
            request.CommentAllowed = allowed;
    }

    private async Task<CoinsnapLnurlPayResponse> GetMetadataAsync(CoinsnapLightningAddress address)
    {
        var key = $"coinsnap-lnurl-metadata:{address.Value}";
        if (_cache.TryGetValue(key, out CoinsnapLnurlPayResponse? cached) && cached is not null)
            return cached;

        var service = new CoinsnapLnurlService(
            new CoinsnapHttpClient(_httpClientFactory.CreateClient(CoinsnapConstants.HttpClientName)),
            _bolt11Parser,
            _states,
            _clock,
            _loggerFactory.CreateLogger<CoinsnapLnurlService>());
        var result = await service.GetMetadataAsync(address, CancellationToken.None);
        _cache.Set(key, result, CacheDuration);
        return result;
    }

    private static LightMoney? Max(LightMoney? left, LightMoney? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left > right ? left : right;
    }

    private static LightMoney? Min(LightMoney? left, LightMoney? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left < right ? left : right;
    }
}
