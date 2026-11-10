using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using BTCPayServer.Lightning;
using NBitcoin;

namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

public sealed record ProxyHit(DateTimeOffset At, string Target, string Path, string Outcome)
{
    public bool Faulted => Outcome is "replace" or "delay" or "rewrite" or "blackhole" or "proxy-error";
}

/// <summary>
/// fault_proxy.py in a container, published on loopback. The gate's BTCPay opens every dev host
/// connection here (the plugin's Debug-only COINSNAP_DEV_CONNECT_TO) for the whole run, so there is
/// no network redirect to switch and no path around it. Faults are toggled through a mounted file;
/// with none set, the proxy forwards to the real dev host.
/// </summary>
public sealed class FaultProxy : IAsyncDisposable
{
    private const string Image = "python:3.12.7-alpine3.20";
    private const string Container = "coinsnap-gate-fault-proxy";

    private static readonly string WorkDir = Path.Combine(GateCertificateAuthority.StateDir, "fault");
    private static readonly string FaultFile = Path.Combine(WorkDir, "fault.json");
    private static readonly string HitsFile = Path.Combine(WorkDir, "hits.log");

    public static string Endpoint => $"127.0.0.1:{GateConfig.ProxyPort}";

    public static async Task<FaultProxy> StartAsync()
    {
        GateCertificateAuthority.Ensure();
        Directory.CreateDirectory(WorkDir);
        File.SetUnixFileMode(WorkDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        WriteFault("{}");
        File.WriteAllText(HitsFile, "");

        await Docker.RunAsync("rm", "-f", Container);
        var script = Path.Combine(GateConfig.RepoRoot, "tests", "BTCPayServer.Plugins.Coinsnap.ReleaseGate", "fault_proxy.py");
        var (code, _, error) = await Docker.RunAsync(
            // Runs as this user so it can read the owner-only key and write the hits log.
            "run", "-d", "--rm", "--name", Container, "--user", await CurrentUserAsync(),
            "-p", $"{Endpoint}:8443",
            "-v", $"{GateCertificateAuthority.CertDir}:/certs:ro",
            "-v", $"{WorkDir}:/gate",
            "-v", $"{script}:/proxy.py:ro",
            Image,
            "python", "-u", "/proxy.py", GateConfig.DevHost);
        if (code != 0)
            throw new InvalidOperationException($"Could not start the fault proxy: {error}");

        var proxy = new FaultProxy();
        try
        {
            await WaitForPortAsync();
        }
        catch
        {
            await proxy.DisposeAsync();
            throw;
        }
        return proxy;
    }

    /// <summary>
    /// Clears any fault. The request log runs for the whole gate and callers filter it by time and
    /// invoice: truncating it would orphan the finish line of a request already in flight.
    /// </summary>
    public void BeginDrill() => Clear();

    /// <summary>
    /// target is metadata, callback, verify or * (everything). Pass status/body/headers to replace the
    /// response, delay (seconds) to stall it, blackhole to hold every request until cleared, or
    /// rewrite to merge fields into the real JSON response.
    /// </summary>
    public void Inject(object fault) => WriteFault(JsonSerializer.Serialize(fault));

    public void Clear() => WriteFault("{}");

    public IReadOnlyList<ProxyHit> Hits(string target, string? outcome = null) =>
        File.ReadAllLines(HitsFile)
            .Where(line => line.Length > 0)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Select(e => new ProxyHit(
                DateTimeOffset.FromUnixTimeMilliseconds((long)(e.GetProperty("t").GetDouble() * 1000)),
                e.GetProperty("target").GetString()!,
                e.GetProperty("path").GetString()!,
                e.GetProperty("outcome").GetString()!))
            .Where(h => h.Target == target && (outcome is null || h.Outcome == outcome))
            .ToList();

    /// <summary>This invoice's verify requests; every pending invoice is polled through the proxy.</summary>
    public IEnumerable<ProxyHit> VerifiesOf(string bolt11)
    {
        var hash = BOLT11PaymentRequest.Parse(bolt11, Network.RegTest).PaymentHash!.ToString();
        return Hits("verify").Where(h => h.Path.Contains(hash, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A verify that passed before the fault can still be open at the server and would truthfully
    /// report a payment, so a test pays only once none is in flight and polling meets the fault.
    /// </summary>
    public async Task WaitUntilFaultedAsync(string bolt11, DateTimeOffset since)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(45);
        while (true)
        {
            var hits = VerifiesOf(bolt11).ToList();
            var inFlight = hits.Count(h => h.Outcome == "pass") - hits.Count(h => h.Outcome == "pass-done");
            if (inFlight == 0 && hits.Any(h => h.At >= since && h.Faulted))
                return;
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Fault never took hold for this invoice: {Describe(hits)}" +
                                           $"\nProxy output:\n{await LogsAsync()}");
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }

    public static string Describe(IReadOnlyList<ProxyHit> hits) =>
        hits.Count == 0 ? "no requests" : string.Join(", ", hits.Select(h => $"{h.At:HH:mm:ss.f} {h.Outcome}"));

    public async Task<string> LogsAsync()
    {
        var (_, stdout, stderr) = await Docker.RunAsync("logs", "--tail", "60", Container);
        return stdout + stderr;
    }

    public async ValueTask DisposeAsync()
    {
        await Docker.RunAsync("rm", "-f", Container);
        Archive();
    }

    // Replace atomically, so the proxy never reads a half-written fault.
    private static void WriteFault(string json)
    {
        var temp = FaultFile + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, FaultFile, overwrite: true);
    }

    private static void Archive()
    {
        if (File.Exists(HitsFile) && new FileInfo(HitsFile).Length > 0)
            File.Copy(HitsFile, Path.Combine(WorkDir, $"hits-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss.fff}.log"), overwrite: true);
    }

    private static async Task<string> CurrentUserAsync()
    {
        static async Task<string> Id(string flag)
        {
            using var process = Process.Start(new ProcessStartInfo("id", flag) { RedirectStandardOutput = true })!;
            var value = (await process.StandardOutput.ReadToEndAsync()).Trim();
            await process.WaitForExitAsync();
            return value;
        }
        return $"{await Id("-u")}:{await Id("-g")}";
    }

    private static async Task WaitForPortAsync()
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", GateConfig.ProxyPort);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(500);
            }
        }
        var (_, logs, _) = await Docker.RunAsync("logs", Container);
        throw new TimeoutException($"The fault proxy never listened on {Endpoint}.\n{logs}");
    }
}

public static class Docker
{
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var info = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Failed to start docker.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, (await stdout).Trim(), await stderr);
    }
}
