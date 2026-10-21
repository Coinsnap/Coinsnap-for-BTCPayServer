using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Coinsnap;

internal sealed class CoinsnapLnurlPayResponse
{
    [JsonProperty("status")]
    public string? Status { get; set; }

    [JsonProperty("reason")]
    public string? Reason { get; set; }

    [JsonProperty("tag")]
    public string? Tag { get; set; }

    [JsonProperty("callback")]
    public string? Callback { get; set; }

    [JsonProperty("minSendable")]
    public long? MinSendable { get; set; }

    [JsonProperty("maxSendable")]
    public long? MaxSendable { get; set; }

    [JsonProperty("metadata")]
    public string? Metadata { get; set; }

    [JsonProperty("commentAllowed")]
    public int? CommentAllowed { get; set; }
}

internal sealed class CoinsnapInvoiceCallbackResponse
{
    [JsonProperty("status")]
    public string? Status { get; set; }

    [JsonProperty("reason")]
    public string? Reason { get; set; }

    [JsonProperty("pr")]
    public string? PaymentRequest { get; set; }

    [JsonProperty("verify")]
    public string? Verify { get; set; }
}

internal sealed class CoinsnapVerifyResponse
{
    [JsonProperty("status")]
    public string? Status { get; set; }

    [JsonProperty("reason")]
    public string? Reason { get; set; }

    [JsonProperty("settled")]
    public bool? Settled { get; set; }

    [JsonProperty("preimage")]
    public string? Preimage { get; set; }

    [JsonProperty("pr")]
    public string? PaymentRequest { get; set; }
}
