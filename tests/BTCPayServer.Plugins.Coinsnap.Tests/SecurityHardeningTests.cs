using BTCPayServer.Events;
using BTCPayServer.Logging;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

/// <summary>Coinsnap Plugin Security Guide 6.13: logs never carry a full store ID.</summary>
public class LogMaskTests
{
    [Theory]
    [InlineData("8Qm3kXv2Yb7RtLpW9sZc", "…9sZc")]
    [InlineData("  8Qm3kXv2Yb7RtLpW9sZc ", "…9sZc")]
    [InlineData("abcd", "…")]
    [InlineData("ab", "…")]
    [InlineData("", "(none)")]
    [InlineData(null, "(none)")]
    public void StoreIdsKeepOnlyTheirLastFourCharacters(string? storeId, string expected)
    {
        Assert.Equal(expected, CoinsnapLogMask.StoreId(storeId));
    }

    [Fact]
    public async Task RemovingACopiedConnectionLogsNoFullStoreId()
    {
        const string attacker = "AttackerStoreId0001x";
        const string victim = "VictimStoreId000002y";
        var stores = new FakeStoreConfigLookup();
        stores.ConnectionStrings[attacker] =
            $"type=coinsnap;server=https://coinsnap.app/;ln-address=victim@coinsnap.app;store-id={victim};binding=b;";
        var logger = new CapturingLogger<CoinsnapForeignStoreGuard>();

        await new CoinsnapForeignStoreGuard(new EventAggregator(new Logs()), stores, logger).EnforceAsync(attacker);

        Assert.False(stores.ConnectionStrings.ContainsKey(attacker));
        var line = Assert.Single(logger.Lines);
        Assert.DoesNotContain(attacker, line);
        Assert.DoesNotContain(victim, line);
        Assert.Contains("…001x", line);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}

[CollectionDefinition(nameof(GlobalJsonSettingsCollection), DisableParallelization = true)]
public class GlobalJsonSettingsCollection;

/// <summary>Guide 6.1: a Coinsnap answer never chooses a .NET type, whatever another plugin set globally.</summary>
[Collection(nameof(GlobalJsonSettingsCollection))]
public class SafeJsonTests
{
    private const string Body =
        """{"$type":"System.IO.FileInfo, System.IO.FileSystem","status":"OK","settled":true,"pr":"lnbc1"}""";

    [Fact]
    public void TypeNamesInCoinsnapAnswersAreIgnoredEvenWhenGlobalSettingsAllowThem()
    {
        var previous = JsonConvert.DefaultSettings;
        JsonConvert.DefaultSettings = () => new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All };
        try
        {
            var parsed = CoinsnapLnurlService.Deserialize<CoinsnapVerifyResponse>(Body, "test");

            Assert.IsType<CoinsnapVerifyResponse>(parsed);
            Assert.Equal("OK", parsed.Status);
            Assert.True(parsed.Settled);
        }
        finally
        {
            JsonConvert.DefaultSettings = previous;
        }
    }

    [Fact]
    public void DeeplyNestedAnswersAreRefused()
    {
        // Deeper than 32, but within Newtonsoft's own default of 64, in a property the DTO does not have.
        var body = "{\"extra\":" + new string('[', 40) + new string(']', 40) + ",\"status\":\"OK\"}";

        Assert.Throws<FormatException>(() => CoinsnapLnurlService.Deserialize<CoinsnapVerifyResponse>(body, "test"));
    }

    [Fact]
    public void SettingsNeverResolveTypes()
    {
        Assert.Equal(TypeNameHandling.None, CoinsnapLnurlService.SafeJsonSettings.TypeNameHandling);
        Assert.Equal(MetadataPropertyHandling.Ignore, CoinsnapLnurlService.SafeJsonSettings.MetadataPropertyHandling);
    }
}
