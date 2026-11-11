# Resources (the quick view)

The **Resources** view is the app's main surface. It shows the counts and top messages of any resource or
receiver, by message type, without changing anything. (Before 0.8.2 there were separate Queues and Topics grid
tabs; 0.8.2 merged them into a Queues & topics tab; 0.8.3 removes the tabs. `--tab resources`, `--tab queues`,
`--tab topics` and `--tab quick` all open it.) The model behind it is in [Resources and receivers](resource-model.md).

## The top bar

One slim bar (48 px) replaces 0.8.2's observe banner, tab row and namespace / read-only / Top N bar:

| Left | Middle | Right |
| --- | --- | --- |
| **Namespace switcher**: the connected namespace, with the account and cloud under it (for example "alice@contoso.example · Azure public"), and a chevron. Opens a quick switch: Recent and Saved (two-line rows, the namespace on top and the account and cloud under it, with a check on the namespace open now), and **Manage accounts…**, which opens the full sheet with Connect (Recent, Saved, connection details), Accounts and Saved logins, in the style of Azure Data Studio's connection dialog. | **Search** (⌘F on macOS, Ctrl+F elsewhere; the hint follows the platform): one box that finds any resource or receiver. A match on a receiver shows its topic, opened. Esc clears it. | **Mode chip**: "Read-only" (green, lock) or "Move" (amber). Its tooltip is one phrase: "Read-only: peeks, never changes messages". Then the **Voice** mic (a side panel) and the **Settings** gear (appearance, Top N, Test jobs…). Every icon-only button has a name, a tooltip and a hit area of at least 24 px. |

Esc closes the topmost sheet or panel. The switcher is open at launch and closes once a namespace connects.
It opens on the page with the next action, worked out from the data: **Connect › Recent** when there are
recent connections; **Connect › Saved** when there are saved logins but nothing recent; **Accounts** with focus
on **Add an account** on first run; **Accounts** with **Browse** in view when there are accounts but nothing
saved. A page you pick stays picked.

While Move mode is on, the amber banner names what it applies to: "Move mode is on for <namespace>
(<account>, <cloud>)". (The mode chip itself is unchanged in 0.8.4; where Move lives is being redesigned.)

## Layout

- **Kind filter** (top of the list): one segmented control, **All**, **Queues** or **Topics**, with counts.
  The choice is remembered between launches in `ui-settings.json`. If the selected resource or receiver is
  hidden, the selection is cleared. **Has dead letters** (a toggle under it, this session only) keeps only
  resources and receivers with dead-lettered or transfer dead-lettered messages.
- **Match line**: there is no summary line in the normal state. While the kind filter, Has dead letters or
  the search narrows the list, one line says how many show: the search is quoted after "match" and filters
  follow a colon ("6 of 9: Queues", "1 of 9 match 'audit'", "2 of 9: Queues, Has dead letters"), with a
  **Clear** link that resets all three.
- **Pinned selection**: when the search or Has dead letters would hide the selected row, it stays in the list,
  in its place in the tree, dimmed (muted italic text, contrast kept), with a small pin whose tooltip and
  spoken name are "Selected, doesn't match". It isn't counted in the match line.
- **Resources list** (left): one list of resources, sorted by name. Under **All**, topics and subscriptions
  carry a small kind badge; queues rely on their icon, whose tooltip and accessible name say "Queue". Under
  **Queues** or **Topics** no row has a badge, because the filter already says the kind:

  | Kind | Icon | Tag | Colour |
  | --- | --- | --- | --- |
  | Queue (channel): its own single receiver | `fa-inbox` | QUEUE | teal |
  | Topic | `fa-tower-broadcast` | TOPIC | purple |
  | Subscription receiver (under its topic) | `fa-code-branch` | SUB | purple |

  A queue has nothing to expand: picking it opens its messages. A topic expands to its receivers, read the
  first time you open it. The scheduled count stays on the topic (messages wait there until they are due);
  dead letters are counted on each receiver. The same icon and tag show in the entity header. The arrow keys
  walk the list; Right opens a topic. A row shows at most two count badges: active, and dead-lettered when
  above 0 (scheduled and transfer counts are on the cards). Each badge has a tooltip and spoken name ("12
  active", "3 dead-lettered") and its text meets 4.5:1 in both themes. A topic has no active count of its
  own, so its row shows only a dead-letter badge, if any. Zero badges are hidden.
- **Lane cards** (top): one card per message type with its count, in one fixed order for every resource:
  Active, Dead-letter, Scheduled, Transfer DL. A zero card shows 0 like the others, captioned "Nothing to
  peek". Click a card to open that section and jump to it. Dead-letter cards turn red when the count is above
  zero. When the main pane is narrower than 720 px (for example with Voice open), the cards wrap to 2 by 2.
- **Sections** (middle): Active, Dead-letter, Scheduled and Transfer dead-letter, in the cards' order. The
  count shows once, on the card; the section headers have no count chip. Each one opens as a
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
