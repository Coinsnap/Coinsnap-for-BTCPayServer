namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>
/// Saving a connection through BTCPay's own validation. BTCPay builds a client to validate a
/// connection before it is saved, so a check that needs the saved configuration must not run
/// there; unit tests fake the store lookup and cannot catch that.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "ReleaseGate")]
public sealed class SetupTests
{
    private readonly GateFixture _gate;

    public SetupTests(GateFixture gate) => _gate = gate;

    [Fact]
    public async Task A_fresh_store_saves_its_first_address()
    {
        var store = await _gate.StoreOwner.CreateStoreAsync("gate-setup-fresh");

        await _gate.StoreOwner.SetCoinsnapLightningAsync(store, GateConfig.LightningAddress);

        Assert.Equal(
            await _gate.StoreOwner.GetCoinsnapConnectionAsync(store, GateConfig.LightningAddress),
            await _gate.StoreOwner.GetLightningConnectionAsync(store));
    }

    [Fact]
    public async Task A_saved_address_can_be_changed()
    {
        var store = await _gate.StoreOwner.CreateStoreAsync("gate-setup-change");
        await _gate.StoreOwner.SetCoinsnapLightningAsync(store, GateConfig.LightningAddress);

        await _gate.StoreOwner.SetCoinsnapLightningAsync(store, GateConfig.SecondLightningAddress);

        Assert.Contains(
            $"ln-address={GateConfig.SecondLightningAddress};",
            await _gate.StoreOwner.GetLightningConnectionAsync(store));
    }

    /// <summary>
    /// A store's Lightning Address and id can both be public. Another store owner who copies them
    /// must not be able to save a connection that claims the victim's store.
    /// </summary>
    [Fact]
    public async Task Another_store_owner_cannot_claim_a_store_by_copying_its_address_and_id()
    {
        var victim = await _gate.StoreOwner.CreateStoreAsync("gate-setup-victim");
        await _gate.StoreOwner.SetCoinsnapLightningAsync(victim, GateConfig.SecondLightningAddress);
        var claimant = await _gate.Attacker.CreateStoreAsync("gate-setup-claimant");

        var copied = $"type=coinsnap;server=https://{GateConfig.DevHost};" +
                     $"ln-address={GateConfig.SecondLightningAddress};store-id={victim};";
        var error = await Assert.ThrowsAsync<GreenfieldValidationException>(
            () => _gate.Attacker.SetLightningConnectionAsync(claimant, copied));

        Assert.Contains("not issued for this store", error.Message);
    }

    [Fact]
    public async Task A_binding_issued_to_one_store_does_not_unlock_another()
    {
        var victim = await _gate.StoreOwner.CreateStoreAsync("gate-setup-victim-2");
        await _gate.StoreOwner.SetCoinsnapLightningAsync(victim, GateConfig.SecondLightningAddress);
        var claimant = await _gate.Attacker.CreateStoreAsync("gate-setup-claimant-2");

        var own = await _gate.Attacker.GetCoinsnapConnectionAsync(claimant, GateConfig.SecondLightningAddress);
        var replayed = own.Replace($"store-id={claimant};", $"store-id={victim};");
        var error = await Assert.ThrowsAsync<GreenfieldValidationException>(
            () => _gate.Attacker.SetLightningConnectionAsync(claimant, replayed));

        Assert.Contains("not issued for this store", error.Message);
    }

    [Fact]
    public async Task Only_a_stores_own_managers_can_open_its_setup_page()
    {
        var victim = await _gate.StoreOwner.CreateStoreAsync("gate-setup-victim-3");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _gate.Attacker.GetCoinsnapConnectionAsync(victim, GateConfig.LightningAddress));

        Assert.Contains($"setup page of store {victim} returned", error.Message);
    }

    [Fact]
    public async Task A_store_owner_cannot_save_a_connection_without_the_server_key()
    {
        var store = await _gate.StoreOwner.CreateStoreAsync("gate-setup-legacy");
        var issued = await _gate.StoreOwner.GetCoinsnapConnectionAsync(store, GateConfig.LightningAddress);

        // Pins the BTCPay 2.4.5 rule the settings page works around; if it relaxes, so can the page.
        var withoutServer = issued.Replace($"server=https://{GateConfig.DevHost};", "");
        var error = await Assert.ThrowsAsync<GreenfieldValidationException>(
            () => _gate.StoreOwner.SetLightningConnectionAsync(store, withoutServer));

        Assert.Contains("canmodifyserversettings", error.Message);
    }

    /// <summary>
    /// A manager sees the store's binding legitimately. Once removed, that copy must stop working, and
    /// the store itself must keep working without the merchant doing anything.
    /// </summary>
    [Fact]
    public async Task A_removed_manager_cannot_reuse_the_stores_binding()
    {
        // Only a server admin may add a store user without an invitation, so the admin owns this store.
        var victim = await _gate.Greenfield.CreateStoreAsync("gate-setup-removed-manager");
        await _gate.Greenfield.SetCoinsnapLightningAsync(victim, GateConfig.LightningAddress);
        await _gate.Greenfield.AddStoreUserAsync(victim, _gate.AttackerEmail, "Owner");
        var copied = await _gate.Attacker.GetLightningConnectionAsync(victim);

        await _gate.Greenfield.RemoveStoreUserAsync(victim, _gate.AttackerEmail);

        // The rotation runs on BTCPay's event queue, so give it a moment.
        var current = copied;
        for (var i = 0; i < 30 && current == copied; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            current = await _gate.Greenfield.GetLightningConnectionAsync(victim);
        }
        Assert.NotEqual(copied, current);

        var claimant = await _gate.Attacker.CreateStoreAsync("gate-setup-removed-claimant");
        var error = await Assert.ThrowsAsync<GreenfieldValidationException>(
            () => _gate.Attacker.SetLightningConnectionAsync(claimant, copied));
        Assert.Contains("not issued for this store", error.Message);

        // The store's re-saved connection still passes BTCPay's validation.
        await _gate.Greenfield.SetLightningConnectionAsync(victim, current);
    }
}
