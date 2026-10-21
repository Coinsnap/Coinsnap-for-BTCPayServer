using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace BTCPayServer.Plugins.Coinsnap;

/// <summary>
/// Keeps checkout responsive when Coinsnap is slow or down: Lightning Address metadata is reused
/// briefly instead of fetched for every invoice, and after repeated failures to reach Coinsnap,
/// invoice creation for that address fails at once for a short while instead of making each customer
/// wait out two 20-second timeouts. Payment polling does not use it.
/// Both are kept per Lightning Address, compared exactly: the name part may be case-sensitive at
/// Coinsnap, and one store's broken or unreachable address must never pause another's.
/// </summary>
internal sealed class CoinsnapAvailability
{
    public static readonly TimeSpan MetadataTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan OpenFor = TimeSpan.FromSeconds(30);
    public const int FailuresToOpen = 3;

    private readonly ConcurrentDictionary<string, (CoinsnapLnurlPayResponse Metadata, DateTimeOffset FetchedAt)> _metadata =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Circuit> _circuits = new(StringComparer.Ordinal);

    private sealed class Circuit
    {
        public int ConsecutiveFailures;
        public DateTimeOffset OpenUntil = DateTimeOffset.MinValue;
    }

    public bool TryGetMetadata(string address, DateTimeOffset now, [NotNullWhen(true)] out CoinsnapLnurlPayResponse? metadata)
    {
        if (_metadata.TryGetValue(address, out var cached) && now - cached.FetchedAt < MetadataTtl)
        {
            metadata = cached.Metadata;
            return true;
        }
        metadata = null;
        return false;
    }

    public void StoreMetadata(string address, CoinsnapLnurlPayResponse metadata, DateTimeOffset now) =>
        _metadata[address] = (metadata, now);

    public void ForgetMetadata(string address) => _metadata.TryRemove(address, out _);

    public void EnsureReachable(string address, DateTimeOffset now)
    {
        if (!_circuits.TryGetValue(address, out var circuit))
            return;
        lock (circuit)
        {
            if (now < circuit.OpenUntil)
                throw new HttpRequestException("Coinsnap was unreachable moments ago; Lightning invoices resume shortly.");
        }
    }

    public void RecordSuccess(string address)
    {
        if (!_circuits.TryGetValue(address, out var circuit))
            return;
        lock (circuit)
            circuit.ConsecutiveFailures = 0;
    }

    public void RecordFailure(string address, DateTimeOffset now)
    {
        var circuit = _circuits.GetOrAdd(address, _ => new Circuit());
        lock (circuit)
        {
            if (++circuit.ConsecutiveFailures < FailuresToOpen)
                return;
            circuit.ConsecutiveFailures = 0;
            circuit.OpenUntil = now + OpenFor;
        }
    }

    /// <summary>
    /// Only a failure to reach Coinsnap counts: no answer, a timeout or a 5xx. A 4xx is an answer
    /// about that address, not an outage.
    /// </summary>
    public static bool IsOutage(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } => (int)status >= 500,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        _ => false
    };
}
