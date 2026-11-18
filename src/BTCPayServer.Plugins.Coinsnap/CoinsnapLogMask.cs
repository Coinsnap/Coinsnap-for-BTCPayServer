namespace BTCPayServer.Plugins.Coinsnap;

/// <summary>
/// Logs end up in support tickets and backups, so they never carry a full store ID. The last four
/// characters are enough to tell two stores apart when reading a log.
/// </summary>
internal static class CoinsnapLogMask
{
    public static string StoreId(string? storeId)
    {
        var trimmed = storeId?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return "(none)";
        return trimmed.Length <= 4 ? "…" : "…" + trimmed[^4..];
    }
}
