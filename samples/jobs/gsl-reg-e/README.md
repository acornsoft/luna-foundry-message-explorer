# Sample collection: Gotham Savings and Loan (GSL) Reg E dispute (stub)

Not built yet. This collection waits for the GSL Reg E dispute class library and console runner, which arrive in their own PR under `src/Scenarios/`.

Planned shape (see `docs/design/test-jobs.md`, section 9.1):

- Generator: synthetic customers open Reg E disputes over a simulated business day, from data-driven rows.
- Simulator: a card-network stand-in that acknowledges a chargeback request, then resolves it after a delay.
- Assertion: a provisional credit decision within the configured window of simulated time, and a notice for every decision.
- Observer: the decisions topic and the notices queue.

Synthetic data only.
