using BTCPayServer.Lightning;
using NBitcoin;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap;

internal sealed record CoinsnapBolt11(
    string Raw,
    string PaymentHash,
    LightMoney Amount,
    DateTimeOffset ExpiresAt,
    string? DescriptionHash);

internal interface ICoinsnapBolt11Parser
{
    CoinsnapBolt11 Parse(string paymentRequest, Network network);
}

internal sealed class CoinsnapBolt11Parser : ICoinsnapBolt11Parser
{
    public CoinsnapBolt11 Parse(string paymentRequest, Network network)
    {
        BOLT11PaymentRequest parsed;
        try
        {
            parsed = BOLT11PaymentRequest.Parse(paymentRequest, network);
        }
        catch (Exception ex)
        {
            throw new FormatException($"Coinsnap returned an invalid BOLT11 for {network.Name}.", ex);
        }

        var paymentHash = parsed.PaymentHash?.ToString();
        if (string.IsNullOrWhiteSpace(paymentHash))
            throw new FormatException("Coinsnap returned a BOLT11 without a payment hash.");
        if (parsed.MinimumAmount is null)
            throw new FormatException("Coinsnap returned an amountless BOLT11.");

        return new CoinsnapBolt11(
            paymentRequest,
            paymentHash.ToLowerInvariant(),
            parsed.MinimumAmount,
            parsed.ExpiryDate,
            parsed.DescriptionHash?.ToString()?.ToLowerInvariant());
    }
}
