using System.Threading.Channels;
using BTCPayServer.Lightning;

namespace BTCPayServer.Plugins.Coinsnap;

internal sealed class CoinsnapInvoiceListener : ILightningInvoiceListener
{
    private readonly ICoinsnapInvoiceStateRepository _states;
    private readonly string _connectionIdentity;
    private readonly Func<CoinsnapInvoiceState, LightningInvoice> _invoiceFactory;
    private readonly Channel<LightningInvoice> _channel = Channel.CreateUnbounded<LightningInvoice>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private bool _disposed;

    public CoinsnapInvoiceListener(
        ICoinsnapInvoiceStateRepository states,
        string connectionIdentity,
        Func<CoinsnapInvoiceState, LightningInvoice> invoiceFactory)
    {
        _states = states;
        _connectionIdentity = connectionIdentity;
        _invoiceFactory = invoiceFactory;
        _states.Settled += OnSettled;
    }

    public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
    {
        return await _channel.Reader.ReadAsync(cancellation);
    }

    private void OnSettled(CoinsnapInvoiceState state)
    {
        if (!state.ConnectionIdentity.Equals(_connectionIdentity, StringComparison.Ordinal))
            return;
        try
        {
            _channel.Writer.TryWrite(_invoiceFactory(state));
        }
        catch
        {
            // A malformed persisted entry is ignored; the core poll path will continue to retry it.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _states.Settled -= OnSettled;
        _channel.Writer.TryComplete();
    }
}
