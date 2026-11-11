// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Jobs.Hosting;
using Acornsoft.MessageExplorer.Jobs.Live;
using Acornsoft.MessageExplorer.Jobs.Loading;
using Acornsoft.MessageExplorer.Jobs.Model;
using Acornsoft.MessageExplorer.Jobs.Protocol;
using Acornsoft.MessageExplorer.Jobs.Running;

namespace Acornsoft.MessageExplorer.Jobs.Tests;

/// <summary>A wall clock that only moves when the sender waits. No real time passes in these tests.</summary>
internal sealed class FakeLiveClock : ILiveClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 11, 20, 0, 0, TimeSpan.Zero);

    public List<TimeSpan> Waits { get; } = [];

    public Action? OnWait { get; set; }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waits.Add(delay);
        UtcNow += delay;
        OnWait?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

/// <summary>Records what it is handed. Talks to nothing.</summary>
internal sealed class RecordingSink(FakeLiveClock? clock = null) : IMessageSink
{
    public List<(LiveMessage Message, TimeSpan TimeToLive, DateTimeOffset At)> Sent { get; } = [];

    public Action<int>? AfterSend { get; set; }

    public int? FailOn { get; set; }

    public string Name => "recording";

    public Task SendAsync(LiveMessage message, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        if (FailOn == Sent.Count + 1)
            throw new InvalidOperationException("The broker said no.\nSecond line with detail.");
        Sent.Add((message, timeToLive, clock?.UtcNow ?? default));
        AfterSend?.Invoke(Sent.Count);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class LiveSendTests
{
    private static LiveMessage Order(int i, int lines = 2, string entity = "sbq-lfme-synth-dev-001") =>
        LiveMessage.Create(entity, $"ord-{i:D4}", new JsonObject { ["orderRef"] = $"ord-{i:D4}", ["lines"] = lines }, "order-generator");

    private static List<LiveMessage> Orders(int count) => Enumerable.Range(1, count).Select(i => Order(i)).ToList();

    private static LiveSendOptions Live(int perMinute = 10, int max = 500) => new() { DryRun = false, PerMinute = perMinute, MaxMessages = max };

    // ---- Rate limiter (wall clock) ----

    [Fact]
    public async Task The_rate_limiter_spaces_sends_evenly_by_wall_clock()
    {
        var clock = new FakeLiveClock();
        var start = clock.UtcNow;
        var sink = new RecordingSink(clock);

        await new LiveSender(clock).SendAsync(Orders(4), sink, Live(perMinute: 10));

        Assert.Equal([0, 6, 12, 18], sink.Sent.Select(s => (s.At - start).TotalSeconds));
    }

    [Fact]
    public async Task The_rate_limiter_does_not_wait_again_for_time_already_spent()
    {
        var clock = new FakeLiveClock();
        var limiter = new RateLimiter(30, clock);
        await limiter.WaitTurnAsync(CancellationToken.None);
        await clock.DelayAsync(TimeSpan.FromSeconds(1.5), CancellationToken.None); // a slow send
        clock.Waits.Clear();
        await limiter.WaitTurnAsync(CancellationToken.None);
        Assert.Equal([TimeSpan.FromSeconds(0.5)], clock.Waits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(61)]
    public void Per_minute_must_be_between_1_and_60(int perMinute) =>
        Assert.Contains(new LiveSendOptions { PerMinute = perMinute }.Problems(), p => p.Contains("rate.perMinute", StringComparison.Ordinal));

    // ---- Hard cap ----

    [Fact]
    public void The_cap_defaults_to_500_and_config_cannot_raise_it()
    {
        Assert.Equal(500, new LiveSendOptions().MaxMessages);
        Assert.Equal(500, LiveSendOptions.HardCap);
        Assert.Contains(new LiveSendOptions { MaxMessages = 501 }.Problems(), p => p.Contains("maxMessages", StringComparison.Ordinal));
        Assert.Contains(new LiveSendOptions { MaxMessages = 0 }.Problems(), p => p.Contains("maxMessages", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cap_above_500_is_refused_before_anything_is_sent()
    {
        var sink = new RecordingSink();
        await Assert.ThrowsAsync<LiveSendException>(() => new LiveSender(new FakeLiveClock()).SendAsync(Orders(3), sink, Live(max: 10_000)));
        Assert.Empty(sink.Sent);
    }

    [Fact]
    public async Task Over_the_cap_the_run_stops()
    {
        var sink = new RecordingSink();
        var report = await new LiveSender(new FakeLiveClock()).SendAsync(Orders(7), sink, Live(perMinute: 60, max: 5));

        Assert.Equal(5, sink.Sent.Count);
        Assert.Equal((5, 2, LiveStopReason.CapReached), (report.Sent, report.NotSent, report.StopReason));
    }

    // ---- TTL ----

    [Fact]
    public async Task Every_message_gets_the_ttl_one_hour_by_default()
    {
        Assert.Equal(TimeSpan.FromHours(1), new LiveSendOptions().TimeToLive);
        var sink = new RecordingSink();
        await new LiveSender(new FakeLiveClock()).SendAsync(Orders(3), sink, Live());
        Assert.All(sink.Sent, s => Assert.Equal(TimeSpan.FromHours(1), s.TimeToLive));

        var shorter = new RecordingSink();
        await new LiveSender(new FakeLiveClock()).SendAsync(Orders(2), shorter, Live() with { TimeToLive = TimeSpan.FromMinutes(10) });
        Assert.All(shorter.Sent, s => Assert.Equal(TimeSpan.FromMinutes(10), s.TimeToLive));
    }

    [Fact]
    public void A_ttl_must_be_positive_and_at_most_one_day() =>
        Assert.Contains(new LiveSendOptions { TimeToLive = TimeSpan.Zero }.Problems(), p => p.Contains("ttl", StringComparison.Ordinal));

    // ---- Dry run ----

    [Fact]
    public async Task Dry_run_is_the_default_and_sends_nothing()
    {
        Assert.True(new LiveSendOptions().DryRun);
        var clock = new FakeLiveClock();
        var sink = new RecordingSink(clock);
        var messages = Orders(4);
        messages.Add(Order(5, lines: 0));

        var report = await new LiveSender(clock).SendAsync(messages, sink, new LiveSendOptions());

        Assert.Empty(sink.Sent);
        Assert.Empty(clock.Waits);
        Assert.True(report.DryRun);
        Assert.Equal((0, 5), (report.Sent, report.Planned));
        Assert.Equal(4, report.Count("sbq-lfme-synth-dev-001", MessageKind.Good));
        Assert.Equal(1, report.Count("sbq-lfme-synth-dev-001", MessageKind.Poison));
    }

    // ---- Broker failure ----

    [Fact]
    public async Task A_send_failure_stops_the_run_and_reports_what_was_sent()
    {
        var sink = new RecordingSink { FailOn = 3 };
        var log = new StringWriter();
        var report = await new LiveSender(new FakeLiveClock()).SendAsync(Orders(5), sink, Live(), log);

        Assert.Equal(2, sink.Sent.Count);
        Assert.Equal(LiveStopReason.Failed, report.StopReason);
        Assert.Equal(2, report.Counts.Values.Sum());
        var text = log.ToString();
        Assert.Contains("Send failed after 2: InvalidOperationException: The broker said no.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Second line", text, StringComparison.Ordinal);
        Assert.Contains("sent 2 of 5", text, StringComparison.Ordinal);
    }

    // ---- Stop switch ----

    [Fact]
    public async Task Cancelling_stops_the_run_between_messages()
    {
        using var stop = new CancellationTokenSource();
        var sink = new RecordingSink { AfterSend = n => { if (n == 2) stop.Cancel(); } };
        var report = await new LiveSender(new FakeLiveClock()).SendAsync(Orders(5), sink, Live(), cancellationToken: stop.Token);

        Assert.Equal(2, sink.Sent.Count);
        Assert.Equal(LiveStopReason.Stopped, report.StopReason);
    }

    [Fact]
    public async Task A_stop_file_stops_the_run()
    {
        var stopFile = Path.Combine(Path.GetTempPath(), $"lfme-stop-{Guid.NewGuid():N}");
        try
        {
            var sink = new RecordingSink { AfterSend = n => { if (n == 3) File.WriteAllText(stopFile, "stop"); } };
            var report = await new LiveSender(new FakeLiveClock()).SendAsync(Orders(5), sink, Live() with { StopFile = stopFile });

            Assert.Equal(3, sink.Sent.Count);
            Assert.Equal(LiveStopReason.StopFile, report.StopReason);
        }
        finally
        {
            File.Delete(stopFile);
        }
    }

    // ---- Counts by entity and kind; the fan-out seam ----

    [Fact]
    public async Task The_report_counts_what_was_sent_by_entity_and_kind()
    {
        var messages = new List<LiveMessage>
        {
            Order(1), Order(2, lines: 0),
            LiveMessage.Create("sbt-lfme-synth-dev-001", "tkt-0001", new JsonObject { ["ticketRef"] = "tkt-0001", ["itemCode"] = "PIPE-2IN" }, "crew-ticket-feed"),
            LiveMessage.Create("sbt-lfme-synth-dev-001", "tkt-0002", new JsonObject { ["ticketRef"] = "tkt-0002", ["itemCode"] = "" }, "crew-ticket-feed"),
        };
        var log = new StringWriter();
        var report = await new LiveSender(new FakeLiveClock()).SendAsync(messages, new RecordingSink(), Live(perMinute: 60), log);

        Assert.Equal(1, report.Count("sbq-lfme-synth-dev-001", MessageKind.Good));
        Assert.Equal(1, report.Count("sbq-lfme-synth-dev-001", MessageKind.Poison));
        Assert.Equal(1, report.Count("sbt-lfme-synth-dev-001", MessageKind.Good));
        Assert.Equal(1, report.Count("sbt-lfme-synth-dev-001", MessageKind.Poison));
        Assert.Contains("sbq-lfme-synth-dev-001: 1 good, 1 poison", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Messages_are_generated_once_and_the_same_instances_go_to_the_sink()
    {
        // The seam a future fan-out (one run, N sinks) needs: a composite sink can hand these same messages on.
        var messages = Orders(3);
        var sink = new RecordingSink();
        await new LiveSender(new FakeLiveClock()).SendAsync(messages, sink, Live(perMinute: 60));
        Assert.Equal(messages.Count, sink.Sent.Count);
        Assert.All(messages.Zip(sink.Sent), pair => Assert.Same(pair.First, pair.Second.Message));
    }

    [Theory]
    [InlineData("{\"lines\":0}", MessageKind.Poison)]
    [InlineData("{\"lines\":[]}", MessageKind.Poison)]
    [InlineData("{\"lines\":2}", MessageKind.Good)]
    [InlineData("{\"itemCode\":\"\"}", MessageKind.Poison)]
    [InlineData("{\"itemCode\":\"VALVE-1\"}", MessageKind.Good)]
    [InlineData("{\"hello\":\"world\"}", MessageKind.Good)]
    public void Poison_is_a_zero_line_order_or_a_ticket_with_no_item_code(string body, MessageKind kind) =>
        Assert.Equal(kind, PoisonRule.Classify(JsonNode.Parse(body)!.AsObject()));

    // ---- Namespace allow-list ----

    [Fact]
    public void Only_listed_namespaces_are_allowed()
    {
        var allow = new NamespaceAllowList(["sbns-demo-001"]);
        Assert.True(allow.IsAllowed("sbns-demo-001.servicebus.windows.net"));
        Assert.True(allow.IsAllowed("SBNS-DEMO-001"));
        Assert.False(allow.IsAllowed("sbns-demo-002.servicebus.windows.net"));
        Assert.False(allow.IsAllowed("sbns-demo-001.evil.example"));
        Assert.False(allow.IsAllowed("Endpoint=sb://sbns-demo-001.servicebus.windows.net/"));
        var refused = Assert.Throws<LiveSendException>(() => allow.Check("sbns-other-001.servicebus.windows.net"));
        Assert.Contains("not on the live allow-list", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_or_missing_allow_list_refuses_every_namespace()
    {
        Assert.False(new NamespaceAllowList([]).IsAllowed("sbns-demo-001.servicebus.windows.net"));
        var missing = NamespaceAllowList.Load(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.txt"));
        Assert.Empty(missing.Namespaces);
    }

    [Fact]
    public void The_allow_list_file_takes_one_namespace_per_line_with_comments()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lfme-allow-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, "# live namespaces\nsbns-demo-001\n\n  sbns-demo-003.servicebus.windows.net  # dev\n");
            Assert.Equal(["sbns-demo-001", "sbns-demo-003"], NamespaceAllowList.Load(path).Namespaces);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Generate once from the job's generators ----

    [Fact]
    public async Task Generation_runs_only_generators_once_over_the_simulated_clock_and_routes_per_plugin()
    {
        using var temp = new TempCollection();
        temp.Write("collection.yaml", "collection: live\nplugins:\n  orders: { version: \"1.0.0\", command: [\"x\"] }\n  tickets: { version: \"1.0.0\", command: [\"x\"] }\n  relay: { version: \"1.0.0\", command: [\"x\"] }\n");
        temp.Write("environments/live.yaml", "provider: azure-servicebus\nnamespace: sbns-demo-001.servicebus.windows.net\n");
        var jobPath = temp.Write("jobs/live.job.yaml", """
            job: live
            seed: 1
            clock: { start: "2026-03-02T09:00:00Z", step: PT1H, duration: PT3H }
            target: { send: sbq-lfme-synth-dev-001 }
            plugins:
              - use: orders@1.0.0
                role: generator
              - use: tickets@1.0.0
                role: generator
                send: sbt-lfme-synth-dev-001
              - use: relay@1.0.0
                role: simulator
            """);
        var job = JobLoader.Load(jobPath, "live", _ => null);
        var ticks = 0;
        var launcher = new FakeLauncher(new()
        {
            ["orders"] = () => new FakePlugin("orders", null, PluginRole.Generator) { OnTick = now => { ticks++; return [new OutboundMessage(null, $"ord-{now:HH}", new JsonObject { ["lines"] = now.Hour == 10 ? 0 : 1 }, 0)]; } },
            ["tickets"] = () => new FakePlugin("tickets", null, PluginRole.Generator) { OnTick = now => [new OutboundMessage(null, $"tkt-{now:HH}", new JsonObject { ["itemCode"] = "A" }, 0)] },
            ["relay"] = () => throw new InvalidOperationException("simulators are not started for a live send"),
        });

        var batch = await LiveBatch.GenerateAsync(job, launcher);

        Assert.Equal(4, ticks); // 09, 10, 11, 12: once each
        Assert.Equal(8, batch.Count);
        Assert.Equal(4, batch.Count(m => m.Entity == "sbq-lfme-synth-dev-001"));
        Assert.Equal(4, batch.Count(m => m.Entity == "sbt-lfme-synth-dev-001"));
        Assert.Equal(1, batch.Count(m => m.Kind == MessageKind.Poison));
        Assert.Equal(["ord-09", "tkt-09", "ord-10", "tkt-10"], batch.Take(4).Select(m => m.CorrelationId));
    }

    [Fact]
    public async Task Generation_refuses_a_dead_letter_queue_or_an_entity_the_job_does_not_name()
    {
        using var temp = new TempCollection();
        temp.Write("collection.yaml", "collection: live\nplugins:\n  orders: { version: \"1.0.0\", command: [\"x\"] }\n");
        temp.Write("environments/live.yaml", "provider: azure-servicebus\nnamespace: sbns-demo-001.servicebus.windows.net\n");
        var jobPath = temp.Write("jobs/live.job.yaml", "job: live\nseed: 1\nclock: { step: PT1H, duration: PT1H }\ntarget: { send: sbq-lfme-synth-dev-001 }\nplugins:\n  - use: orders@1.0.0\n    role: generator\n");
        var job = JobLoader.Load(jobPath, "live", _ => null);

        foreach (var to in new[] { "sbq-lfme-synth-dev-001/$deadletterqueue", "sbq-gsl-dev-001" })
        {
            var launcher = new FakeLauncher(new() { ["orders"] = () => new FakePlugin("orders", null, PluginRole.Generator) { OnTick = _ => [new OutboundMessage(to, "ord-1", new JsonObject(), 0)] } });
            var refused = await Assert.ThrowsAsync<LiveSendException>(() => LiveBatch.GenerateAsync(job, launcher));
            Assert.Contains(to, refused.Message, StringComparison.Ordinal);
        }
    }

    // ---- Loading a live job ----

    [Fact]
    public void A_live_environment_loads_its_namespace_and_the_job_its_rate_cap_and_ttl()
    {
        using var temp = new TempCollection();
        temp.Write("collection.yaml", "collection: live\nplugins:\n  orders: { version: \"1.0.0\", command: [\"x\"] }\n");
        temp.Write("environments/live.yaml", "provider: azure-servicebus\nnamespace: \"{{ env.LFME_LIVE_NAMESPACE }}\"\n");
        var jobPath = temp.Write("jobs/live.job.yaml", "job: live\nseed: 1\ntarget: { send: q }\nrate: { perMinute: 10 }\nmaxMessages: 120\nttl: PT30M\nplugins:\n  - use: orders@1.0.0\n    role: generator\n");

        var job = JobLoader.Load(jobPath, "live", name => name == "LFME_LIVE_NAMESPACE" ? "sbns-demo-001.servicebus.windows.net" : null);

        Assert.Equal(JobLoader.AzureServiceBusProvider, job.Provider);
        Assert.Equal("sbns-demo-001.servicebus.windows.net", job.Namespace);
        Assert.Equal(new LiveSendOptions { PerMinute = 10, MaxMessages = 120, TimeToLive = TimeSpan.FromMinutes(30) }, job.Live);
        Assert.True(job.Live!.DryRun);
    }

    [Fact]
    public void A_live_job_over_the_hard_cap_or_without_a_namespace_does_not_load()
    {
        using var temp = new TempCollection();
        temp.Write("collection.yaml", "collection: live\nplugins:\n  orders: { version: \"1.0.0\", command: [\"x\"] }\n");
        temp.Write("environments/live.yaml", "provider: azure-servicebus\n");
        var jobPath = temp.Write("jobs/live.job.yaml", "job: live\nseed: 1\ntarget: { send: q }\nmaxMessages: 5000\nplugins:\n  - use: orders@1.0.0\n    role: generator\n");

        var error = Assert.Throws<JobValidationException>(() => JobLoader.Load(jobPath, "live", _ => null));
        Assert.Contains("namespace", error.Message, StringComparison.Ordinal);
        Assert.Contains("maxMessages", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_simulated_runner_will_not_run_a_live_job()
    {
        using var temp = new TempCollection();
        temp.Write("collection.yaml", "collection: live\nplugins:\n  orders: { version: \"1.0.0\", command: [\"x\"] }\n");
        temp.Write("environments/live.yaml", "provider: azure-servicebus\nnamespace: sbns-demo-001\n");
        var jobPath = temp.Write("jobs/live.job.yaml", "job: live\nseed: 1\ntarget: { send: q }\nplugins:\n  - use: orders@1.0.0\n    role: generator\n");
        var job = JobLoader.Load(jobPath, "live", _ => null);

        var refused = await Assert.ThrowsAsync<JobRunException>(() => new JobRunner().RunAsync(job));
        Assert.Contains("me-jobs send", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_providers_are_still_refused()
    {
        using var temp = new TempCollection();
        temp.Write("collection.yaml", "collection: live\nplugins:\n  orders: { version: \"1.0.0\", command: [\"x\"] }\n");
        temp.Write("environments/kafka.yaml", "provider: kafka\n");
        var jobPath = temp.Write("jobs/live.job.yaml", "job: live\nseed: 1\ntarget: { send: q }\nplugins:\n  - use: orders@1.0.0\n    role: generator\n");
        Assert.Throws<JobValidationException>(() => JobLoader.Load(jobPath, "kafka", _ => null));
    }
}

public sealed class LiveSendCliTests
{
    private static string LiveJob => Repo.Path("samples", "live", "lfme-live", "jobs", "synth-traffic.job.yaml");

    private static Func<string, string?> Env(string ns) => name => name == "LFME_LIVE_NAMESPACE" ? ns : null;

    private static string AllowList(params string[] names)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lfme-allow-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, names);
        return path;
    }

    [Fact]
    public async Task Send_refuses_a_namespace_off_the_allow_list_and_opens_no_sink()
    {
        var allow = AllowList("sbns-demo-001");
        var opened = 0;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Cli.JobsCli.RunAsync(
            ["send", LiveJob, "--send", "--allow-list", allow, "--stop-file", Path.Combine(Path.GetTempPath(), $"stop-{Guid.NewGuid():N}")],
            stdout, stderr, Env("sbns-other-001.servicebus.windows.net"), (_, _) => { opened++; return new RecordingSink(); }, CancellationToken.None);

        Assert.Equal(Cli.JobsCli.SetupError, code);
        Assert.Equal(0, opened);
        Assert.Contains("not on the live allow-list", stderr.ToString(), StringComparison.Ordinal);
        File.Delete(allow);
    }

    [Fact]
    public async Task Send_refuses_an_in_memory_job()
    {
        var stderr = new StringWriter();
        var code = await Cli.JobsCli.RunAsync(["send", Repo.ErpJob], new StringWriter(), stderr, _ => null, (_, _) => new RecordingSink(), CancellationToken.None);
        Assert.Equal(Cli.JobsCli.SetupError, code);
        Assert.Contains("azure-servicebus", stderr.ToString(), StringComparison.Ordinal);
    }

    [PythonFact]
    public async Task The_sample_live_job_is_a_dry_run_by_default_and_sends_nothing()
    {
        var allow = AllowList("sbns-demo-001");
        var sink = new RecordingSink();
        var stdout = new StringWriter();
        var code = await Cli.JobsCli.RunAsync(
            ["send", LiveJob, "--allow-list", allow, "--stop-file", Path.Combine(Path.GetTempPath(), $"stop-{Guid.NewGuid():N}")],
            stdout, new StringWriter(), Env("sbns-demo-001.servicebus.windows.net"), (_, _) => sink, CancellationToken.None);

        Assert.Equal(Cli.JobsCli.Ok, code);
        Assert.Empty(sink.Sent);
        var text = stdout.ToString();
        Assert.Contains("70 generated", text, StringComparison.Ordinal);
        Assert.Contains("DRY RUN: would send 70 of 70", text, StringComparison.Ordinal);
        Assert.Matches(@"sbq-lfme-synth-dev-001: \d+ good, [1-9]\d* poison", text);
        Assert.Matches(@"sbt-lfme-synth-dev-001: \d+ good, [1-9]\d* poison", text);
        File.Delete(allow);
    }

    [PythonFact]
    public async Task A_broker_failure_during_send_exits_with_a_setup_error()
    {
        var allow = AllowList("sbns-demo-001");
        var sink = new RecordingSink { FailOn = 1 };
        var stdout = new StringWriter();
        var code = await Cli.JobsCli.RunAsync(
            ["send", LiveJob, "--send", "--allow-list", allow, "--stop-file", Path.Combine(Path.GetTempPath(), $"stop-{Guid.NewGuid():N}")],
            stdout, new StringWriter(), Env("sbns-demo-001.servicebus.windows.net"), (_, _) => sink, CancellationToken.None);

        Assert.Equal(Cli.JobsCli.SetupError, code);
        Assert.Empty(sink.Sent);
        Assert.Contains("(Failed)", stdout.ToString(), StringComparison.Ordinal);
        File.Delete(allow);
    }

    [PythonFact]
    public async Task With_send_the_sample_job_hands_every_message_to_the_sink_with_ttl_and_pacing_limits()
    {
        var allow = AllowList("sbns-demo-001");
        var sink = new RecordingSink();
        using var stop = new CancellationTokenSource();
        sink.AfterSend = n => { if (n == 1) stop.Cancel(); }; // the stop switch, so the test waits for no real pacing
        var stdout = new StringWriter();
        var code = await Cli.JobsCli.RunAsync(
            ["send", LiveJob, "--send", "--allow-list", allow, "--stop-file", Path.Combine(Path.GetTempPath(), $"stop-{Guid.NewGuid():N}")],
            stdout, new StringWriter(), Env("sbns-demo-001.servicebus.windows.net"), (_, _) => sink, stop.Token);

        Assert.Equal(Cli.JobsCli.Ok, code);
        var only = Assert.Single(sink.Sent);
        Assert.Equal(TimeSpan.FromHours(1), only.TimeToLive);
        Assert.Contains("(Stopped)", stdout.ToString(), StringComparison.Ordinal);
        File.Delete(allow);
    }
}
