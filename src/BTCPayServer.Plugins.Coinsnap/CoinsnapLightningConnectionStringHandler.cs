using BTCPayServer.Lightning;
using Microsoft.Extensions.Logging;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap;

public sealed class CoinsnapLightningConnectionStringHandler : ILightningConnectionStringHandler
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "server", "ln-address", "store-id", "binding"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICoinsnapBolt11Parser _bolt11Parser;
    private readonly ICoinsnapInvoiceStateRepository _states;
    private readonly ICoinsnapClock _clock;
    private readonly ICoinsnapStoreConfigLookup _storeConfig;
    private readonly ICoinsnapStoreBinding _binding;
    private readonly ILoggerFactory _loggerFactory;
    private static readonly TimeSpan StateLoadTimeout = TimeSpan.FromSeconds(10);
    private readonly CoinsnapAvailability _availability = new();

    internal CoinsnapLightningConnectionStringHandler(
        IHttpClientFactory httpClientFactory,
        ICoinsnapBolt11Parser bolt11Parser,
        ICoinsnapInvoiceStateRepository states,
        ICoinsnapClock clock,
        ICoinsnapStoreConfigLookup storeConfig,
        ICoinsnapStoreBinding binding,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _bolt11Parser = bolt11Parser;
        _states = states;
        _clock = clock;
        _storeConfig = storeConfig;
        _binding = binding;
        _loggerFactory = loggerFactory;
    }

    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        Dictionary<string, string> values;
        string type;
        try
        {
            values = LightningConnectionStringHelper.ExtractValues(connectionString, out type);
        }
        catch (FormatException)
        {
            throw;
        }

        if (!type.Equals(CoinsnapConstants.ConnectionType, StringComparison.OrdinalIgnoreCase))
        {
            error = null;
            return null;
        }
        if (!IsNetworkAllowed(network, CoinsnapConstants.IsHostOverridden))
        {
            error = "Coinsnap Wallet receiving is available only on Bitcoin mainnet.";
            return null;
        }

        var unknown = values.Keys.FirstOrDefault(k => !AllowedKeys.Contains(k));
        if (unknown is not null)
        {
            error = $"The key '{unknown}' is not supported by Coinsnap Wallet connections.";
            return null;
        }
        if (values.TryGetValue("server", out var server) && !IsCoinsnapServer(server))
        {
            error = $"The server must be https://{CoinsnapConstants.Host}.";
            return null;
        }
        if (!values.TryGetValue("ln-address", out var rawAddress))
        {
            error = "The key 'ln-address' is required.";
            return null;
        }
        if (!CoinsnapLightningAddress.TryParse(rawAddress, out var address, out error))
        {
            return null;
        }

        // Tracking must be bound to the store. An address-derived identity moves when the merchant
        // edits their address, and settlement of any invoice still in flight is then dropped.
        values.TryGetValue("store-id", out var storeId);
        if (string.IsNullOrWhiteSpace(storeId))
        {
            error = "A store-id is required so invoices stay tracked if the Lightning Address changes.";
            return null;
        }
        if (storeId.Length > 128 || storeId.Any(c => char.IsWhiteSpace(c) || c is ';' or '='))
        {
            error = "The store-id is malformed.";
            return null;
        }
        // The address and store-id may both be public; only the binding proves the store-id is this store's.
        // Checked here, not on use, because BTCPay builds a client from the stored string every time.
        values.TryGetValue("binding", out var binding);
        if (!_binding.Verify(storeId, binding))
        {
            error = "This Coinsnap connection was not issued for this store. Save it from the store's Coinsnap Wallet settings.";
            return null;
        }

        var logger = _loggerFactory.CreateLogger<CoinsnapLightningConnectionStringHandler>();
        // The poller loads state at startup, so this is normally already done. BTCPay's interface is
        // synchronous, so a cold load blocks this thread; bound it rather than wait indefinitely.
        try
        {
            if (!_states.EnsureLoadedAsync().Wait(StateLoadTimeout))
            {
                error = "Coinsnap invoice tracking is still loading; try again in a moment.";
                return null;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load persisted Coinsnap invoices");
            error = "Coinsnap invoice tracking state could not be loaded.";
            return null;
        }

        var identity = ConnectionIdentity(address!, storeId);
        var httpClient = _httpClientFactory.CreateClient(CoinsnapConstants.HttpClientName);
        var service = new CoinsnapLnurlService(
            new CoinsnapHttpClient(httpClient),
            _bolt11Parser,
            _states,
            _clock,
            _loggerFactory.CreateLogger<CoinsnapLnurlService>(),
            _availability);

        error = null;
        return new CoinsnapLnAddressLightningClient(address!, identity, storeId, network, service, _states, _storeConfig);
    }

    // BTCPay 2.4.5+ lets only server admins save a connection without a public `server`.
    internal static bool IsCoinsnapServer(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 &&
        uri.PathAndQuery == "/" &&
        uri.Fragment.Length == 0 &&
        uri.Host.Equals(CoinsnapConstants.Host, StringComparison.OrdinalIgnoreCase);

    internal static bool IsNetworkAllowed(Network network, bool hostOverridden) =>
        network == Network.Main || hostOverridden;

    internal static string ConnectionIdentity(CoinsnapLightningAddress address, string? storeId) =>
        string.IsNullOrWhiteSpace(storeId) ? $"address:{address.Value}" : $"store:{storeId}";
}
