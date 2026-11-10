namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>
/// Brings up one BTCPay for a test class and tears it down afterwards. Starting BTCPay dominates
/// the gate's runtime, so it is shared rather than started per test.
/// </summary>
public sealed class GateFixture : IAsyncLifetime
{
    public BtcpayHost Host { get; } = new();

    /// <summary>Sits between BTCPay and the dev host for the whole run; drills toggle faults in it.</summary>
    public FaultProxy Proxy { get; private set; } = null!;
    /// <summary>A server admin.</summary>
    public GreenfieldClient Greenfield { get; private set; } = null!;

    /// <summary>
    /// A store owner who is not a server admin: the merchant BTCPay 2.4.5 holds to stricter rules.
    /// </summary>
    public GreenfieldClient StoreOwner { get; private set; } = null!;
    public string StoreOwnerEmail { get; private set; } = null!;
    public string StoreOwnerPassword { get; private set; } = null!;
    /// <summary>An unrelated non-admin store owner, for attacks on the store owner's stores.</summary>
    public GreenfieldClient Attacker { get; private set; } = null!;
    public string AttackerEmail { get; private set; } = null!;
    public ClusterPayer Payer { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Payer = new ClusterPayer();
        await Payer.EnsureNotPayeeOfAsync(await LnurlDirectory.MintAsync(GateConfig.LightningAddress, 1));
        await GateDatabase.ResetAsync();
        Proxy = await FaultProxy.StartAsync();
        await Host.StartAsync();
        Greenfield = await GreenfieldClient.BootstrapAsync(Host.BaseAddress);
        if (!await Greenfield.IsServerAdminAsync())
            throw new InvalidOperationException("The gate's bootstrap user is not a server admin.");

        (StoreOwner, StoreOwnerEmail, StoreOwnerPassword) = await Greenfield.CreateUserAsync(isAdministrator: false);
        (Attacker, AttackerEmail, _) = await Greenfield.CreateUserAsync(isAdministrator: false);
        if (await StoreOwner.IsServerAdminAsync())
            throw new InvalidOperationException("The gate's store owner is a server admin, so setup tests would prove nothing.");
    }

    public async ValueTask DisposeAsync()
    {
        Attacker?.Dispose();
        StoreOwner?.Dispose();
        Greenfield?.Dispose();
        Payer?.Dispose();
        await Host.DisposeAsync();
        if (Proxy is not null)
            await Proxy.DisposeAsync();
    }
}
