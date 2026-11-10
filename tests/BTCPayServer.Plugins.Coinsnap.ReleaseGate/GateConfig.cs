namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>Environment for the release gate. Secrets come from the environment, never the repo.</summary>
public static class GateConfig
{
    public static string DevHost => Require(
        "COINSNAP_GATE_DEV_HOST",
        "Set it to the regtest LNURL host the gate creates invoices on, e.g. ln.regtest.example.com.");

    public static string LightningAddress =>
        Get("COINSNAP_GATE_LN_ADDRESS", $"releasegate@{DevHost}");

    public static string SecondLightningAddress =>
        Get("COINSNAP_GATE_LN_ADDRESS_2", $"releasegate-b@{DevHost}");

    /// <summary>REST URL of the paying LND node. Must differ from the node the dev host mints on.</summary>
    public static string LndRestUrl => Require(
        "COINSNAP_GATE_LND_URL",
        "Set it to the paying LND node's REST URL, e.g. https://lnd.regtest.example.com:8080.");

    /// <summary>Hex macaroon for the paying node. Required; the gate cannot invent one.</summary>
    public static string LndMacaroonHex => Require(
        "COINSNAP_GATE_LND_MACAROON",
        "Set it to a hex admin macaroon for the paying LND node (COINSNAP_GATE_LND_URL).");

    /// <summary>Loopback port the fault proxy is published on; BTCPay connects to the dev host through it.</summary>
    public static int ProxyPort =>
        int.Parse(Get("COINSNAP_GATE_PROXY_PORT", "18443"));

    public static int BtcpayPort =>
        int.Parse(Get("COINSNAP_GATE_BTCPAY_PORT", "14150"));

    public static Uri BtcpayUri => new($"http://127.0.0.1:{BtcpayPort}/");

    /// <summary>BTCPay to launch. Defaults to the pinned submodule so the gate is self-contained.</summary>
    public static string BtcpayProjectDir => Get(
        "COINSNAP_GATE_BTCPAY_DIR",
        Path.Combine(RepoRoot, "submodules", "btcpayserver", "BTCPayServer"));

    public static string PluginDll => Get(
        "COINSNAP_GATE_PLUGIN_DLL",
        Path.Combine(RepoRoot, "src", "BTCPayServer.Plugins.Coinsnap", "bin", "Debug", "net10.0",
            "BTCPayServer.Plugins.Coinsnap.dll"));

    /// <summary>Separate database from a developer's own instance, so the gate never clobbers it.</summary>
    public static string PostgresConnectionString => Get(
        "COINSNAP_GATE_POSTGRES",
        "User ID=postgres;Include Error Detail=true;Host=127.0.0.1;Port=39372;Database=btcpayserver_releasegate");

    public static string ExplorerPostgresConnectionString => Get(
        "COINSNAP_GATE_EXPLORER_POSTGRES",
        "User ID=postgres;Include Error Detail=true;Host=127.0.0.1;Port=39372;Database=nbxplorer");

    public static string ExplorerUrl => Get("COINSNAP_GATE_EXPLORER_URL", "http://127.0.0.1:32838/");

    public static TimeSpan StartupTimeout =>
        TimeSpan.FromSeconds(int.Parse(Get("COINSNAP_GATE_STARTUP_TIMEOUT", "180")));

    public static string RepoRoot { get; } = FindRepoRoot();

    private static string Get(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

    private static string Require(string key, string how) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v
            ? v
            : throw new InvalidOperationException($"{key} is not set.\n{how}");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BTCPayServer.Plugins.Coinsnap.slnx")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException("Could not locate the plugin repository root.");
    }
}
