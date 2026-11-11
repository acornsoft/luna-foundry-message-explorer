# Resources (the quick view)

The **Resources** view is the app's main surface. It shows the counts and top messages of any resource or
receiver, by message type, without changing anything. (Before 0.8.2 there were separate Queues and Topics grid
tabs; 0.8.2 merged them into a Queues & topics tab; 0.8.3 removes the tabs. `--tab resources`, `--tab queues`,
`--tab topics` and `--tab quick` all open it.) The model behind it is in [Resources and receivers](resource-model.md).

## The top bar

One slim bar (48 px) replaces 0.8.2's observe banner, tab row and namespace / read-only / Top N bar:

| Left | Middle | Right |
| --- | --- | --- |
| **Namespace switcher**: the connected namespace and a chevron. Opens a sheet with Connect (Recent, Saved, connection details), Accounts and Saved logins, in the style of Azure Data Studio's connection dialog. | **Search** (Ctrl+F, or Cmd+F on a Mac): one box that finds any resource or receiver. A match on a receiver shows its topic, opened. Esc clears it. | **Mode chip**: "Read-only" (green, lock) or "Move" (amber). Its tooltip is the one place the read-only rules are explained. Then the **Voice** mic (a side panel) and the **Settings** gear (theme, Top N, Test jobs…). |

Esc closes the topmost sheet or panel. The switcher is open at launch and closes once a namespace connects.

## Layout

- **Kind filter** (top of the list): one segmented control, **All**, **Queues** or **Topics**, with counts.
  The choice is remembered between launches in `ui-settings.json`. If the selected resource or receiver is
  hidden, the selection is cleared.
- **Resources list** (left): one list of resources, sorted by name. Each row has a small kind badge:

  | Kind | Icon | Tag | Colour |
  | --- | --- | --- | --- |
  | Queue (channel): its own single receiver | `fa-inbox` | QUEUE | teal |
  | Topic | `fa-tower-broadcast` | TOPIC | purple |
  | Subscription receiver (under its topic) | `fa-code-branch` | SUB | purple |

  A queue has nothing to expand: picking it opens its messages. A topic expands to its receivers, read the
  first time you open it. The scheduled count stays on the topic (messages wait there until they are due);
  dead letters are counted on each receiver. The same icon and tag show in the entity header. The arrow keys
  walk the list; Right opens a topic. A summary line counts resources, queues with dead letters, and topics.
- **Lane cards** (top): one card per message type with its count. Click a card to open that section and jump
  to it. Dead-letter cards turn red when the count is above zero.
- **Sections** (middle): Active, Dead-letter, Scheduled and Transfer dead-letter. Each one opens as a
  virtualized list of rows: sequence number, message ID, enqueued time (UTC), subject, a masked body summary,
  size and the dead-letter reason. Icons on each row open it in the drawer or copy its redacted text.
- **Detail drawer** (right): system properties, application property names with masked values, and the body
  with every value masked.

### Icons and labels

Font Awesome icons sit on the vertical middle of their labels: the lane cards, section headers, message rows,
tree nodes, the drawer, load more and the top bar, the switcher's rail and the Voice panel's push-to-talk button. One app-wide style does it: put
the icon and its TextBlock in a panel with class `il` and both are centered on the row, with no baseline nudges.
`HeadlessIconAlignmentTests` checks that every icon's center is within 1px of its text's center.

## What it reads

| What | From |
| --- | --- |
| Counts | Runtime properties (one list of queues and one of topics per load; subscriptions per topic when expanded). |
| Active and Scheduled rows (queue) | A peek of the entity. One peek returns active, deferred and scheduled messages; they are split by state. |
| Dead-letter rows | A peek of `<entity>/$DeadLetterQueue`. |
| Transfer dead-letter rows | A peek of `<entity>/$Transfer/$DeadLetterQueue`. |
| Scheduled (subscription) | The topic's Scheduled count, count only. A subscription peek doesn't return scheduled messages; they wait on the topic until they're due. The section says so. |
| Scheduled (topic) | Count only. A topic can't be peeked. |

- **Top N.** Each section peeks N messages per page (25 by default, 1 to 100, set under **Settings**).
- **Cursors.** There is one sequence cursor per sub-queue (main, dead-letter, transfer dead-letter). "Peek
  next N from #seq" continues from it. Active and Scheduled share the main cursor; scheduled messages met
  while filling Active are kept for the Scheduled section. Refreshing a section starts its sub-queue again.
- **Short batches.** A peek can return fewer messages than asked even when more are waiting, so a page keeps
  peeking until it has N, the sub-queue comes back empty, or 20 calls are used (then "more waiting" stays on).
- **One read on connect.** Connecting lists queues once and topics once (no second grid loads them again).
  Receivers are read when a topic is opened or a search needs them. A section peeks only when it is opened.

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
demo provide one today. A provider without one shows every section as count only.

## Performance

Lists use a `VirtualizingStackPanel` (a headless test checks that 100 rows realize only a screenful), sections
load lazily, and a page is at most 100 messages. No other measured numbers yet.
