# Move mode: drag-and-drop message moves

The app is read-only by default. Move mode is the one place it changes broker state: it moves messages
from one queue, subscription or dead-letter queue to another queue or topic, one message at a time,
oldest first, after you confirm each drop. It works the same for any namespace you can reach; the
provider decides what is possible, and anything a provider can't do is refused with a plain sentence.

## Guarantees

| Guarantee | How |
|---|---|
| Off unless you turn it on | `MoveModeSwitch` starts off on every launch. No setting, file, argument or environment variable turns it on; only the toggle at the top of the window. Turning it off cancels a move in progress between messages. A banner shows while it is on. |
| Only one assembly can mutate | Every send, receive, lock and settle call lives in `Acornsoft.MessageExplorer.Operations`. The Mono.Cecil guard (`MutationScanner`, unchanged) now also scans every other assembly built from `src/` and fails if any of them reaches a mutating Service Bus API. A positive control proves the scanner sees the calls inside Operations. |
| Rights first | Before anything is locked, the engine checks receive rights on the source and send rights on the target. On Service Bus both checks change nothing: a peek proves Listen (or Data Receiver), and opening a send link by creating an empty batch proves Send (or Data Sender). Missing either right refuses the drop. |
| You confirm every drop | The engine builds a `MovePlan` (count, source, target, transaction or at-least-once, ordering, warnings) and `ExecuteAsync` refuses a plan that wasn't confirmed. The dialog defaults to "Don't move"; Escape and closing also mean no. |
| Order | Messages are received under a peek-lock and moved in broker sequence order. A session-enabled source is moved one session at a time, each session in order. A dead-letter queue (never session-locked) is moved in sequence order, which keeps each session's relative order. |
| Same message | The copy keeps the body, message ID, session ID, correlation ID, subject, content type, To, ReplyTo, ReplyToSessionId, partition key, time to live and application properties, and gains `lfme-moved-from` = `<namespace>/<entity path>`. The broker-set `DeadLetterReason` and `DeadLetterErrorDescription` properties are dropped from the copy (they describe the old failure); the reason stays in the audit log. |
| No loss | Each message is sent, then completed. Within one namespace both run in one transaction (Service Bus cross-entity transaction, source as the via entity), so either both happen or neither does. |
| Duplicates across namespaces | Across namespaces there is no shared transaction, so the move is at-least-once. The plan says so and reads the target's duplicate detection: on, off, or unknown. If a complete fails after its send, the result names the message that may now exist in both places. |
| Stop on failure | The first failure stops the move. Every message still held (locked but not moved) is abandoned, so it is visible again at once. The result says how many moved. |
| Safe cancel | Cancel is checked between messages. A message whose send has started always finishes its complete, so nothing is left half-moved. |
| Audit | `audit/moves.jsonl` in the app's config folder, mode 0600 on macOS and Linux. One line for the start (actor, source, target, route, mode, count), one per moved message (sequence number, message ID, session ID) and one for the end (outcome, moved, released, possible duplicate, reason). The entry type has no field for a body, property, key, token or connection string. |

## Supported moves

| From | To | Notes |
|---|---|---|
| A queue's dead-letter queue | That queue | The classic resubmit. With duplicate detection on, a message whose original arrived inside the window is dropped by the broker; the plan warns. |
| A queue | Another queue | Same or another namespace. |
| A subscription | A queue | Same or another namespace. |
| A subscription's dead-letter queue | Its topic | Every subscription of the topic gets a copy, not only the one it came from; the plan warns. |

Anything else is refused: a dead-letter queue as a target, a topic as a source, a dead-letter queue to
some other entity, a queue onto itself. One drop moves 1 to 100 messages.

## Using it

1. Open a namespace (Accounts tab or Connect).
2. Turn on **Move mode** at the top right. The banner appears.
3. On the **Move** tab, pick a source in the tree. The list shows its messages: sequence number, message
   ID, session, enqueued time, deliveries and dead-letter reason. Bodies are never read.
4. Either pick messages (multi-select) and drag them onto a queue or topic in the tree, or drag the source
   node itself to move the oldest N. Allowed targets light up while you drag.
5. From the keyboard: choose a target in the list and press **Move to…** (Ctrl+M).
6. Confirm in the dialog. Progress shows; **Cancel move** stops between messages.

Picking messages that aren't the oldest means the ones ahead of them are locked while the move runs and
released afterwards (their delivery count goes up by one). The plan warns when that applies.

## Providers

`IMoveTransport` is the provider seam: rights checks, session detection, duplicate detection, peek
(metadata only), peek-lock receivers (plain and per session), senders, and a transaction wrapper.
`MoveCapabilities` says what a provider can do; the engine refuses plainly when a capability is missing.

| Provider | Move mode |
|---|---|
| Azure Service Bus | Built (`ServiceBusMoveTransport`). Entra accounts, namespace or entity keys, SAS tokens and the emulator. |
| Synthetic demo | Built (`InMemoryMoveBroker`), for screenshots and tests. |
| Apache Kafka | Refused: topics are append-only logs, a message can't be removed from a partition. |
| Azure Event Hubs | Refused: append-only log. |
| Azure Event Grid | Refused: it doesn't store messages. |
| RabbitMQ, AWS SQS and SNS | Refused for now ("isn't built yet"). |

## Tests

- `tests/ME.Operations.Tests` drives the engine against `InMemoryMoveBroker` and fakes: every route and
  refusal, rights, capabilities, cap of 100, picked batches, transactions and failed commits, a failed
  send mid-move, at-least-once with and without duplicate detection, sessions, the session-mode fallback,
  cancel, partial moves, the JSONL audit (no bodies or property values, 0600), and the Service Bus
  mapping and error kinds without a network.
- `OnlyOperationsMutatesTests` is the all-assemblies guard.
- `tests/ME.App.Tests/MoveModeTests` and `HeadlessMoveTests` cover the toggle and banner, off at launch,
  the tree and list, drag and drop through the view model, Move to…, the confirm dialog, refusals and
  refresh of the Queues grid after a move.
- `LiveMoveTests` is opt-in: set `LFME_LIVE_MOVE=1`, `LFME_LIVE_MOVE_CONNECTION` (in the environment
  only), `LFME_LIVE_MOVE_SOURCE` and `LFME_LIVE_MOVE_TARGET` (two empty queues in a throwaway namespace).
  CI doesn't set them, so it is skipped there.

## Limits

- The app opens one namespace at a time, so cross-namespace moves are built and tested in the engine but
  the UI offers only targets in the open namespace.
- Real pointer drag and drop is wired in the view; tests drive it through the view model and check the
  tree accepts drops. A recorded drag on a real desktop is still to do.
- Lock duration bounds how many messages a picked move can look through (the engine stops at 1,000).
- The live Service Bus test hasn't been run against a real namespace yet.
