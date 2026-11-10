using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

/// <summary>
/// A binding proves a connection was issued for the store it names, but anyone holding the string can
/// paste it into a store of their own. The guard keeps each Coinsnap connection inside that store.
/// </summary>
public class ForeignStoreGuardTests
{
    private const string VictimConnection =
        "type=coinsnap;server=https://coinsnap.app/;ln-address=victim@coinsnap.app;store-id=victim;binding=b-victim;";

    [Theory]
    [InlineData(VictimConnection, "attacker", true)]
    [InlineData(VictimConnection, "victim", false)]
    [InlineData(VictimConnection, "Victim", true)]
    [InlineData("type=coinsnap;ln-address=victim@coinsnap.app;", "attacker", false)]
    [InlineData("type=lnd-rest;server=https://node.example/;macaroon=00;", "attacker", false)]
    [InlineData("not a connection string", "attacker", false)]
    [InlineData("", "attacker", false)]
    [InlineData(null, "attacker", false)]
    public void NamesAnotherStoreOnlyFlagsACoinsnapConnectionForADifferentStore(
        string? connection, string savingStoreId, bool expected)
    {
        Assert.Equal(expected, CoinsnapStoreClaim.NamesAnotherStore(connection, savingStoreId));
    }

    [Fact]
    public async Task ACopiedConnectionIsRemovedFromTheStoreThatSavedIt()
    {
        var stores = Stores();
        stores.ConnectionStrings["attacker"] = VictimConnection;

        await Guard(stores).EnforceAsync("attacker");

        Assert.False(stores.ConnectionStrings.ContainsKey("attacker"));
        Assert.Equal(VictimConnection, stores.ConnectionStrings["victim"]);
    }

    [Fact]
    public async Task AStoresOwnConnectionIsKept()
    {
        var stores = Stores();

        await Guard(stores).EnforceAsync("victim");

        Assert.Equal(VictimConnection, stores.ConnectionStrings["victim"]);
        Assert.Empty(stores.Removed);
    }

    [Fact]
    public async Task StartupRemovesCopiesSavedBeforeTheGuardExisted()
    {
        var stores = Stores();
        stores.ConnectionStrings["attacker"] = VictimConnection;
        stores.ConnectionStrings["other"] = "type=lnd-rest;server=https://node.example/;macaroon=00;";
        var guard = Guard(stores, new EventAggregator(new Logs()));

        await guard.StartAsync(CancellationToken.None);

        Assert.Equal(new[] { "attacker" }, stores.Removed);
        Assert.Equal(VictimConnection, stores.ConnectionStrings["victim"]);
        await guard.StopAsync(CancellationToken.None);
    }

    /// <summary>One unreadable store must neither stop BTCPay from starting nor skip the other stores.</summary>
    [Fact]
    public async Task StartupKeepsCheckingAfterAStoreFails()
    {
        var stores = Stores();
        stores.ConnectionStrings["broken"] = "type=lnd-rest;server=https://node.example/;macaroon=00;";
        stores.ConnectionStrings["attacker"] = VictimConnection;
        stores.FailingStores.Add("broken");
        var guard = Guard(stores);

        await guard.StartAsync(CancellationToken.None);

        Assert.Equal(new[] { "attacker" }, stores.Removed);
        await guard.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartupSurvivesWhenStoresCannotBeListedAndStillGuardsSaves()
    {
        var stores = Stores();
        stores.ListFailure = new InvalidOperationException("database unavailable");
        var events = new EventAggregator(new Logs());
        var guard = Guard(stores, events);

        await guard.StartAsync(CancellationToken.None);

        stores.ConnectionStrings["attacker"] = VictimConnection;
        events.Publish(new StoreEvent.Updated(new StoreData { Id = "attacker" }));
        Assert.False(stores.ConnectionStrings.ContainsKey("attacker"));
        await guard.StopAsync(CancellationToken.None);
    }

    /// <summary>Removal must finish inside the save's own event, before the save request returns.</summary>
    [Fact]
    public async Task ASaveIsCleanedUpBeforeTheStoreUpdatedEventReturns()
    {
        var stores = Stores();
        var events = new EventAggregator(new Logs());
        var guard = Guard(stores, events);
        await guard.StartAsync(CancellationToken.None);

        stores.ConnectionStrings["attacker"] = VictimConnection;
        events.Publish(new StoreEvent.Updated(new StoreData { Id = "attacker" }));

        Assert.False(stores.ConnectionStrings.ContainsKey("attacker"));
        await guard.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AfterStopUpdatesAreNoLongerHandled()
    {
        var stores = Stores();
        var events = new EventAggregator(new Logs());
        var guard = Guard(stores, events);
        await guard.StartAsync(CancellationToken.None);
        await guard.StopAsync(CancellationToken.None);

        stores.ConnectionStrings["attacker"] = VictimConnection;
        events.Publish(new StoreEvent.Updated(new StoreData { Id = "attacker" }));

        Assert.True(stores.ConnectionStrings.ContainsKey("attacker"));
    }

    private static FakeStoreConfigLookup Stores()
    {
        var stores = new FakeStoreConfigLookup();
        stores.ConnectionStrings["victim"] = VictimConnection;
        return stores;
    }

    private static CoinsnapForeignStoreGuard Guard(FakeStoreConfigLookup stores, EventAggregator? events = null) =>
        new(events ?? new EventAggregator(new Logs()), stores, NullLogger<CoinsnapForeignStoreGuard>.Instance);
}
