using System.Collections.Concurrent;
using BTCPayServer.Lightning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap;

internal sealed class CoinsnapSettlementPoller : IHostedService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NotFoundDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettledGrace = CoinsnapRetention.SettledGrace;
    private static readonly TimeSpan ExpiredGrace = CoinsnapRetention.ExpiredGrace;
    private static readonly TimeSpan ExpiredRecheck = TimeSpan.FromMinutes(5);
    // Past this, an invoice the server still cannot answer for is dropped rather than polled forever.
    private static readonly TimeSpan GiveUpAfterExpiry = CoinsnapRetention.GiveUpAfterExpiry;
    private const int MaxConcurrency = 8;

    private readonly ICoinsnapInvoiceStateRepository _states;
    private readonly CoinsnapLnurlService _lnurl;
    private readonly ICoinsnapClock _clock;
    private readonly ILogger<CoinsnapSettlementPoller> _logger;
    private readonly ConcurrentDictionary<string, PollState> _pollStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    private sealed class PollState
    {
        public int Errors;
        public DateTimeOffset NextAttempt;
    }

    public CoinsnapSettlementPoller(
        ICoinsnapInvoiceStateRepository states,
        IHttpClientFactory httpClientFactory,
        ICoinsnapBolt11Parser bolt11Parser,
        ICoinsnapClock clock,
        ILoggerFactory loggerFactory)
    {
        _states = states;
        _clock = clock;
        _logger = loggerFactory.CreateLogger<CoinsnapSettlementPoller>();
        _lnurl = new CoinsnapLnurlService(
            new CoinsnapHttpClient(httpClientFactory.CreateClient(CoinsnapConstants.HttpClientName)),
            bolt11Parser,
            states,
            clock,
            loggerFactory.CreateLogger<CoinsnapLnurlService>());
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _states.EnsureLoadedAsync(cancellationToken);
        _loop = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stop.Cancel();
        if (_loop is null)
            return;
        try
        {
            await _loop.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Coinsnap settlement polling cycle failed");
            }

            try
            {
                await Task.Delay(ScanInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One pass over every tracked invoice that is due. The background loop runs it every second.</summary>
    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var snapshot = _states.Snapshot();
        var live = snapshot.Select(s => s.PaymentHash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var hash in _pollStates.Keys)
        {
            if (!live.Contains(hash))
                _pollStates.TryRemove(hash, out _);
        }

        var now = _clock.UtcNow;
        var due = snapshot.Where(state => IsDue(state, now));
        await Parallel.ForEachAsync(
            due,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrency,
                CancellationToken = cancellationToken
            },
            async (state, token) => await PollOneAsync(state, token));
    }

    private async Task PollOneAsync(CoinsnapInvoiceState invoice, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        if (invoice.Settled)
        {
            if (now > invoice.ExpiresAt + SettledGrace)
                await _states.RemoveAsync(invoice.PaymentHash, invoice.ConnectionIdentity, cancellationToken);
            return;
        }
        var pollState = _pollStates.GetOrAdd(invoice.PaymentHash, _ => new PollState());
        if (now < pollState.NextAttempt)
            return;

        CoinsnapVerificationResult result;
        try
        {
            result = await _lnurl.VerifyAsync(invoice, NetworkFor(invoice), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unexpected Coinsnap verification failure for {PaymentHash}", invoice.PaymentHash);
            await ScheduleFailureAsync(invoice, pollState, null, cancellationToken);
            return;
        }

        switch (result.Outcome)
        {
            case CoinsnapVerificationOutcome.Paid when result.Invoice?.Preimage is { } preimage:
                _pollStates.TryRemove(invoice.PaymentHash, out _);
                await _states.MarkSettledAsync(
                    invoice.PaymentHash,
                    preimage,
                    result.Invoice.PaidAt ?? now,
                    cancellationToken);
                break;

            // Expired and unsettled: keep re-checking through the grace period, because a settlement can
            // surface late, and drop the invoice only once a check after the grace still finds it unpaid.
            case CoinsnapVerificationOutcome.Expired:
                pollState.Errors = 0;
                if (now > invoice.ExpiresAt + ExpiredGrace)
                    await RemoveAsync(invoice, cancellationToken);
                else
                    pollState.NextAttempt = now + AddJitter(ExpiredRecheck);
                break;

            // "Not found" can be transient, so it never drops a payable invoice; it only ends tracking the
            // same way an expired invoice does.
            case CoinsnapVerificationOutcome.Unknown:
                pollState.Errors = 0;
                if (now > invoice.ExpiresAt + ExpiredGrace)
                    await RemoveAsync(invoice, cancellationToken);
                else
                    pollState.NextAttempt = now + AddJitter(NotFoundDelay);
                break;

            case CoinsnapVerificationOutcome.Retry:
                await ScheduleFailureAsync(invoice, pollState, result.RetryAfter, cancellationToken);
                break;

            case CoinsnapVerificationOutcome.Pending:
                pollState.Errors = 0;
                pollState.NextAttempt = now + AddJitter(IntervalForAge(now - invoice.CreatedAt));
                break;
        }
    }

    private async Task RemoveAsync(CoinsnapInvoiceState invoice, CancellationToken cancellationToken)
    {
        _pollStates.TryRemove(invoice.PaymentHash, out _);
        await _states.RemoveAsync(invoice.PaymentHash, invoice.ConnectionIdentity, cancellationToken);
    }

    private async Task ScheduleFailureAsync(
        CoinsnapInvoiceState invoice, PollState state, TimeSpan? requestedDelay, CancellationToken cancellationToken)
    {
        if (_clock.UtcNow > invoice.ExpiresAt + GiveUpAfterExpiry)
        {
            _logger.LogWarning(
                "Giving up on Coinsnap invoice {PaymentHash}: its settlement could not be checked for {Hours} hours after expiry",
                invoice.PaymentHash, GiveUpAfterExpiry.TotalHours);
            await RemoveAsync(invoice, cancellationToken);
            return;
        }
        ScheduleFailure(state, requestedDelay);
    }

    private void ScheduleFailure(PollState state, TimeSpan? requestedDelay)
    {
        state.Errors++;
        var exponentialMs = ScanInterval.TotalMilliseconds * Math.Pow(2, Math.Min(state.Errors, 16));
        var delay = TimeSpan.FromMilliseconds(Math.Min(exponentialMs, MaxBackoff.TotalMilliseconds));
        if (requestedDelay is { } serverDelay && serverDelay > delay)
            delay = serverDelay > MaxBackoff ? MaxBackoff : serverDelay;
        var scheduled = AddJitter(delay);
        if (scheduled > MaxBackoff)
            scheduled = MaxBackoff;
        if (requestedDelay is { } minimumRequested)
        {
            var minimum = minimumRequested > MaxBackoff ? MaxBackoff : minimumRequested;
            if (scheduled < minimum)
                scheduled = minimum;
        }
        state.NextAttempt = _clock.UtcNow + scheduled;
    }

    private bool IsDue(CoinsnapInvoiceState invoice, DateTimeOffset now)
    {
        if (invoice.Settled)
            return now > invoice.ExpiresAt + SettledGrace;
        return !_pollStates.TryGetValue(invoice.PaymentHash, out var state) || now >= state.NextAttempt;
    }

    internal static Network NetworkFor(CoinsnapInvoiceState state) =>
        (string.IsNullOrEmpty(state.NetworkName) ? null : Network.GetNetwork(state.NetworkName)) ?? Network.Main;

    // Invoice age below which each poll interval applies; older invoices use AgedInterval.
    private static readonly (TimeSpan Below, TimeSpan Interval)[] PollSteps =
    [
        (TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(2)),
        (TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5)),
        (TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(10)),
    ];
    private static readonly TimeSpan AgedInterval = TimeSpan.FromSeconds(30);

    internal static TimeSpan IntervalForAge(TimeSpan age)
    {
        foreach (var (below, interval) in PollSteps)
        {
            if (age < below)
                return interval;
        }
        return AgedInterval;
    }

    internal static TimeSpan AddJitter(TimeSpan value)
    {
        var multiplier = 0.8 + Random.Shared.NextDouble() * 0.4;
        return TimeSpan.FromMilliseconds(Math.Max(1, value.TotalMilliseconds * multiplier));
    }
}
