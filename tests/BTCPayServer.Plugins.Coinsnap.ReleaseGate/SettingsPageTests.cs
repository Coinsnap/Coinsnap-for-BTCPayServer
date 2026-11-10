using Microsoft.Playwright;

namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>
/// The settings page's script builds the connection string, so only a browser proves what a
/// merchant actually saves. Everything here runs as a store owner who is not a server admin.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "ReleaseGate")]
public sealed class SettingsPageTests : IAsyncLifetime
{
    private readonly GateFixture _gate;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private IPage _page = null!;

    public SettingsPageTests(GateFixture gate) => _gate = gate;

    public async ValueTask InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
        _page = await _browser.NewPageAsync(new() { BaseURL = _gate.Host.BaseAddress.ToString() });
        _page.SetDefaultTimeout(30_000);

        await _page.GotoAsync("login");
        await _page.FillAsync("#Email", _gate.StoreOwnerEmail);
        await _page.FillAsync("#Password", _gate.StoreOwnerPassword);
        await _page.ClickAsync("#LoginButton");
        await _page.WaitForURLAsync(url => !url.Contains("/login"));
    }

    public async ValueTask DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    [Fact]
    public async Task A_store_owner_saves_an_address_from_the_settings_page()
    {
        var store = await _gate.StoreOwner.CreateStoreAsync("gate-ui-fresh");

        await SaveThroughPageAsync(store, GateConfig.LightningAddress);

        Assert.Equal(
            await _gate.StoreOwner.GetCoinsnapConnectionAsync(store, GateConfig.LightningAddress),
            await _gate.StoreOwner.GetLightningConnectionAsync(store));
    }

    [Fact]
    public async Task The_page_shows_the_saved_address_and_saves_a_change()
    {
        var store = await _gate.StoreOwner.CreateStoreAsync("gate-ui-change");
        await SaveThroughPageAsync(store, GateConfig.LightningAddress);

        await OpenSetupAsync(store);
        Assert.True(await _page.IsCheckedAsync("#LightningNodeType-Coinsnap"));
        Assert.Equal(GateConfig.LightningAddress, await _page.InputValueAsync("#CoinsnapLightningAddress"));

        await SaveThroughPageAsync(store, GateConfig.SecondLightningAddress);

        Assert.Equal(
            await _gate.StoreOwner.GetCoinsnapConnectionAsync(store, GateConfig.SecondLightningAddress),
            await _gate.StoreOwner.GetLightningConnectionAsync(store));
    }

    [Fact]
    public async Task Test_connection_succeeds_for_a_valid_address()
    {
        var store = await _gate.StoreOwner.CreateStoreAsync("gate-ui-test-connection");

        await OpenSetupAsync(store);
        await _page.ClickAsync("label[for=LightningNodeType-Coinsnap]");
        await _page.FillAsync("#CoinsnapLightningAddress", GateConfig.LightningAddress);
        await _page.ClickAsync("#CoinsnapTest");

        var alert = _page.Locator(".alert-success, .alert-danger, .text-danger:not(:empty)").First;
        await alert.WaitForAsync();
        Assert.Contains("alert-success", await alert.GetAttributeAsync("class") ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_foreign_domain_is_refused_on_the_page()
    {
        var store = await _gate.StoreOwner.CreateStoreAsync("gate-ui-foreign");

        await OpenSetupAsync(store);
        await _page.ClickAsync("label[for=LightningNodeType-Coinsnap]");
        await _page.FillAsync("#CoinsnapLightningAddress", "bob@evil.example.com");
        await _page.ClickAsync("#page-primary");

        var error = _page.Locator(".text-danger:visible:not(:empty), .alert-danger:visible").First;
        await error.WaitForAsync();
        Assert.Contains(GateConfig.DevHost, await error.InnerTextAsync());
        Assert.Contains("/lightning/BTC/setup", _page.Url);
        await Assert.ThrowsAnyAsync<Exception>(() => _gate.StoreOwner.GetLightningConnectionAsync(store));
    }

    /// <summary>The page builds a ';'-delimited string, so the address must not be able to add keys to it.</summary>
    [Fact]
    public async Task An_address_carrying_connection_string_delimiters_is_blocked_on_the_page()
    {
        var store = await _gate.StoreOwner.CreateStoreAsync("gate-ui-injection");

        await OpenSetupAsync(store);
        await _page.ClickAsync("label[for=LightningNodeType-Coinsnap]");
        await _page.FillAsync("#CoinsnapLightningAddress", $"bob;store-id=someone-else@{GateConfig.DevHost}");
        await _page.ClickAsync("#page-primary");

        var message = await _page.EvalOnSelectorAsync<string>("#CoinsnapLightningAddress", "e => e.validationMessage");
        Assert.Contains("cannot contain", message);
        Assert.Contains("/lightning/BTC/setup", _page.Url);
        await Assert.ThrowsAnyAsync<Exception>(() => _gate.StoreOwner.GetLightningConnectionAsync(store));
    }

    private Task OpenSetupAsync(string store) => _page.GotoAsync($"stores/{store}/lightning/BTC/setup");

    private async Task SaveThroughPageAsync(string store, string address)
    {
        await OpenSetupAsync(store);
        await _page.ClickAsync("label[for=LightningNodeType-Coinsnap]");
        await _page.FillAsync("#CoinsnapLightningAddress", address);
        await _page.ClickAsync("#page-primary");

        // BTCPay moves on to the Lightning settings page only after a successful save.
        try
        {
            await _page.WaitForURLAsync(url => url.Contains("/lightning/BTC/settings"), new() { Timeout = 20_000 });
        }
        catch (TimeoutException)
        {
            var errors = await _page.Locator(".text-danger, .alert-danger").AllInnerTextsAsync();
            throw new InvalidOperationException($"Save was refused: {string.Join(" | ", errors.Where(e => e.Length > 0))}");
        }
    }
}
