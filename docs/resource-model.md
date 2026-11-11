# Resources and receivers

Luna Foundry Message Explorer works on **resources**, not on "queues" and "topics" as separate worlds. The
types are in `src/ME.Brokers.Abstractions/Resources.cs`. They have no broker types in them.

| Type | What it is | Kinds |
| --- | --- | --- |
| `Resource` | What you send to. | `Queue`, `Topic`; later `EventHub`, `KafkaTopic` |
| `Receiver` | What you read from. | `Implicit` (a queue is its own single receiver), `Subscription`; later `ConsumerGroup` |

A queue (channel) behaves like a topic with exactly one subscription: it has one implicit receiver, which is
the queue itself, so picking a queue opens its messages straight away. A topic fans out to N receivers
(subscriptions today; consumer groups for Event Hubs and Kafka later), and you open the topic to pick one.

Why keep the two kinds at all, then? Brokers keep them apart for real reasons, and the app shows them where it
matters:

- **Where messages wait.** A queue holds its own messages. A topic holds nothing you can peek; each receiver
  holds its own copy. Scheduled messages wait on the topic until they are due, so the scheduled count is shown
  on the topic, and dead letters are counted on each receiver.
- **Competing consumers vs fan-out.** Readers of one queue share its messages; each subscription gets every
  message (after its filter). That is the design choice a person makes when they pick one or the other.
- **Rights and cost.** Some brokers price and secure the two differently (for example, Service Bus topics need
  the Standard tier).

## How the app reads them

- `ResourceReader.ReadAsync` lists queues once and topics once and returns one list sorted by name. That is
  the only list read on connect.
- `ResourceReader.ReceiversAsync` returns a queue's implicit receiver without any read, and reads a topic's
  subscriptions when asked (when the topic is opened, or when a search needs receiver names).
- `ResourceKindFilter` (All, Queues, Topics) is the one filter, remembered in `ui-settings.json`.
  "Queues" means resources with an implicit receiver, so a future kind that behaves like a queue joins it
  without a new switch.

All of this is read-only. Moving messages stays in the Operations assembly, behind Move mode.
