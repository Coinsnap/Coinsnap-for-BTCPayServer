using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>
/// Steady load over a long window: a stream of invoices across several stores, some paid at random
/// ages, the rest left to expire. Catches what minute-long tests cannot: polling that falls behind,
/// memory or connections that grow, and pruning that misbehaves with many invoices in flight.
/// Opt-in: set COINSNAP_GATE_SOAK_MINUTES.
/// </summary>
[Collection(GateCollection.Name)]
[Trait("Category", "Soak")]
public sealed class SoakTests
{
    private const int Stores = 4;
    private static readonly TimeSpan SettleBound = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SettleMax = TimeSpan.FromMinutes(3);

    private readonly GateFixture _gate;
    private readonly ITestOutputHelper _output;

    public SoakTests(GateFixture gate, ITestOutputHelper output)
    {
        _gate = gate;
        _output = output;
    }

    private sealed class Tracked
    {
        public required string Store;
        public required string Invoice;
        public required string Bolt11;
        public DateTimeOffset CreatedAt;
        public DateTimeOffset? PaidAt;
        public DateTimeOffset? SettledAt;
        public string? PayError;
    }

    [Fact]
    public async Task Steady_load_settles_every_payment_once_without_drift()
    {
        var minutesSetting = Environment.GetEnvironmentVariable("COINSNAP_GATE_SOAK_MINUTES");
        Assert.SkipUnless(int.TryParse(minutesSetting, out var minutes) && minutes > 0,
            "Set COINSNAP_GATE_SOAK_MINUTES to run the soak test.");
        var perMinute = int.TryParse(Environment.GetEnvironmentVariable("COINSNAP_GATE_SOAK_RATE"), out var r) ? r : 6;

        var stores = new List<string>();
        for (var i = 0; i < Stores; i++)
        {
            var store = await _gate.Greenfield.CreateStoreAsync($"gate-soak-{i}");
            await _gate.Greenfield.SetCoinsnapLightningAsync(store,
                i % 2 == 0 ? GateConfig.LightningAddress : GateConfig.SecondLightningAddress);
            stores.Add(store);
        }

        var tracked = new ConcurrentBag<Tracked>();
        var payments = new List<Task>();
        var samples = new List<(DateTimeOffset At, long RssKb, int Sockets)>();
        var random = new Random(4242);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(minutes);
        var tick = TimeSpan.FromSeconds(60.0 / perMinute);
        var nextSample = DateTimeOffset.UtcNow;
        var created = 0;

        using var watcher = new CancellationTokenSource();
        var watching = WatchSettlementsAsync(tracked, watcher.Token);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var store = stores[created++ % Stores];
            try
            {
                var invoice = await _gate.Greenfield.CreateLightningInvoiceAsync(store, random.Next(100, 400));
                tracked.Add(new Tracked
                {
                    Store = store,
                    Invoice = invoice,
                    Bolt11 = await _gate.Greenfield.GetLightningDestinationAsync(store, invoice),
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
            catch (Exception ex)
            {
                _output.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss} invoice creation failed: {ex.Message}");
            }

            // Pay a random unpaid invoice young enough to still be payable, so ages at payment vary.
            if (random.NextDouble() < 0.5)
            {
                var candidates = tracked
                    .Where(t => t.PaidAt is null && DateTimeOffset.UtcNow - t.CreatedAt < TimeSpan.FromMinutes(12))
                    .ToList();
                if (candidates.Count > 0)
                    payments.Add(PayAsync(candidates[random.Next(candidates.Count)]));
            }

            if (DateTimeOffset.UtcNow >= nextSample)
            {
                samples.Add((DateTimeOffset.UtcNow, BtcpayRssKb(), DevHostSockets()));
                nextSample += TimeSpan.FromMinutes(1);
            }
            await Task.Delay(tick);
        }

        await Task.WhenAll(payments);
        var drainUntil = DateTimeOffset.UtcNow + SettleMax;
        while (tracked.Any(t => t.PaidAt is not null && t.SettledAt is null) && DateTimeOffset.UtcNow < drainUntil)
            await Task.Delay(TimeSpan.FromSeconds(5));
        watcher.Cancel();
        await watching;
        samples.Add((DateTimeOffset.UtcNow, BtcpayRssKb(), DevHostSockets()));

        var paid = tracked.Where(t => t.PaidAt is not null).OrderBy(t => t.PaidAt).ToList();
        var unpaid = tracked.Where(t => t.PaidAt is null && t.PayError is null).ToList();
        var latencies = paid.Where(t => t.SettledAt is not null).Select(t => t.SettledAt!.Value - t.PaidAt!.Value).ToList();
        var thirds = paid.Count / 3;
        var early = Latencies(paid.Take(thirds));
        var late = Latencies(paid.Skip(paid.Count - thirds));

        var doubled = new List<string>();
        foreach (var t in paid.Where(t => t.SettledAt is not null))
            if (await _gate.Greenfield.CountPaymentsAsync(t.Store, t.Invoice) != 1)
                doubled.Add(t.Invoice);
        var phantom = new List<string>();
        foreach (var t in unpaid)
            if (await _gate.Greenfield.CountPaymentsAsync(t.Store, t.Invoice) != 0)
                phantom.Add(t.Invoice);

        var summary = new StringBuilder()
            .AppendLine($"Soak: {minutes} min, {perMinute}/min, {tracked.Count} invoices, {paid.Count} paid, " +
                        $"{tracked.Count(t => t.PayError is not null)} payment errors")
            .AppendLine($"Settle latency p50 {P(latencies, 0.5):F1}s p95 {P(latencies, 0.95):F1}s max {P(latencies, 1):F1}s; " +
                        $"first third p95 {P(early, 0.95):F1}s, last third p95 {P(late, 0.95):F1}s")
            .AppendLine($"Unsettled: {paid.Count(t => t.SettledAt is null)}, double-recorded: {doubled.Count}, " +
                        $"payments on unpaid invoices: {phantom.Count}")
            .AppendLine("Minute | BTCPay RSS MB | sockets to dev host")
            .AppendJoin("\n", samples.Select(s => $"{s.At:HH:mm} | {s.RssKb / 1024} | {s.Sockets}"))
            .ToString();
        _output.WriteLine(summary);
        await File.WriteAllTextAsync(Path.Combine(GateCertificateAuthority.StateDir, "soak-summary.txt"), summary);

        Assert.Empty(paid.Where(t => t.SettledAt is null).Select(t => t.Invoice));
        Assert.Empty(doubled);
        Assert.Empty(phantom);
        Assert.True(P(latencies, 0.95) <= SettleBound.TotalSeconds, $"p95 settle latency too high.\n{summary}");
        Assert.True(P(late, 0.95) <= 2 * P(early, 0.95) + 10, $"Settlement slowed down over the run.\n{summary}");
        // Ignore the first two minutes: startup and JIT inflate the baseline unpredictably.
        var baseline = samples.Skip(2).FirstOrDefault().RssKb;
        Assert.True(baseline == 0 || samples[^1].RssKb <= baseline * 1.5, $"BTCPay memory grew.\n{summary}");
        Assert.True(samples.Max(s => s.Sockets) <= 50, $"Too many sockets to the dev host.\n{summary}");
    }

    private async Task PayAsync(Tracked invoice)
    {
        invoice.PaidAt = DateTimeOffset.UtcNow;
        try
        {
            await _gate.Payer.PayAsync(invoice.Bolt11);
        }
        catch (Exception ex)
        {
            // An invoice that expired between picking and paying is not a settlement failure.
            invoice.PaidAt = null;
            invoice.PayError = ex.Message;
            _output.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss} payment of {invoice.Invoice} failed: {ex.Message}");
        }
    }

    private async Task WatchSettlementsAsync(ConcurrentBag<Tracked> tracked, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            foreach (var t in tracked.Where(t => t.PaidAt is not null && t.SettledAt is null))
            {
                try
                {
                    if (await _gate.Greenfield.GetStatusAsync(t.Store, t.Invoice) == "Settled")
                        t.SettledAt = DateTimeOffset.UtcNow;
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss} status check failed: {ex.Message}");
                }
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static List<TimeSpan> Latencies(IEnumerable<Tracked> invoices) =>
        invoices.Where(t => t.SettledAt is not null).Select(t => t.SettledAt!.Value - t.PaidAt!.Value).ToList();

    private static double P(List<TimeSpan> values, double quantile)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.Select(v => v.TotalSeconds).Order().ToList();
        return sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(quantile * sorted.Count) - 1)];
    }

    private static long BtcpayRssKb()
    {
        foreach (var dir in Directory.EnumerateDirectories("/proc").Where(d => int.TryParse(Path.GetFileName(d), out _)))
        {
            try
            {
                // dotnet run starts the apphost, so match the executable rather than BTCPayServer.dll.
                var executable = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0')[0];
                if (!executable.EndsWith("/bin/Debug/net10.0/BTCPayServer", StringComparison.Ordinal))
                    continue;
                var line = File.ReadLines(Path.Combine(dir, "status")).First(l => l.StartsWith("VmRSS:"));
                return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return 0;
    }

    private static int DevHostSockets()
    {
        // BTCPay reaches the dev host only through the fault proxy, so count its sockets to the proxy.
        var info = new ProcessStartInfo("ss", $"-tn state established dport = :{GateConfig.ProxyPort}") { RedirectStandardOutput = true };
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Split('\n').Count(l => l.Contains(FaultProxy.Endpoint));
    }
}
