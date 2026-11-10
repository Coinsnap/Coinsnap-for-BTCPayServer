# Changelog

All notable changes to Coinsnap Wallet for BTCPay Server are listed here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-11-10

First beta release.

### Added

- Receive Lightning payments in a Coinsnap Wallet through a `@coinsnap.app` Lightning Address,
  without running a Lightning node or needing a Coinsnap Merchant account or API key.
- Per-store setup: each BTCPay store connects its own Lightning Address, or several stores share one.
- Invoices are marked paid only after the plugin verifies the payment proof returned by Coinsnap.
- Lightning invoices and payment proofs are recorded under Off-Chain Payments for reconciliation.
- Variable-amount top-ups through BTCPay Server's LNURL flow.
- Greenfield API endpoint to get a store's connection string:
  `GET /api/v1/stores/{storeId}/coinsnap/connection-string`.

[0.1.0]: https://github.com/Coinsnap/Coinsnap-for-BTCPayServer/releases/tag/v0.1.0
