# Coinsnap Wallet for BTCPay Server

**Accept Lightning payments in BTCPay Server, paid straight into your [Coinsnap Wallet](https://coinsnap.io/wallet). No Lightning node needed.**

You only enter your Coinsnap Lightning Address (`name@coinsnap.app`). No Coinsnap Merchant account,
API key, node, channels or wallet seed are needed. BTCPay keeps handling your invoices, orders and
payment status; Coinsnap Wallet just receives the money.

**Version 0.1.0 is a beta.** Test it with small amounts before relying on it.

## Requirements

- BTCPay Server 2.4.5 or a later 2.4.x version, on Bitcoin mainnet
- a server admin to install the plugin; after that, each store owner sets up their own store
- a Coinsnap Wallet with a Lightning Address ending in `@coinsnap.app`

## Install

A server admin does this once per server.

1. Build `BTCPayServer.Plugins.Coinsnap.btcpay` with `./pack.sh` (see [Build](#build)), or contact
   Coinsnap for the package and its SHA-256, and check the file with `sha256sum` before installing.
2. In BTCPay: **Plugins → Installed Plugins → Upload Plugin**, select the file, **Upload**.
3. Press **Restart now**.

## Set up a store

Any store owner can do this for their own store.

1. In your store, go to **Wallets → Lightning** and choose **Set up a Lightning node** (or **Change
   connection**).
2. Open the **Coinsnap Wallet** tab.
3. Enter your Lightning Address and press **Test connection**. BTCPay shows "Connection to the
   Lightning node successful. No public address has been configured."; the second sentence is normal.
4. Press **Save**.

New invoices now offer Lightning, paid into your Coinsnap Wallet. A payment usually shows as Settled
in BTCPay within a few seconds.

## Good to know

- **Receive only.** BTCPay cannot spend from your Coinsnap Wallet, see its balance, open channels or
  send refunds over Lightning. Do refunds from your wallet directly. BTCPay labels the connection
  "Custom Node, Coinsnap Wallet (receive only)"; that is expected.
- **Paid means proven.** An invoice is marked paid only after Coinsnap returns the payment's
  cryptographic proof, which the plugin checks.
- **Privacy.** The plugin sends Coinsnap only what a Lightning payment needs: no customer names,
  emails, addresses or cart contents. Your Lightning Address is public and appears in every invoice;
  use a separate address per store if you don't want stores linked.
- **Matching payments.** Each paid invoice lists its Lightning invoice and payment proof under
  **Off-Chain Payments**; use them to match Coinsnap Wallet entries to orders.
- **Several stores** on one server can each use their own address, or share one.

## Limitations

- Bitcoin mainnet only, and only `@coinsnap.app` Lightning Addresses
- fixed amounts in whole satoshis, within the minimum and maximum Coinsnap sets. Invoices outside
  those limits are offered without Lightning; if Lightning is the store's only payment method, such
  an invoice cannot be created
- top-up invoices without a fixed amount are paid through BTCPay's LNURL: the customer's wallet
  asks for the amount
- no outgoing payments, balances, channels or automatic refunds

## Build

Requires the .NET 10 SDK.

```bash
git submodule update --init --recursive
dotnet test tests/BTCPayServer.Plugins.Coinsnap.Tests
./pack.sh
```

The package and its `SHA256SUMS` are written to `artifacts/BTCPayServer.Plugins.Coinsnap/<version>/`.
To load the plugin while debugging BTCPay locally, run `./plugin-register.sh`.

## Support

Questions or problems: [support@coinsnap.io](mailto:support@coinsnap.io). Please include your BTCPay version, the invoice ID and,
for a payment, its Lightning payment hash. Never send your wallet seed or passwords.

## License

MIT. See `LICENSE` and `THIRD_PARTY_NOTICES.md`. Not endorsed by BTCPay Server or its contributors.
