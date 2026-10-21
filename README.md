# Coinsnap Wallet for BTCPay Server

**Accept Lightning payments with BTCPay Server without running your own Lightning node.**

Coinsnap Wallet for BTCPay Server is a receive-only Lightning integration that lets a BTCPay merchant send Lightning payments directly to a Coinsnap Wallet (https://coinsnap.io/wallet).

The merchant only needs a Coinsnap Lightning Address such as:

`{username}@coinsnap.app`

No Coinsnap Merchant account, API key, Lightning node, channel management, wallet seed, or spending credentials are required.

## How it works

The merchant continues to use BTCPay Server for:

- invoices
- orders
- payment status
- transaction records
- store management

Coinsnap Wallet is only the receiving wallet.

The payment flow is:

**Customer → BTCPay Server → Coinsnap Plugin → Coinsnap Lightning Address → Coinsnap Wallet**

BTCPay requests a Lightning invoice through the configured Coinsnap Lightning Address and automatically detects settlement through LUD-21 payment verification.

BTCPay never receives access to the Coinsnap Wallet seed or spending keys.

## Current status

Version **0.1.0** is an early testing release.

Current target:

- BTCPay Server 2.4.5 or a later 2.4.x release (the only versions tested; BTCPay refuses to load
  the plugin elsewhere)
- Bitcoin mainnet
- Coinsnap Lightning Addresses on `coinsnap.app`
- receive-only Lightning payments

The plugin has automated test coverage, but the current version should be considered **beta/test software until real mainnet BTCPay payments have been successfully tested by multiple environments**.

## Merchant requirements

You need:

- BTCPay Server 2.4.5 to 2.4.x
- permission to install and restart BTCPay plugins
- a Coinsnap Wallet
- a Coinsnap Lightning Address ending in `@coinsnap.app`

Example:

`{username}@coinsnap.app`

No Coinsnap Merchant account is required.

No API key is required.

## Wallet accounts

The plugin treats the Lightning Address as the destination account.

Example:

`{username}@coinsnap.app`

The plugin contains no currency conversion logic. Account routing is handled entirely by Coinsnap Wallet.

## Install the plugin

1. Download `BTCPayServer.Plugins.Coinsnap.btcpay` from the release, and check it against the
   published SHA-256 checksum.
2. In BTCPay, open the **Plugins** menu → **Installed Plugins**, scroll to **Upload Plugin**, select
   the file and press **Upload**.
3. Press **Restart now** at the top of the page. After the restart the plugin is listed under
   **Installed Plugins**.

Installing plugins needs a server admin. BTCPay refuses to load the plugin on versions outside
2.4.5 to 2.4.x.

## Configure BTCPay Server

1. Select the store, then in the left menu under **Wallets** click **Lightning**. For a store
   without Lightning this opens **Set up a Lightning node**; otherwise click **Change connection**.
2. Choose the **Coinsnap Wallet** tab, next to "Use internal node" and "Use custom node".
3. Enter your Coinsnap Lightning Address, `{username}@coinsnap.app`.
4. Optionally press **Test connection**: the plugin checks the address and fetches its LNURL-pay
   metadata. On success BTCPay shows its standard message, "Connection to the Lightning node
   successful. No public address has been configured."; the second sentence is expected, since there
   is no node of your own.
5. Press **Save**.

From then on BTCPay offers Lightning on new invoices, paid into your Coinsnap Wallet.

**After setup**, BTCPay's Lightning page describes the connection as "Custom Node" with
"Coinsnap Wallet (receive only)". That is expected: BTCPay labels every plugin connection a custom
node. Its **Public Node Info** page shows "Online" and "No public address available", since there is
no node of your own to connect to. Anything that needs such a node (balances, channels, payouts and
refunds over Lightning) is not available, because the plugin only receives.

The internal BTCPay connection string is generated automatically. It carries the store's id and a
binding that only this BTCPay server can issue, so another store on the same server cannot claim
yours by copying your Lightning Address. Merchants should not enter or edit it by hand.

To configure a store through the Greenfield API instead, get the connection string for it from
`GET /api/v1/stores/{storeId}/coinsnap/connection-string?ln-address={address}` (requires
`btcpay.store.canmodifystoresettings` on that store) and save it as the store's BTC Lightning
connection (`PUT /api/v1/stores/{storeId}/payment-methods/BTC-LN`). Unlike the settings page, this
does not turn on BTCPay's LNURL (the checkout's LNURL and the store's Lightning Address); to offer
it, also enable `PUT /api/v1/stores/{storeId}/payment-methods/BTC-LNURL` with
`{"enabled": true, "config": {}}`.

The binding is revoked and reissued automatically whenever a user is removed from the store, has
their store role changed, or the store's roles change, and the store's own connection is re-saved
with the new one, so a former manager cannot reuse a copy. To revoke it on demand, for example after
a suspected leak, call `POST /api/v1/stores/{storeId}/coinsnap/rotate-binding` (same permission).

**Upgrading from an earlier build:** connections saved before the binding existed stop working
until they are saved again. Open the store's Coinsnap Wallet settings and press **Save**.

## Payment flow

For every payment the plugin:

1. resolves the Coinsnap Lightning Address through LNURL-pay;
2. reads the server-provided payment limits;
3. requests a BOLT11 Lightning invoice for the exact amount;
4. validates the returned invoice;
5. stores the explicit LUD-21 verification URL;
6. returns the Lightning invoice to BTCPay;
7. polls the verification endpoint;
8. reports the payment as paid only after settlement is confirmed.

For settled payments the plugin verifies:

`SHA256(preimage) == payment_hash`

A temporary server error or `settled=false` response is never interpreted as payment success.

Each BOLT11 expires together with the BTCPay invoice: the plugin passes BTCPay's expiry to Coinsnap and
refuses a BOLT11 that expires more than two minutes earlier or later, so it cannot be paid after
BTCPay stops watching it.

### When Coinsnap is slow or unavailable

- Requests to Coinsnap time out after 20 seconds; **Test connection** gives up after 15.
- After three consecutive failures to reach Coinsnap for an address, new invoices for that address
  are offered without Lightning (or refused, if Lightning is the store's only method) for 30 seconds,
  instead of making each customer wait. An answer Coinsnap gives, even an error, does not count.
- A Lightning Address's metadata is reused for up to 60 seconds, and only after it produced a valid
  invoice.
- Invoices already issued keep being checked throughout, and payments made during an outage are
  recorded once Coinsnap answers again.

## Receive-only security model

The plugin is intentionally receive-only.

BTCPay Server cannot:

- spend Coinsnap Wallet funds
- pay Lightning invoices
- query wallet balances
- open or close channels
- access wallet seeds or private keys
- automatically refund from the Coinsnap Wallet

Refunds or outgoing payments must be performed separately from the merchant's wallet.

## Privacy

The plugin does not send Coinsnap:

- customer names
- customer email addresses
- shipping information
- cart contents
- BTCPay API credentials
- wallet spending credentials

Only the information required to create and verify a Lightning payment is transmitted.

### Matching payments to orders, and what a payment proves

- **The customer's wallet never shows the order.** It shows Coinsnap's fixed text for your Lightning
  Address, because LNURL-pay metadata is set by the receiver. The plugin does not send order or cart
  details to Coinsnap, so Coinsnap's records do not name the order either.
- **Match by payment hash.** BTCPay records every Lightning payment under its payment hash: the
  payment `id` in `GET /api/v1/stores/{storeId}/invoices/{invoiceId}/payment-methods`. Use it to
  match Coinsnap wallet entries, refunds or customer queries to orders, not amount and time.
- **Disputes.** A customer's proof of payment is the BOLT11 and its preimage; the payment hash is
  the SHA-256 of the preimage, and leads to the BTCPay order.
- **Settled is not credited.** BTCPay marks an invoice paid when Coinsnap returns a valid preimage,
  which proves the Lightning payment settled. Crediting your Coinsnap balance is Coinsnap's
  responsibility; check your wallet.
- **Your Lightning Address is public.** It appears in every invoice, and stores using the same
  address can be linked to each other. Use a separate address per store if that matters.

### What the plugin stores, and for how long

To detect payments across restarts, the plugin keeps each Lightning invoice it created in one
BTCPay setting: the BOLT11, its verify URL, the Lightning Address and the store. Payment preimages
are encrypted with BTCPay's Data Protection keys.

- A paid invoice is kept for one hour after its BOLT11 expires, so BTCPay can still record the
  payment after a restart.
- An unpaid invoice is re-checked through that hour and removed after a check past it. If Coinsnap
  cannot be reached at all, it is removed a day after expiry.
- Each store can have at most 500 Coinsnap invoices that can still be paid, and the server 5,000.
  Past that, new invoices get no Lightning option until older ones expire (or are refused, if
  Lightning is the store's only payment method). Expired invoices do not count; nothing payable is
  ever dropped. A store can keep at most 4,000 invoices in total and the server 20,000; past that,
  only invoices already past their re-check hour are dropped (oldest first, and only the store's
  own), and if that is not enough, new invoices are refused.

## Build and test

Requirements:

- .NET SDK 10
- Git submodules
- Docker for local BTCPay development

```bash
git submodule update --init --recursive
dotnet restore BTCPayServer.Plugins.Coinsnap.slnx
dotnet test tests/BTCPayServer.Plugins.Coinsnap.Tests
./pack.sh
```

The package script creates the BTCPay plugin package and checksum under:

`artifacts/BTCPayServer.Plugins.Coinsnap/<version>/`

## Current limitations

Version 0.1.0 currently supports:

- Bitcoin mainnet only
- fixed positive Lightning amounts
- whole-satoshi amounts only
- Coinsnap Lightning Addresses on `coinsnap.app`
- receive-only operation
- a Coinsnap server that honours the invoice callback's `expiry` (`coinsnap.app` does, verified in
  October 2026); a BOLT11 that does not match BTCPay's invoice expiry is refused

Automatic refunds, outgoing Lightning payments, wallet balance access, channel management and arbitrary Lightning Address providers are outside the scope of version 0.1.0.

## License

MIT License.

This project builds on BTCPay Server's public plugin APIs. See `THIRD_PARTY_NOTICES.md`.

No upstream project or contributor endorses this plugin.
