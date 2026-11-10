using System.Globalization;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Coinsnap;

/// <summary>
/// Keeps BTCPay's "less than" amount limit for Lightning and LNURL at the Coinsnap address's maximum,
/// so BTCPay leaves both off an invoice Coinsnap could not pay. Only limits the plugin wrote are changed.
/// </summary>
internal static class CoinsnapAmountLimits
{
    internal const string RecordKey = "coinsnapAmountLimits";
    private const string Currency = "BTC";

    private static readonly PaymentMethodId[] Methods =
    [
        PaymentTypes.LN.GetPaymentMethodId("BTC"),
        PaymentTypes.LNURL.GetPaymentMethodId("BTC")
    ];

    internal static decimal ToBitcoin(long millisatoshis) => millisatoshis / 100_000_000_000m;

    /// <summary>Sets the limits to <paramref name="maximumBtc"/>, or removes the plugin's own when null. Returns whether the blob changed.</summary>
    internal static bool Apply(StoreBlob blob, decimal? maximumBtc)
    {
        blob.PaymentMethodCriteria ??= [];
        var record = blob.AdditionalData.TryGetValue(RecordKey, out var token) && token is JObject saved
            ? (JObject)saved.DeepClone()
            : new JObject();
        var changed = false;

        foreach (var method in Methods)
        {
            var key = method.ToString();
            var existing = blob.PaymentMethodCriteria.Find(c => c.PaymentMethod == method);
            var written = record.Value<string>(key) is { } text ? decimal.Parse(text, CultureInfo.InvariantCulture) : (decimal?)null;
            var ours = existing is { Above: false, Value: { } value } && written == value.Value &&
                       value.Currency.Equals(Currency, StringComparison.OrdinalIgnoreCase);

            if (maximumBtc is not { } maximum)
            {
                if (ours)
                {
                    blob.PaymentMethodCriteria.Remove(existing!);
                    changed = true;
                }
                record.Remove(key);
                continue;
            }

            if (existing is null)
            {
                blob.PaymentMethodCriteria.Add(new PaymentMethodCriteria
                {
                    PaymentMethod = method,
                    Value = new CurrencyValue { Value = maximum, Currency = Currency },
                    Above = false
                });
                changed = true;
            }
            else if (ours)
            {
                if (existing.Value.Value != maximum)
                {
                    existing.Value = new CurrencyValue { Value = maximum, Currency = Currency };
                    changed = true;
                }
            }
            else
            {
                record.Remove(key);
                continue;
            }
            record[key] = maximum.ToString(CultureInfo.InvariantCulture);
        }

        var hadRecord = blob.AdditionalData.TryGetValue(RecordKey, out var before);
        if (record.Count == 0)
        {
            if (hadRecord)
            {
                blob.AdditionalData.Remove(RecordKey);
                changed = true;
            }
        }
        else if (!hadRecord || !JToken.DeepEquals(before, record))
        {
            blob.AdditionalData[RecordKey] = record;
            changed = true;
        }
        return changed;
    }
}
