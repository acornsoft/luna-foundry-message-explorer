# Backlog

Items are grouped by theme. Nothing here is built unless it says so. Synthetic data only.

## Service Bus parity (see [parity matrix](parity-matrix.md))

- Entity tree: queues, topics, subscriptions, with a name filter.
- Topics grid and subscriptions grid: built in the fifth PR (Topics tab, read-only, per-cell dead-letter highlight).
- Read-only rules view per subscription.
- Peek view for active, dead-letter, and scheduled messages, with redacted bodies.
- Dead-letter view with reason and description.
- Session browse without taking a session lock.
- Saved connections: built in the seventh PR. Connection strings saved by name in the OS secure store only (Keychain on macOS).
- Entra accounts and namespace discovery: built in the seventh PR (Accounts tab, several accounts, ARM GETs across tenants and subscriptions).
- Per-account removal from the shared token cache (needs MSAL directly; Azure.Identity has no call for it).
- Choose which signed-in account a typed namespace or endpoint-only string uses (today: the first one).
- Export inventory to JSON or CSV.
- Azure Monitor metrics charts.
- Admin module actions (send, resubmit) behind approval, with an audit row.

## New broker providers (behind `IBrokerProvider`)

### Apache Kafka (planned, not built)

- Read-only inventory: topics, partitions, consumer groups, and lag.
- Peek by reading with a dedicated consumer group that never commits offsets.
- Test only against a local container, for example `docker compose` with Redpanda or Apache Kafka in KRaft mode. No shared or cloud cluster in CI.

### AWS SQS and SNS (planned, not built)

- Read-only inventory: SQS queues with approximate visible, in-flight, and delayed counts, plus the redrive (dead-letter) queue. SNS topics and subscriptions.
- Test only against LocalStack. David has no AWS account, so there are no cloud integration tests.
- Note for design: an SQS receive changes visibility. It is not a peek. Observe mode must not call it.

## Test jobs

- First slice built in the third PR: job files, stdio JSON-RPC plugin host, in-memory broker, `me-jobs` CLI with JUnit, sample plugins and jobs. See [test jobs design](design/test-jobs.md), section 11.
- Jobs panel and live view in the app: built in the fifth PR. Pick a collection, job, environment, and pace; Run and Cancel; expected versus actual per correlation ID with status, hop timeline, latency, plugin event counters, a status filter, and Export JUnit.
- Live view later: a drawn hop graph (the timeline is text today), saved runs, and comparing two runs.
- GSL Reg E and order-app sample collections. Stubs only.
- Replay store, gRPC transport, sending to sandbox brokers behind an allow-list, redaction of saved run records.
- `hop-relay`: ordering gates and timer-driven reprocessing.

## Packaging

- macOS DMG (arm64 and x64), ad-hoc signed: built in the sixth PR. Developer ID signing and notarization are wired and wait for the certificate and API key. See [install on macOS](install-macos.md).
- Windows and Linux packages. Not started.

## Learning

- Video learning deliverable for LFME, to hand off to Jodie (Videographer) later. Not started.
