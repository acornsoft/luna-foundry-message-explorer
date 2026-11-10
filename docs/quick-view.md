# Quick view

The Quick view tab shows the top messages of any queue, topic or subscription, by message type, without
changing anything.

## Layout

- **Entity tree** (left): the namespace, then Queues and Topics. A topic's subscriptions are read the first
  time you expand it. The find box hides entities whose names don't match. Badges show the active,
  dead-letter, scheduled and transfer dead-letter counts.
- **Lane cards** (top): one card per message type with its count. Click a card to open that section and jump
  to it. Dead-letter cards turn red when the count is above zero.
- **Sections** (middle): Active, Dead-letter, Scheduled and Transfer dead-letter. Each one opens as a
  virtualized list of rows: sequence number, message ID, enqueued time (UTC), subject, a masked body summary,
  size and the dead-letter reason. Icons on each row open it in the drawer or copy its redacted text.
- **Detail drawer** (right): system properties, application property names with masked values, and the body
  with every value masked.

## What it reads

| What | From |
| --- | --- |
| Counts | Runtime properties, the same read as the Queues and Topics tabs. |
| Active and Scheduled rows (queue) | A peek of the entity. One peek returns active, deferred and scheduled messages; they are split by state. |
| Dead-letter rows | A peek of `<entity>/$DeadLetterQueue`. |
| Transfer dead-letter rows | A peek of `<entity>/$Transfer/$DeadLetterQueue`. |
| Scheduled (subscription) | The topic's Scheduled count, count only. A subscription peek doesn't return scheduled messages; they wait on the topic until they're due. The section says so. |
| Scheduled (topic) | Count only. A topic can't be peeked. |

- **Top N.** Each section peeks N messages per page (25 by default, 1 to 100, set in the top bar).
- **Cursors.** There is one sequence cursor per sub-queue (main, dead-letter, transfer dead-letter). "Peek
  next N from #seq" continues from it. Active and Scheduled share the main cursor; scheduled messages met
  while filling Active are kept for the Scheduled section. Refreshing a section starts its sub-queue again.
- **Short batches.** A peek can return fewer messages than asked even when more are waiting, so a page keeps
  peeking until it has N, the sub-queue comes back empty, or 20 calls are used (then "more waiting" stays on).
- **Lazy.** Nothing is read until the tab is first shown. A section peeks only when it is opened.

## Safety

- **Peek only.** The browser contract (`IMessageBrowser`) has `CanPeek` and `PeekAsync` and nothing else. A
  peek takes no lock: nothing is received, renewed, deferred, completed, abandoned or dead-lettered.
- **Where the Service Bus code lives.** `ServiceBusMessageBrowser` is in the Operations assembly, because every
  other assembly must not reference a Service Bus receiver at all (the all-assemblies guard). Its own IL test
  allows `CreateReceiver`, `PeekMessagesAsync`, `DisposeAsync` and option setters, and fails on anything else.
- **Redacted by default.** Every frame goes through the `quickview-default` redaction profile before it is
  kept. Metadata (subject, message, correlation and session IDs, content type, dead-letter reason and
  description) is kept and scrubbed by the pattern net and by every dropped value. Body fields and application
  property values are dropped: only their names are shown. The redaction property harness runs through the
  quick view in `QuickViewRedactionTests`. The token key and salt are random per window.
- **The clipboard** gets only the redacted text.

## Any provider

The quick view talks to `IMessageBrowser`, which has no broker types in it. Azure Service Bus and the synthetic
demo provide one today. A provider without one shows every section as count only, and the top bar says
"Read-only · counts only".

## Performance

Lists use a `VirtualizingStackPanel` (a headless test checks that 100 rows realize only a screenful), sections
load lazily, and a page is at most 100 messages. No other measured numbers yet.
