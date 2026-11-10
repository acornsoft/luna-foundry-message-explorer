# Backlog

Items are grouped by theme. Nothing here is built unless it says so. Synthetic data only.

## Service Bus parity (see [parity matrix](parity-matrix.md))

- Entity tree: queues, topics, subscriptions, with a name filter.
- Topics grid and subscriptions grid on top of the adapter calls that this PR already builds.
- Read-only rules view per subscription.
- Peek view for active, dead-letter, and scheduled messages, with redacted bodies.
- Dead-letter view with reason and description.
- Session browse without taking a session lock.
- Saved connections: host name and sign-in mode only. Secrets in the OS keychain.
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

## Test jobs (third PR, design only)

- Language-neutral plugin runner for synthetic system tests. See [test jobs design](design/test-jobs.md).

## Learning

- Video learning deliverable for LFME, to hand off to Jodie (Videographer) later. Not started.
