using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using NBitcoin;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Coinsnap;

public sealed class CoinsnapLnAddressLightningClient : IExtendedLightningClient
{
    private const string ReceiveOnlyMessage =
        "Coinsnap Wallet is configured for receiving only. BTCPay cannot spend wallet funds or perform balance, refund, node, or channel operations.";

    private readonly CoinsnapLightningAddress _address;
    private readonly string _connectionIdentity;
    private readonly string _storeId;
    private readonly Network _network;
    private readonly CoinsnapLnurlService _lnurl;
    private readonly ICoinsnapInvoiceStateRepository _states;
    private readonly ICoinsnapStoreConfigLookup _storeConfig;

    internal CoinsnapLnAddressLightningClient(
        CoinsnapLightningAddress address,
        string connectionIdentity,
        string storeId,
        Network network,
        CoinsnapLnurlService lnurl,
        ICoinsnapInvoiceStateRepository states,
        ICoinsnapStoreConfigLookup storeConfig)
    {
        _address = address;
        _connectionIdentity = connectionIdentity;
        _storeId = storeId;
        _network = network;
        _lnurl = lnurl;
        _states = states;
        _storeConfig = storeConfig;
    }

    public async Task<LightningInvoice> CreateInvoice(
        LightMoney amount,
        string description,
        TimeSpan expiry,
        CancellationToken cancellation = default)
    {
        await EnsureStoreDeclaresConnectionAsync(cancellation);
        return await _lnurl.CreateInvoiceAsync(
            _address,
            _connectionIdentity,
            _storeId,
            _network,
            amount,
            expiry,
            null,
            cancellation);
    }

    public async Task<LightningInvoice> CreateInvoice(
        CreateInvoiceParams createInvoiceRequest,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(createInvoiceRequest);
        await EnsureStoreDeclaresConnectionAsync(cancellation);
        return await _lnurl.CreateInvoiceAsync(
            _address,
            _connectionIdentity,
            _storeId,
            _network,
            createInvoiceRequest.Amount,
            createInvoiceRequest.Expiry,
            ExpectedDescriptionHash(createInvoiceRequest),
            cancellation);
    }

    // Checked on use rather than in Create: BTCPay builds a client to validate a connection before saving it.
    private async Task EnsureStoreDeclaresConnectionAsync(CancellationToken cancellation)
    {
        var configured = await _storeConfig.GetLightningConnectionStringAsync(_storeId, cancellation);
        if (!CoinsnapStoreClaim.IsVerified(configured, _address, _storeId))
            throw new InvalidOperationException("This store does not declare this Coinsnap connection.");
    }

    internal static string? ExpectedDescriptionHash(CreateInvoiceParams request)
    {
        if (request.DescriptionHash is { } explicitHash)
            return explicitHash.ToString();
        if (!request.DescriptionHashOnly || request.Description is null)
            return null;

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Description)))
            .ToLowerInvariant();
    }

    public async Task<LightningInvoice?> GetInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        await EnsureStoreDeclaresConnectionAsync(cancellation);
        await _states.EnsureLoadedAsync(cancellation);
        if (!_states.TryGet(invoiceId, _connectionIdentity, out var state))
            return null;

        var result = await _lnurl.VerifyAsync(state, _network, cancellation);
        switch (result.Outcome)
        {
            case CoinsnapVerificationOutcome.Paid when result.Invoice?.Preimage is { } preimage:
                if (!state.Settled)
                    await _states.MarkSettledAsync(state.PaymentHash, preimage, result.Invoice.PaidAt ?? DateTimeOffset.UtcNow, cancellation);
                return result.Invoice;
            default:
                return result.Invoice;
        }
    }

    public Task<LightningInvoice?> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default) =>
        GetInvoice(paymentHash.ToString(), cancellation);

    public Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default) =>
        ListInvoices(new ListInvoicesParams(), cancellation);

    public async Task<LightningInvoice[]> ListInvoices(
        ListInvoicesParams request,
        CancellationToken cancellation = default)
    {
        await EnsureStoreDeclaresConnectionAsync(cancellation);
        await _states.EnsureLoadedAsync(cancellation);
        var invoices = _states.Snapshot(_connectionIdentity)
            .Select(s => _lnurl.BuildFromState(s, _network))
            .Where(i => request?.PendingOnly is not true || i.Status == LightningInvoiceStatus.Unpaid)
            .ToArray();
        return invoices;
    }

    public async Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
    {
        await EnsureStoreDeclaresConnectionAsync(cancellation);
        await _states.EnsureLoadedAsync(cancellation);
        return new CoinsnapInvoiceListener(
            _states,
            _connectionIdentity,
            state => _lnurl.BuildFromState(state, _network));
    }

    public async Task CancelInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        await EnsureStoreDeclaresConnectionAsync(cancellation);
        await _states.RemoveAsync(invoiceId, _connectionIdentity, cancellation);
    }

    public async Task<ValidationResult?> Validate()
    {
        if (!CoinsnapLightningConnectionStringHandler.IsNetworkAllowed(_network, CoinsnapConstants.IsHostOverridden))
            return new ValidationResult("Coinsnap Wallet receiving is available only on Bitcoin mainnet.");
        // Test connection runs on a request; do not let a slow Coinsnap hold it open.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await _lnurl.GetMetadataAsync(_address, timeout.Token);
            return ValidationResult.Success;
        }
        catch (Exception ex)
        {
            return new ValidationResult(ex.Message);
        }
    }

    public Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = default) =>
        throw new NotSupportedException(ReceiveOnlyMessage);

    public Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = default) =>
        throw new NotSupportedException(ReceiveOnlyMessage);

    public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default) =>
        throw new NotSupportedException(ReceiveOnlyMessage);

    public Task<LightningPayment?> GetPayment(string paymentHash, CancellationToken cancellation = default) =>
        Task.FromResult<LightningPayment?>(null);

    public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default) =>
        Task.FromResult(Array.Empty<LightningPayment>());

    public Task<LightningPayment[]> ListPayments(ListPaymentsParams request, CancellationToken cancellation = default) =>
        Task.FromResult(Array.Empty<LightningPayment>());

    public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default) =>
        throw new NotSupportedException(ReceiveOnlyMessage);

    public Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default) =>
        throw new NotSupportedException(ReceiveOnlyMessage);

    public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default) =>
        throw new NotSupportedException(ReceiveOnlyMessage);

    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellation = default) =>
        throw new NotSupportedException(ReceiveOnlyMessage);

    public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = default) =>
        throw new NotSupportedException(ReceiveOnlyMessage);

    public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default) =>
        throw new NotSupportedException(ReceiveOnlyMessage);

    public string? DisplayName => "Coinsnap Wallet (receive only)";
    public Uri? ServerUri => new($"https://{CoinsnapConstants.Host}");
}
