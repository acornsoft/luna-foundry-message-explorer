// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Jobs.Cli;
using Acornsoft.MessageExplorer.Jobs.Loading;
using Acornsoft.MessageExplorer.Jobs.Model;
using Acornsoft.MessageExplorer.Jobs.Protocol;
using Acornsoft.MessageExplorer.Jobs.Reporting;
using Acornsoft.MessageExplorer.Jobs.Running;
using Acornsoft.MessageExplorer.Shell.ViewModels;

namespace Acornsoft.MessageExplorer.Shell.Tests;

public sealed class JobsViewModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 2, 13, 0, 0, TimeSpan.Zero);

    private static JobsViewModel NewPanel(IUiScheduler? scheduler = null, IJobsDialogs? dialogs = null) =>
        new(scheduler ?? InlineUiScheduler.Instance, dialogs, environmentVariables: _ => null);

    [Fact]
    public void Starts_with_no_collection_and_run_disabled()
    {
        var panel = NewPanel();

        Assert.Equal(JobPanelState.NoCollection, panel.State);
        Assert.False(panel.RunCommand.CanExecute(null));
        Assert.False(panel.CancelCommand.CanExecute(null));
        Assert.Contains("in-memory broker only", panel.SafetyText, StringComparison.Ordinal);
    }

    [Fact]
    public void Opening_a_collection_lists_jobs_environments_and_pinned_plugins()
    {
        var panel = NewPanel();
        panel.OpenCollection(Repo.CrewStock);

        Assert.Equal(JobPanelState.Ready, panel.State);
        Assert.Equal(["shift-sync"], panel.Jobs.Select(j => j.Name));
        Assert.Equal(["local"], panel.Environments);
        Assert.Equal("local", panel.SelectedEnvironment);
        Assert.Equal("515151", panel.SeedText);
        Assert.Equal("crew.ticket-changes on in-memory", panel.TargetText);
        Assert.Equal(
            ["crew-ticket-feed@0.1.0 · generator, assertion", "hop-relay@0.1.0 · simulator", "bus-observer@1.0.0 · observer · built in"],
            panel.Plugins);
        Assert.True(panel.RunCommand.CanExecute(null));
        Assert.Contains("crew-stock", panel.CollectionText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_without_collection_yaml_says_what_to_pick()
    {
        using var temp = new TempFolder();
        var panel = NewPanel();
        panel.OpenCollection(temp.Root);

        Assert.Equal(JobPanelState.NoCollection, panel.State);
        Assert.Contains("collection.yaml", panel.ValidationText, StringComparison.Ordinal);
        Assert.Empty(panel.Jobs);
        Assert.False(panel.RunCommand.CanExecute(null));
    }

    [Fact]
    public void An_invalid_job_shows_every_problem_and_cannot_run()
    {
        using var temp = new TempFolder();
        temp.Write("collection.yaml", "collection: c\nplugins:\n  gen: { version: \"1.0.0\", command: [python3] }\n");
        temp.Write("environments/local.yaml", "provider: in-memory\n");
        temp.Write("jobs/bad.job.yaml", "job: bad\nseed: 1\ntarget: { send: q }\nplugins:\n  - { use: gen@2.0.0, role: generator }\n");
        var panel = NewPanel();
        panel.OpenCollection(temp.Root);

        Assert.Equal(JobPanelState.Invalid, panel.State);
        Assert.Contains("gen@2.0.0", panel.ValidationText, StringComparison.Ordinal);
        Assert.False(panel.RunCommand.CanExecute(null));
    }

    [Fact]
    public void A_real_broker_environment_is_refused()
    {
        using var temp = new TempFolder();
        temp.Write("collection.yaml", "collection: c\nplugins:\n  gen: { version: \"1.0.0\", command: [python3] }\n");
        temp.Write("environments/local.yaml", "provider: in-memory\n");
        temp.Write("environments/prod.yaml", "provider: azure-service-bus\n");
        temp.Write("jobs/ok.job.yaml", "job: ok\nseed: 1\ntarget: { send: q }\nplugins:\n  - { use: gen@1.0.0, role: generator }\n");
        var panel = NewPanel();
        panel.OpenCollection(temp.Root);
        Assert.Equal(JobPanelState.Ready, panel.State);

        panel.SelectedEnvironment = "prod";

        Assert.Equal(JobPanelState.Invalid, panel.State);
        Assert.Contains("in-memory", panel.ValidationText, StringComparison.Ordinal);
        Assert.False(panel.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task Browse_opens_the_folder_the_dialog_returns()
    {
        var panel = NewPanel(dialogs: new FakeDialogs(Repo.ErpStandin, null));
        Assert.True(panel.BrowseCommand.CanExecute(null));

        await panel.BrowseCommand.ExecuteAsync();

        Assert.Equal(["erp-day"], panel.Jobs.Select(j => j.Name));
        Assert.Equal(JobPanelState.Ready, panel.State);
    }

    [PythonFact]
    public async Task Crew_stock_through_the_panel_matches_the_cli_byte_for_byte()
    {
        var panel = NewPanel();
        panel.OpenCollection(Repo.CrewStock);
        await panel.RunAsync();

        Assert.Equal(JobPanelState.Passed, panel.State);
        var live = panel.Live;
        Assert.Equal(70, live.SentCount);
        Assert.Equal(65, live.PassedCount);
        Assert.Equal(5, live.DeadLetteredCount);
        Assert.Equal(0, live.FailedCount + live.PendingCount + live.LateCount);
        Assert.Equal(125, live.Throttled);
        Assert.Equal(26, live.Retries);
        Assert.Equal(5, live.DeadLetterEvents);
        Assert.Equal(4, live.Duplicates);
        Assert.StartsWith("Passed: crew-stock.shift-sync.seed-515151. 70 of 70 passed", live.Summary, StringComparison.Ordinal);

        using var temp = new TempFolder();
        var cliPath = Path.Combine(temp.Root, "cli.xml");
        var panelPath = Path.Combine(temp.Root, "panel.xml");
        Assert.Equal(JobsCli.Ok, await JobsCli.RunAsync(["run", Repo.CrewStockJob, "--junit", cliPath], new StringWriter(), new StringWriter(), _ => null));
        await live.ExportJUnitAsync(panelPath);
        Assert.Equal(File.ReadAllBytes(cliPath), File.ReadAllBytes(panelPath));
    }

    [Fact]
    public async Task Erp_sample_run_shows_credit_holds_and_dead_letters_then_exports_through_the_dialog()
    {
        using var temp = new TempFolder();
        var target = Path.Combine(temp.Root, "out", "erp.xml");
        var dialogs = new FakeDialogs(null, target);
        var panel = NewPanel(dialogs: dialogs);
        panel.OpenCollection(Repo.ErpStandin);
        Assert.False(panel.Live.ExportJUnitCommand.CanExecute(null));

        await panel.RunAsync();

        Assert.Equal(JobPanelState.Passed, panel.State);
        Assert.Equal(55, panel.Live.SentCount);
        Assert.Equal(3, panel.Live.DeadLetteredCount);
        Assert.Contains(panel.Live.AllRows, r => r.Reason.Contains("CREDIT_HOLD", StringComparison.Ordinal));
        Assert.True(panel.Live.ExportJUnitCommand.CanExecute(null));

        await panel.Live.ExportJUnitCommand.ExecuteAsync();

        Assert.Equal("erp-standin.erp-day.seed-424242.junit.xml", dialogs.SuggestedName);
        Assert.Equal(target, panel.Live.ExportedTo);
        Assert.Equal(JUnitWriter.Write(panel.Live.LastResult!), File.ReadAllText(target));
    }

    [Fact]
    public async Task Cancel_stops_a_paced_run_and_the_panel_can_run_again()
    {
        var panel = NewPanel();
        panel.OpenCollection(Repo.ErpStandin);
        panel.SelectedPace = panel.Paces.Single(p => p.SimulatedSecondsPerSecond == 60);

        var run = panel.RunAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (panel.Live.StepCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.Equal(JobPanelState.Running, panel.State);
        Assert.True(panel.CancelCommand.CanExecute(null));
        Assert.False(panel.RunCommand.CanExecute(null));

        panel.Cancel();
        await run;

        Assert.Equal(JobPanelState.Cancelled, panel.State);
        Assert.False(panel.Live.IsRunning);
        Assert.True(panel.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_plugin_that_cannot_start_stops_the_run_with_a_message()
    {
        using var temp = new TempFolder();
        temp.Write("collection.yaml", "collection: c\nplugins:\n  gen: { version: \"1.0.0\", command: [lfme-no-such-plugin] }\n");
        temp.Write("environments/local.yaml", "provider: in-memory\n");
        temp.Write("jobs/x.job.yaml", "job: x\nseed: 1\ntarget: { send: q }\nplugins:\n  - { use: gen@1.0.0, role: generator }\n");
        var panel = NewPanel();
        panel.OpenCollection(temp.Root);

        await panel.RunAsync();

        Assert.Equal(JobPanelState.Error, panel.State);
        Assert.Contains("lfme-no-such-plugin", panel.StatusText, StringComparison.Ordinal);
        Assert.False(panel.Live.IsRunning);
    }

    [Fact]
    public void Steps_are_batched_into_one_ui_update()
    {
        var scheduler = new ManualUiScheduler();
        var live = new LiveRunViewModel(scheduler);
        live.Begin(Job());

        for (var i = 0; i < 200; i++)
        {
            var at = Start.AddSeconds(i);
            live.Enqueue(new RunStep(at, [Sent($"c-{i:D3}", at)], [], [new PluginEvent("p", "throttled", new JsonObject())]));
        }

        Assert.Single(scheduler.Posted);
        Assert.Equal(1, scheduler.RunAll());
        Assert.Equal(1, live.FlushCount);
        Assert.Equal(200, live.StepCount);
        Assert.Equal(200, live.Rows.Count);
        Assert.Equal(200, live.Throttled);
    }

    [Fact]
    public void Rows_turn_late_past_the_deadline_and_dead_lettered_on_a_dead_letter_queue()
    {
        var live = new LiveRunViewModel(InlineUiScheduler.Instance);
        live.Begin(Job());
        live.Enqueue(new RunStep(Start, [Sent("slow", Start, "PT5M"), Sent("poison", Start, "PT5M"), Sent("quick", Start, "PT5M")], [], []));
        live.Enqueue(new RunStep(Start.AddMinutes(1), [], [Seen("poison", "work.q/$deadletterqueue", 1), Seen("quick", "done.q", 1)], []));
        live.Enqueue(new RunStep(Start.AddMinutes(6), [], [], []));

        var rows = live.AllRows.ToDictionary(r => r.CorrelationId);
        Assert.Equal(LiveStatus.Late, rows["slow"].Status);
        Assert.Equal(LiveStatus.DeadLettered, rows["poison"].Status);
        Assert.Equal("+1m0s done.q", rows["quick"].ActualText);
        Assert.Equal("1m0s", rows["quick"].LatencyText);
        Assert.Equal(1, live.LateCount);

        live.Filter = LiveFilter.Late;
        Assert.Equal(["slow"], live.Rows.Select(r => r.CorrelationId));
        live.Filter = LiveFilter.All;
        Assert.Equal(3, live.Rows.Count);
    }

    [Fact]
    public void Filter_labels_read_like_the_status_column()
    {
        Assert.Equal(
            ["All", "Pending", "Passed", "Failed", "Dead-lettered", "Late"],
            Enum.GetValues<LiveFilter>().Select(LiveRunViewModel.FilterLabel));
    }

    [Fact]
    public void Final_status_comes_from_the_verdict()
    {
        var live = new LiveRunViewModel(InlineUiScheduler.Instance);
        var job = Job();
        live.Begin(job);
        live.Enqueue(new RunStep(Start, [Sent("ok", Start, "PT5M"), Sent("dlq", Start, "PT5M"), Sent("bad", Start, "PT5M"), Sent("tardy", Start, "PT5M")], [], []));

        CorrelationRecord Record(string id, VerdictStatus status, params Observation[] seen) =>
            new(id, Start, [new JsonObject { ["within"] = "PT5M" }], seen, new Verdict(status, $"{id} reason"));
        var result = new RunResult(job,
            [
                Record("ok", VerdictStatus.Pass, Seen("ok", "done.q", 1)),
                Record("dlq", VerdictStatus.Pass, Seen("dlq", "work.q/$deadletterqueue", 2)),
                Record("bad", VerdictStatus.Fail, Seen("bad", "done.q", 1)),
                Record("tardy", VerdictStatus.Fail, Seen("tardy", "done.q", 9)),
            ],
            4, 8, new Dictionary<string, string>(), Start.AddMinutes(9), new Dictionary<string, int> { ["p.retry"] = 3 });

        live.Complete(result);

        var rows = live.AllRows.ToDictionary(r => r.CorrelationId);
        Assert.Equal(LiveStatus.Passed, rows["ok"].Status);
        Assert.Equal(LiveStatus.DeadLettered, rows["dlq"].Status);
        Assert.Equal(LiveStatus.Failed, rows["bad"].Status);
        Assert.Equal(LiveStatus.Late, rows["tardy"].Status);
        Assert.Equal("bad reason", rows["bad"].Reason);
        Assert.Equal(3, live.Retries);
        Assert.False(live.IsRunning);
        Assert.StartsWith("Failed:", live.Summary, StringComparison.Ordinal);
    }

    private static JobDefinition Job() => new(
        "demo", "live", "/tmp/live.job.yaml", "/tmp", "local", JobLoader.InMemoryProvider, 1,
        Start, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), "work.q", [], new Dictionary<string, string>());

    private static SentRecord Sent(string id, DateTimeOffset at, string? within = null) =>
        new(id, at, [within is null ? new JsonObject { ["description"] = "answered" } : new JsonObject { ["description"] = "answered", ["within"] = within }]);

    private static Observation Seen(string id, string sink, int minutes) => new(sink, id, Start.AddMinutes(minutes), new JsonObject());
}
