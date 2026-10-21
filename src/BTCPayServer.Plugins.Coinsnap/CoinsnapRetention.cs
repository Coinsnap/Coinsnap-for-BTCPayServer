namespace BTCPayServer.Plugins.Coinsnap;

/// <summary>
/// How long the plugin keeps invoice state, preimages included, and how much it tracks at once.
/// A settled invoice is kept for an hour after its BOLT11 expires, long enough for BTCPay to record
/// the payment after a restart. An unsettled one is re-checked through that hour and dropped after a
/// check past it, or a day after expiry if the server cannot answer at all.
/// </summary>
internal static class CoinsnapRetention
{
    public static readonly TimeSpan SettledGrace = TimeSpan.FromHours(1);
    public static readonly TimeSpan ExpiredGrace = TimeSpan.FromHours(1);
    public static readonly TimeSpan GiveUpAfterExpiry = TimeSpan.FromHours(24);

    // Every change rewrites the whole set, so it must stay bounded. Only invoices that can still be paid
    // count towards this cap: a store over it gets no new Lightning invoices rather than evicting one a
    // customer may be paying, and an expired invoice no longer holds a slot. The cap is per store only,
    // so flooded public pay links cannot block the other stores on the server.
    public const int MaxPayablePerStore = 500;

    // Ceilings on everything kept, per store and per server. Past one, only entries no longer worth
    // keeping are evicted, oldest first: past retention, or unpaid and past the re-check hour. Nothing
    // payable, recently settled or still being re-checked is ever evicted; if that is not enough, new
    // invoices are refused. A store's ceiling only ever evicts that store's own entries. At the server
    // ceiling only stores holding their fair share or more are refused, which keeps the total under
    // twice the ceiling.
    public const int MaxTrackedPerStore = 4000;
    public const int MaxTracked = 20000;

    public static bool IsPayable(CoinsnapInvoiceState state, DateTimeOffset now) =>
        !state.Settled && state.ExpiresAt > now;

    public static bool IsEvictable(CoinsnapInvoiceState state, DateTimeOffset now) =>
        IsPastRetention(state, now) || (!state.Settled && now > state.ExpiresAt + ExpiredGrace);

    public static bool IsPastRetention(CoinsnapInvoiceState state, DateTimeOffset now) =>
        state.Settled ? now > state.ExpiresAt + SettledGrace : now > state.ExpiresAt + GiveUpAfterExpiry;
}
