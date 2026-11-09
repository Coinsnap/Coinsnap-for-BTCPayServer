using System.Net;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Lightning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Coinsnap;

public sealed class CoinsnapPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        // Only tested BTCPay versions. The plugin implements internal BTCPay contracts,
        // so widen this deliberately after testing each new BTCPay release.
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.5 && <2.5.0" }
    ];

    public override void Execute(IServiceCollection services)
    {
        services.AddUIExtension("ln-payment-method-setup-tabhead", "Coinsnap/LNPaymentMethodSetupTabHead");
        services.AddUIExtension("ln-payment-method-setup-tab", "Coinsnap/LNPaymentMethodSetupTab");

        services.AddHttpClient(CoinsnapConstants.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("BTCPayServer.Plugins.Coinsnap/0.1");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseProxy = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(10),
                ConnectCallback = CoinsnapNetworkPolicy.ConnectPublicAsync
            });

        services.AddSingleton<ICoinsnapClock, SystemCoinsnapClock>();
        services.AddSingleton<ICoinsnapBolt11Parser, CoinsnapBolt11Parser>();
        services.AddSingleton<ICoinsnapStateStore, BtcpayCoinsnapStateStore>();
        services.AddSingleton<ICoinsnapInvoiceStateRepository, CoinsnapInvoiceStateRepository>();
        services.AddSingleton<BtcpayCoinsnapStoreConfigLookup>();
        services.AddSingleton<ICoinsnapStoreConfigLookup>(provider => provider.GetRequiredService<BtcpayCoinsnapStoreConfigLookup>());
        services.AddSingleton<IHostedService>(provider => new CoinsnapForeignStoreGuard(
            provider.GetRequiredService<BTCPayServer.EventAggregator>(),
            provider.GetRequiredService<BtcpayCoinsnapStoreConfigLookup>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CoinsnapForeignStoreGuard>>()));
        services.AddSingleton<ICoinsnapStoreBinding, CoinsnapStoreBinding>();
        services.AddSingleton<CoinsnapBindingRotation>(provider => new CoinsnapBindingRotation(
            provider.GetRequiredService<BTCPayServer.EventAggregator>(),
            provider.GetRequiredService<ICoinsnapStoreBinding>(),
            provider,
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CoinsnapBindingRotation>>()));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<CoinsnapBindingRotation>());
        services.AddSingleton<CoinsnapLightningConnectionStringHandler>(provider => new CoinsnapLightningConnectionStringHandler(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<ICoinsnapBolt11Parser>(),
            provider.GetRequiredService<ICoinsnapInvoiceStateRepository>(),
            provider.GetRequiredService<ICoinsnapClock>(),
            provider.GetRequiredService<ICoinsnapStoreConfigLookup>(),
            provider.GetRequiredService<ICoinsnapStoreBinding>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        services.AddSingleton<ILightningConnectionStringHandler>(provider =>
            provider.GetRequiredService<CoinsnapLightningConnectionStringHandler>());
        services.AddSingleton<IPluginHookFilter, CoinsnapLnurlRequestFilter>();
        services.AddSingleton<CoinsnapSettlementPoller>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<CoinsnapSettlementPoller>());

        base.Execute(services);
    }
}
