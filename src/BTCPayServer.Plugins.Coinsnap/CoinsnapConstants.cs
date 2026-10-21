using System.Net;

namespace BTCPayServer.Plugins.Coinsnap;

internal static class CoinsnapConstants
{
    public const string ConnectionType = "coinsnap";
    public const string DefaultHost = "coinsnap.app";
    public const string HostOverrideVariable = "COINSNAP_DEV_HOST";
    public const string ConnectOverrideVariable = "COINSNAP_DEV_CONNECT_TO";
    public const string HttpClientName = "BTCPayServer.Plugins.Coinsnap";
    public const string SettingsName = "Coinsnap.TrackedInvoices.v1";
    public const int MaxResponseBytes = 256 * 1024;
    public const int MaxRedirects = 2;

    public static string Host { get; } = ResolveHost(Environment.GetEnvironmentVariable(HostOverrideVariable));

    public static bool IsHostOverridden => !Host.Equals(DefaultHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where connections to an overridden host are opened instead, e.g. a local test proxy.</summary>
    public static IPEndPoint? ConnectOverride { get; } =
        IsHostOverridden ? ResolveConnectOverride(Environment.GetEnvironmentVariable(ConnectOverrideVariable)) : null;

    /// <summary>Debug builds may point the plugin at a test LNURL host. Release builds always use
    /// coinsnap.app, so a packaged plugin can never be redirected.</summary>
    internal static string ResolveHost(string? overrideValue)
    {
#if DEBUG
        var candidate = overrideValue?.Trim();
        if (string.IsNullOrEmpty(candidate) || Uri.CheckHostName(candidate) != UriHostNameType.Dns)
            return DefaultHost;
        return candidate;
#else
        return DefaultHost;
#endif
    }

    /// <summary>Debug builds may open the dev host's connections to a loopback endpoint; TLS still
    /// validates the dev host's name. Release builds never redirect a connection.</summary>
    internal static IPEndPoint? ResolveConnectOverride(string? value)
    {
#if DEBUG
        if (!IPEndPoint.TryParse(value?.Trim() ?? "", out var endpoint) ||
            !IPAddress.IsLoopback(endpoint.Address) || endpoint.Port == 0)
            return null;
        return endpoint;
#else
        return null;
#endif
    }
}
