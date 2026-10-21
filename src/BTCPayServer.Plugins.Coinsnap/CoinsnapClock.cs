namespace BTCPayServer.Plugins.Coinsnap;

internal interface ICoinsnapClock
{
    DateTimeOffset UtcNow { get; }
}

internal sealed class SystemCoinsnapClock : ICoinsnapClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
