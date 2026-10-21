namespace BTCPayServer.Plugins.Coinsnap;

public sealed class CoinsnapInvoiceState
{
    public string PaymentHash { get; set; } = "";
    public string Bolt11 { get; set; } = "";
    public string VerifyUrl { get; set; } = "";
    public string LightningAddress { get; set; } = "";
    public string ConnectionIdentity { get; set; } = "";
    public string? StoreId { get; set; }
    public string? NetworkName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Settled { get; set; }
    public string? Preimage { get; set; }
    public DateTimeOffset? PaidAt { get; set; }

    public CoinsnapInvoiceState Clone() => new()
    {
        PaymentHash = PaymentHash,
        Bolt11 = Bolt11,
        VerifyUrl = VerifyUrl,
        LightningAddress = LightningAddress,
        ConnectionIdentity = ConnectionIdentity,
        StoreId = StoreId,
        NetworkName = NetworkName,
        CreatedAt = CreatedAt,
        ExpiresAt = ExpiresAt,
        Settled = Settled,
        Preimage = Preimage,
        PaidAt = PaidAt
    };
}

public sealed class CoinsnapPersistedInvoiceStates
{
    public int Version { get; set; } = 1;
    public List<CoinsnapInvoiceState> Invoices { get; set; } = [];
}
