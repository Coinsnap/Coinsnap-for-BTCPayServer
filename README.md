# Coinsnap Wallet for BTCPay Server

**Accept Bitcoin Lightning payments with your own BTCPay Server — without running your own Lightning node.**

Coinsnap Wallet for BTCPay Server is an open-source, receive-only plugin that lets BTCPay Server store owners receive Lightning payments directly in their [Coinsnap Wallet](https://coinsnap.io/wallet/).

Connect a Coinsnap Lightning Address such as `satoshi@coinsnap.app`. BTCPay Server continues to manage invoices, orders and payment status, while incoming Lightning payments are credited to the configured Coinsnap Wallet.

**No Lightning node, payment channels, inbound-liquidity management, Coinsnap Merchant account or API key required.**

> **Beta — version 0.1.0.** Test with small payments before using this plugin in production. It is not yet available in the official BTCPay Server Plugin Directory.

## Why use this plugin?

Running a Lightning node yourself introduces additional operational work: channel management, liquidity, monitoring and maintenance. This plugin provides a simpler receiving option while retaining your own BTCPay Server for checkout and invoicing.

- **Receive Lightning without operating a node.** No LND or Core Lightning node is required for this integration.
- **Direct wallet settlement.** Payments go to the Coinsnap Wallet associated with your Lightning Address.
- **Simple per-store setup.** Enter and test a Lightning Address; no Coinsnap Merchant account or API credentials are required.
- **Keep BTCPay Server workflows.** BTCPay Server continues to create invoices and track orders and payment status.
- **Receive-only permissions.** The plugin cannot spend funds, retrieve wallet balances or recovery information, or manage channels.
- **Multiple stores.** Each store can use a separate Lightning Address, or several stores can share one.
- **Open source.** Published under the MIT license.

## How it works

1. A BTCPay Server store creates an invoice or checkout request.
2. The plugin obtains a Lightning invoice for the store's configured Coinsnap Lightning Address.
3. The customer pays the Lightning invoice with a compatible wallet.
4. The payment is credited to the Coinsnap Wallet.
5. The plugin validates the cryptographic proof of payment returned by Coinsnap.
6. BTCPay Server records the verified payment and updates the invoice status.

The plugin affects **Lightning receiving only**; it does not replace an existing Bitcoin on-chain payment configuration.

## Requirements

- **BTCPay Server 2.4.7 or newer**, configured for Bitcoin mainnet.
- A **server administrator** to install the plugin once per BTCPay Server installation.
- A **Coinsnap Wallet** with a Lightning Address ending in `@coinsnap.app`.

After installation, each BTCPay store owner can configure their store independently.

Learn more about [Coinsnap Wallet](https://coinsnap.io/wallet/).

## Install the plugin

The plugin is currently distributed for manual installation, not through the official BTCPay Server Plugin Directory.

1. Build `BTCPayServer.Plugins.Coinsnap.btcpay` from source (see [Build from source](#build-from-source)), or contact Coinsnap for a compiled package and its SHA-256 checksum.
2. Verify the supplied package checksum before installation. For example, on Linux:
   ```bash
   sha256sum BTCPayServer.Plugins.Coinsnap.btcpay
   ```
3. Log in to BTCPay Server as a server administrator.
4. Open **Plugins → Installed Plugins → Upload Plugin**.
5. Select the `.btcpay` package and click **Upload**.
6. Click **Restart now** when prompted.

## Configure a BTCPay Server store

1. Open the store in BTCPay Server.
2. Go to **Wallets → Lightning**.
3. Select **Set up a Lightning node** (or **Change connection**, if a Lightning connection already exists).
4. Open the **Coinsnap Wallet** tab.
5. Enter your Coinsnap Lightning Address, for example `satoshi@coinsnap.app`.
6. Click **Test connection**. BTCPay Server should display:
   > Connection to the Lightning node successful. No public address has been configured.
   
   The second sentence is expected and does **not** indicate an error.
7. Click **Save**.

New invoices can now offer Lightning payments to the configured Coinsnap Wallet.

### Amount limits

Each Coinsnap Lightning Address accepts payments only up to a maximum amount. After you save the
connection, the plugin sets BTCPay Server's limit for **Lightning** and **LNURL** to that maximum,
under **Checkout Appearance → Enable payment methods only when amount is …**. BTCPay Server then
leaves Lightning off larger invoices, and the customer pays with another payment method if the store
has one.

If you set your own limit there for Lightning or LNURL, the plugin leaves it unchanged. If you switch
the store away from Coinsnap, the plugin removes the limits it set.

### Test your first payment

1. Create a small, fixed-amount invoice in your BTCPay Server store.
2. Pay its Lightning invoice from another Lightning wallet.
3. Verify that the payment is credited to your Coinsnap Wallet.
4. Confirm that BTCPay Server marks the invoice as paid/settled.
5. Check the invoice's **Off-Chain Payments** details to match the Lightning invoice and payment proof to the wallet entry.

Successful payments normally appear as settled in BTCPay Server within a few seconds. Verify the full flow before production use.

## Security, privacy and reconciliation

### Receive-only access

The BTCPay Server connection is shown as **Custom Node, Coinsnap Wallet (receive only)**. This is expected.

The plugin **cannot**:
- Send Lightning payments or automatically refund customers.
- Read or spend the Coinsnap Wallet balance.
- Retrieve a wallet seed, recovery phrase or private keys.
- Open, close or manage Lightning channels.

Process refunds separately from your Coinsnap Wallet.

### Verified payments

An invoice is marked paid only after the plugin checks the cryptographic payment proof returned by Coinsnap. It does not treat an unverified payment notification as proof of settlement.

### Privacy

The plugin sends only information needed to create and verify a Lightning payment. It does not send customer names, email addresses, shipping addresses or shopping-cart contents as part of the Lightning integration.

The configured Lightning Address is exposed in invoice payment information. To avoid linking stores through the same public address, use a different Coinsnap Lightning Address for each store.

### Multiple stores

Multiple stores on the same BTCPay Server can configure different addresses, such as `store-a@coinsnap.app` and `store-b@coinsnap.app` (illustrative examples only), or share one address. Using separate addresses generally makes reconciliation and store separation easier.

BTCPay Server stores the Lightning invoice and payment proof under **Off-Chain Payments**, enabling you to reconcile BTCPay invoices with the incoming Coinsnap Wallet payments.

## Current limitations (v0.1.0)

- **Bitcoin mainnet only.** Testnet and other networks are not supported.
- **Coinsnap addresses only.** The Lightning Address must end in `@coinsnap.app`.
- **Fixed amounts in whole satoshis, within your address's limits.** BTCPay Server does not offer Lightning for invoices above your address's maximum (see [Amount limits](#amount-limits)). When Lightning is the store's only payment method, such an invoice cannot be created.
- **Variable-amount top-ups.** Top-up invoices without a fixed amount use BTCPay Server's LNURL flow, in which the customer's wallet supplies the amount.
- **Receiving only.** No outgoing payments, automatic Lightning refunds, balance queries or channel operations.
- **Beta software.** Compatibility and invoice-settlement behavior must be tested before relying on the plugin for production payments.

## Build from source

Requires the **.NET 10 SDK**.

Clone the repository, including its submodules:

```bash
git clone --recurse-submodules https://github.com/Coinsnap/coinsnap-wallet-btcpay-plugin.git
cd coinsnap-wallet-btcpay-plugin
```

For an existing checkout, initialize submodules with:

```bash
git submodule update --init --recursive
```

Run tests and build the package:

```bash
dotnet test tests/BTCPayServer.Plugins.Coinsnap.Tests
./pack.sh
```

The plugin package and its `SHA256SUMS` file are generated under:

```text
artifacts/BTCPayServer.Plugins.Coinsnap/<version>/
```

For local debugging with BTCPay Server, run `./plugin-register.sh`.

## Development and release status

**Current version:** 0.1.0 (beta).

The next milestone is an end-to-end test on the **Coincharge BTCPay Server** installation. This test will cover installation, wallet connection, incoming Lightning payments, invoice settlement, reconciliation and error handling.

After successful testing and any necessary fixes, we plan to publish a public release and **apply for inclusion in the official BTCPay Server Plugin Directory**. Inclusion is subject to the BTCPay Server project's review and acceptance; this plugin is **not currently officially listed**.

## Frequently asked questions

**Do I need to run my own Lightning node?**  
No. The plugin receives Lightning payments through your Coinsnap Wallet.

**Do I need a Coinsnap Merchant account or an API key?**  
No. A Coinsnap Wallet with a supported `@coinsnap.app` Lightning Address is enough.

**Does Coinsnap replace my BTCPay Server?**  
No. You continue running BTCPay Server to manage your store, invoices and orders. The plugin provides a Lightning receiving connection.

**Where are the payments credited?**  
To the Coinsnap Wallet corresponding to the Lightning Address configured for the store.

**Can I still accept on-chain Bitcoin payments?**  
Yes. The plugin does not replace your existing on-chain payment setup.

**Can BTCPay Server send funds from my Coinsnap Wallet?**  
No. The integration is receive-only.

**Can different BTCPay stores use different wallets?**  
Yes. Each store can configure its own Coinsnap Lightning Address.

**Can I use another Lightning Address provider?**  
Not with version 0.1.0. This release supports addresses ending in `@coinsnap.app` only.

**Is it available in the official BTCPay Server Plugin Directory?**  
Not yet. Manual installation is required during the beta phase.

## Support

For questions or problems, contact [support@coinsnap.io](mailto:support@coinsnap.io).

Include your BTCPay Server version, plugin version, invoice ID, and (for a payment problem) the Lightning payment hash, together with the steps to reproduce the issue.

**Never share your wallet seed, recovery phrase, private keys or passwords.**

## License

MIT. See [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

This independently developed plugin is not endorsed by BTCPay Server or its contributors.
