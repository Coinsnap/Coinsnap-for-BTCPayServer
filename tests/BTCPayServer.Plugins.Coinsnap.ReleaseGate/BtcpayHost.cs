using System.Diagnostics;

namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>
/// Owns a BTCPay process so the gate can restart it for real, which is what the restart
/// scenarios actually need.
/// </summary>
public sealed class BtcpayHost : IAsyncDisposable
{
    private readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(5) };
    private Process? _process;

    public Uri BaseAddress => GateConfig.BtcpayUri;

    public static string LogPath { get; } = Path.Combine(GateCertificateAuthority.StateDir, "btcpay.log");

    private static readonly object LogLock = new();

    /// <summary>Identifies the live process, so a restart can be proven rather than assumed.</summary>
    public int? ProcessId => _process is { HasExited: false } p ? p.Id : null;

    public async Task StartAsync()
    {
        if (_process is { HasExited: false })
            return;

        RegisterPlugin();
        GateCertificateAuthority.Ensure();
        _process = Process.Start(BuildStartInfo())
                   ?? throw new InvalidOperationException("Failed to start BTCPay.");
        // Unread pipes fill up and stall BTCPay mid-run; keep the output as release evidence instead.
        _process.OutputDataReceived += (_, e) => AppendLog(e.Data);
        _process.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        AppendLog($"===== gate: BTCPay started at {DateTimeOffset.UtcNow:O}, pid {_process.Id}");
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        await WaitUntilHealthyAsync();
    }

    public async Task StopAsync()
    {
        if (_process is null || _process.HasExited)
        {
            _process = null;
            return;
        }

        // Kill the whole tree: `dotnet run` spawns the server as a child.
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
        _process = null;
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    /// <summary>BTCPay reads DEBUG_PLUGINS from appsettings.dev.json in Debug builds.</summary>
    private static void RegisterPlugin()
    {
        if (!File.Exists(GateConfig.PluginDll))
            throw new InvalidOperationException(
                $"Plugin assembly not found at {GateConfig.PluginDll}. Build the plugin in Debug first.");

        var path = Path.Combine(GateConfig.BtcpayProjectDir, "appsettings.dev.json");
        File.WriteAllText(path,
            $$"""{ "DEBUG_PLUGINS": "{{GateConfig.PluginDll.Replace("\\", "\\\\")}}" }""");
    }

    private static ProcessStartInfo BuildStartInfo()
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = GateConfig.BtcpayProjectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        // No launch profile: the gate controls port and database so it never collides with a
        // developer's own instance on 14142.
        info.ArgumentList.Add("run");
        info.ArgumentList.Add("--no-launch-profile");

        foreach (var (key, value) in Environment())
            info.Environment[key] = value;

        return info;
    }

    private static Dictionary<string, string> Environment() => new()
    {
        ["COINSNAP_DEV_HOST"] = GateConfig.DevHost,
        // Debug-only: every dev host connection goes to the fault proxy, which forwards unless faulted.
        ["COINSNAP_DEV_CONNECT_TO"] = FaultProxy.Endpoint,
        // Lets the fault proxy terminate TLS for the dev host; nothing else on the machine trusts it.
        ["SSL_CERT_FILE"] = GateCertificateAuthority.TrustBundlePath,
        ["Logging__LogLevel__BTCPayServer.Plugins.Coinsnap"] = "Debug",
        ["ASPNETCORE_ENVIRONMENT"] = "Development",
        ["BTCPAY_NETWORK"] = "regtest",
        ["BTCPAY_CHAINS"] = "btc",
        ["BTCPAY_PORT"] = GateConfig.BtcpayPort.ToString(),
        ["BTCPAY_POSTGRES"] = GateConfig.PostgresConnectionString,
        ["BTCPAY_EXPLORERPOSTGRES"] = GateConfig.ExplorerPostgresConnectionString,
        ["BTCPAY_BTCEXPLORERURL"] = GateConfig.ExplorerUrl,
        ["BTCPAY_ALLOW-ADMIN-REGISTRATION"] = "true",
        ["BTCPAY_DISABLE-REGISTRATION"] = "false",
        ["BTCPAY_UPDATEURL"] = "",
        ["BTCPAY_RECOMMENDED-PLUGINS"] = ""
    };

    private async Task WaitUntilHealthyAsync()
    {
        var deadline = DateTimeOffset.UtcNow + GateConfig.StartupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_process!.HasExited)
                throw new InvalidOperationException(
                    $"BTCPay exited during startup with code {_process.ExitCode}.");

            try
            {
                using var response = await _probe.GetAsync(BaseAddress);
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            catch (TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        throw new TimeoutException(
            $"BTCPay did not become reachable at {BaseAddress} within {GateConfig.StartupTimeout}.");
    }

    private static void AppendLog(string? line)
    {
        if (line is null)
            return;
        lock (LogLock)
            // BTCPay's console logger has no timestamps; these line up with the fault proxy's log.
            File.AppendAllText(LogPath, $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} {line}\n");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _probe.Dispose();
    }
}
