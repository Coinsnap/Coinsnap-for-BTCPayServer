using Microsoft.Playwright;

namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>
/// Opt-in: set COINSNAP_GATE_SCREENSHOTS to a directory. Walks a store owner through setup as a
/// merchant would and saves a screenshot per step, for reviewing the flow by eye.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "ReleaseGate")]
public sealed class SetupTourTests(GateFixture gate)
{
    [Fact]
    public async Task Merchant_setup_tour()
    {
        var dir = Environment.GetEnvironmentVariable("COINSNAP_GATE_SCREENSHOTS");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(dir), "Set COINSNAP_GATE_SCREENSHOTS to capture the setup tour.");
        Directory.CreateDirectory(dir!);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new()
        {
            BaseURL = gate.Host.BaseAddress.ToString(),
            ViewportSize = new() { Width = 1280, Height = 900 }
        });
        page.SetDefaultTimeout(30_000);
        var step = 0;
        Task Shot(string name) => page.ScreenshotAsync(new() { Path = Path.Combine(dir!, $"{++step:00}-{name}.png"), FullPage = true });

        await page.GotoAsync("login");
        await page.FillAsync("#Email", gate.StoreOwnerEmail);
        await page.FillAsync("#Password", gate.StoreOwnerPassword);
        await page.ClickAsync("#LoginButton");
        await page.WaitForURLAsync(url => !url.Contains("/login"));

        var store = await gate.StoreOwner.CreateStoreAsync("My Shop");
        await page.GotoAsync($"stores/{store}");
        await Shot("store-dashboard");

        await page.ClickAsync("[data-testid=Lightning-BTC]");
        await page.WaitForURLAsync(url => url.Contains("/lightning/BTC/setup"));
        await Shot("lightning-setup-default-tab");

        await page.ClickAsync("label[for=LightningNodeType-Coinsnap]");
        await page.Locator("#CoinsnapSetup.show").WaitForAsync();
        await page.WaitForTimeoutAsync(500); // let the fade-in finish
        await Shot("coinsnap-tab");

        await page.FillAsync("#CoinsnapLightningAddress", "shop@example.com");
        await page.ClickAsync("#page-primary");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Shot("error-foreign-domain");

        await page.ClickAsync("label[for=LightningNodeType-Coinsnap]");
        await page.FillAsync("#CoinsnapLightningAddress", GateConfig.LightningAddress);
        await page.ClickAsync("#CoinsnapTest");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Shot("test-connection");

        await page.ClickAsync("label[for=LightningNodeType-Coinsnap]");
        await page.FillAsync("#CoinsnapLightningAddress", GateConfig.LightningAddress);
        await page.ClickAsync("#page-primary");
        await page.WaitForURLAsync(url => !url.Contains("/setup"));
        await Shot("after-save-lightning-settings");

        await page.GotoAsync($"stores/{store}/lightning/BTC/setup");
        await Shot("reopen-setup");

        await page.GotoAsync($"stores/{store}");
        await Shot("store-dashboard-after-setup");

        await page.GotoAsync($"embed/{store}/BTC/ln");
        await Shot("public-node-info");

        var invoice = await gate.StoreOwner.CreateLightningInvoiceAsync(store, 1_000);
        await page.GotoAsync($"i/{invoice}");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Shot("checkout");
    }
}
