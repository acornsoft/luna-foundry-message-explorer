# Live send: synthetic traffic to a real Service Bus

`me-jobs send` sends a job's synthetic messages to a real Azure Service Bus namespace so the explorer can
read real traffic. The explorer itself stays read-only. Sends live only in `ME.Operations`
(`ServiceBusLiveSink`); the job runner generates and paces.

## Safety

- **Dry run by default.** Nothing is sent unless you pass `--send`.
- **Entra only.** `DefaultAzureCredential` without the interactive browser (for example `az login`).
  There is no connection-string path. Plugins never see a credential; only the runner holds it.
- **Allow-list.** The namespace must be listed in a local file kept outside the repo,
  `<config>/lfme/live-namespaces.txt` (Linux: `~/.config/lfme/live-namespaces.txt`), one name per line,
  `#` for comments. A missing or empty list refuses every namespace. `--allow-list <file>` picks another file.
- **Only the job's entities.** Messages may go only to `target.send` and each generator's `send`; never to a
  dead-letter subqueue.
- **Hard cap.** `maxMessages` is 1 to 500; 500 is fixed in code and no job or flag can raise it. Over the cap the run stops.
- **Rate.** `rate.perMinute` (1 to 60) spaces sends evenly by the wall clock.
- **TTL.** Every message gets `ttl` (default `PT1H`).
- **Stop switch.** Ctrl+C, or create the stop file (default `<temp>/lfme-live.stop`, or `--stop-file`). The
  message in flight finishes; nothing after it goes.
- **Send only.** Nothing in this path receives, peeks, completes or settles.

## Run the sample

The sample (`samples/live/lfme-live`) sends about 40 orders (some poison: no lines) to `sbq-lfme-synth-dev-001`
and about 30 crew tickets (some poison: empty item code) to `sbt-lfme-synth-dev-001`, at 10 a minute.

```bash
dotnet build MessageExplorer.sln -c Release           # builds the order generator plugin too
az login                                              # your Entra account needs Azure Service Bus Data Sender on the queue and topic
mkdir -p ~/.config/lfme && echo "<namespace-name>" >> ~/.config/lfme/live-namespaces.txt
export LFME_LIVE_NAMESPACE=<namespace-name>.servicebus.windows.net
dotnet src/ME.Jobs.Cli/bin/Release/net8.0/me-jobs.dll send samples/live/lfme-live/jobs/synth-traffic.job.yaml          # dry run
dotnet src/ME.Jobs.Cli/bin/Release/net8.0/me-jobs.dll send samples/live/lfme-live/jobs/synth-traffic.job.yaml --send   # real send
```

The run prints what it sent (or would send) by entity and kind, for example
`sbq-lfme-synth-dev-001: 36 good, 4 poison`. Each message has `lfme.kind` (`good` or `poison`),
`lfme.generator` and `lfme.synthetic=true` application properties.

## Later: fan-out

One generator run feeding several sinks at once is not built. The seam allows it: messages are generated
once, then handed one by one to an `IMessageSink`; a composite sink can wrap several.

The sample sits in `samples/live`, not `samples/jobs`, so the desktop app's jobs panel and the app bundle do not
pick it up.
