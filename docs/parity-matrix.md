# Parity matrix: Service Bus Explorer ideas

This page lists the features of Paolo Salvatori's [Service Bus Explorer](https://github.com/paolosalvatori/ServiceBusExplorer) (MIT) and says where Luna Foundry Message Explorer stands on each one. We take **ideas only**. No code, icons, layouts, or other assets are copied. Our code is written from scratch on .NET 8 and Avalonia.

Status values:

- **Done (this PR)**: built and tested in the Gotham Savings and Loan (GSL) queue-list slice.
- **Planned**: on the [backlog](backlog.md).
- **Out of scope**: we do not plan to build it, for the reason given.

Read-only first: every write (send, receive, settle, create, delete, purge) stays outside observe mode. A write arrives only through the separate admin module with an approval step.

| Area | Feature in Service Bus Explorer | Our status | How we do it on the modern stack |
|---|---|---|---|
| Connect | Connect with a connection string | Done (this PR) | Masked field. The string lives in memory only and is cleared after connecting. Nothing is saved to disk. |
| Connect | Connect with Microsoft Entra ID | Done (this PR) | `DefaultAzureCredential` with a namespace host name. No secret is typed. |
| Connect | Saved connections list | Planned | Save the host name and sign-in mode only. Secrets go to the OS keychain, never to a settings file. |
| Connect | More than one broker family | Done (this PR), design | `IBrokerProvider` in `ME.Brokers.Abstractions`. Service Bus is enabled. Kafka and AWS SQS/SNS show as planned and disabled. |
| Layout | Entity tree on the left, details on the right | Planned | This PR has a Connect panel and a Queues grid. A tree of queues, topics, and subscriptions comes next. |
| Layout | Log pane | Planned | Structured, redacted event log. No message bodies in the log. |
| Navigation | Filter entities by name | Planned | Client-side filter over the inventory. |
| Queues | List queues | Done (this PR) | `GetQueuesRuntimePropertiesAsync` paged, joined with `GetQueuesAsync` for status. |
| Queues | Active, dead-letter, scheduled, transfer dead-letter counts | Done (this PR) | Sortable grid. Dead-letter counts above zero are highlighted. |
| Queues | Status and size | Done (this PR) | Status from the entity description, size from runtime properties. |
| Queues | Create, update, delete a queue | Out of scope for observe mode | Infrastructure belongs in Bicep or Terraform. A later admin action may file a change request, not run it. |
| Topics | List topics with scheduled count, size, subscription count | Done (adapter), Planned (UI) | `ListTopicsAsync` is built and tested. The grid view is planned. |
| Subscriptions | List subscriptions with counts | Done (adapter), Planned (UI) | `ListSubscriptionsAsync(topic)` is built and tested. |
| Rules | View subscription rules and filters | Planned | Read-only view through `GetRulesAsync`. Editing rules is out of scope for observe mode. |
| Messages | Peek messages | Planned (UI) | The skeleton peek client exists with a sequence cursor and a cap of 100. Bodies are redacted before display. |
| Messages | Receive and delete, receive and complete | Out of scope for observe mode | Settlement changes the broker. Admin module only, with approval. |
| Messages | Send a message | Planned (admin) | Admin module with approval, synthetic data, and an allowed-namespace list. |
| Dead letter | View dead-letter messages | Planned | Peek on the dead-letter subqueue. Show reason and description. |
| Dead letter | Resubmit dead-lettered messages | Planned (admin) | A resubmit is a send plus a settle. Admin module with approval and an audit row. |
| Sessions | Browse session state and session messages | Planned | Peek by session where the SDK allows it without taking a lock. Accepting a session takes a lock, so that stays out of observe mode. |
| Scheduled | View scheduled messages | Planned | Peek shows scheduled messages with their enqueue time. |
| Scheduled | Cancel a scheduled message | Out of scope for observe mode | It is a write. |
| Import/export | Export and import entity definitions | Planned (export only) | Export a read-only inventory to JSON or CSV. Import is infrastructure as code, out of scope. |
| Metrics | Charts from Azure Monitor | Planned | Read Azure Monitor metrics with the same Entra sign-in. |
| Event Hubs | Explore event hubs and partitions | Planned | The skeleton reader exists. A provider behind `IBrokerProvider` is planned. |
| Relays, notification hubs | Explore other Azure messaging services | Out of scope | Not part of the message-flow use case. |
| Testing | Send many test messages from a template | Planned | See [test jobs design](design/test-jobs.md). Synthetic stand-ins drive system tests through the provider interface. |
| Platform | Windows desktop app on .NET Framework | Improved | .NET 8 and Avalonia: Windows, macOS, and Linux from one code base. |
| Safety | Writes are always one click away | Improved | Observe mode by default. A compiled-IL test fails the build if a mutating Service Bus API becomes reachable from the inventory path. |
