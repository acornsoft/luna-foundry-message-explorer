# Sample collection: order app, a day in the life (stub)

Not built yet. The `erp-standin` collection already covers the order, confirm, ship, and credit-hold part. This collection will add:

- Generator: shoppers browse, add to cart, order, pay, and sometimes return, across a simulated day.
- Simulators: a payment stand-in (authorize, decline, timeout) and the ERP stand-in.
- Assertions: every paid order reaches shipped or rejected, every return produces a refund event, and no order is charged twice.
- Observers: order events, refunds, and an orders table through a SQL observer plugin.

See `docs/design/test-jobs.md`, section 9.2. Synthetic data only.
